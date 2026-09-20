// AAC live playback for Tsubaki's MPEG-4 AAC-LC / ADTS output.
//
// Preferred paths:
// 1. Remux ADTS access units to tiny fragmented-MP4 segments and feed native MSE.
//    This does not transcode AAC and fixes Firefox, which can decode AAC in MP4 MSE
//    but still does not accept raw audio/aac as an MSE SourceBuffer.
// 2. Native WebCodecs AudioDecoder with complete ADTS frames.
// 3. Buffer the original ADTS response and use normal <audio> playback.

import {
    createWebAudioSession,
    queueAudioChannels,
    scheduleReplay
} from './web-audio.js';

import {
    AAC_MP4_MIME,
    supportsAacMp4Mse,
    createAacMp4InitSegment,
    createAacMp4Fragment,
    adtsPayload
} from './aac-mp4.js?v=aac2';

const AAC_LC_CODEC = 'mp4a.40.2';
const AAC_SAMPLES_PER_RAW_BLOCK = 1024;
const MP4_FRAGMENT_TARGET_SECONDS = 0.140;

const AAC_SAMPLE_RATES = [
    96000,
    88200,
    64000,
    48000,
    44100,
    32000,
    24000,
    22050,
    16000,
    12000,
    11025,
    8000,
    7350
];

export async function streamAAC({
    engine,
    reader,
    mimeType,
    sampleRate,
    player,
    onChunk,
    onComplete
}) {
    const receivedChunks = [];
    let pending = new Uint8Array(0);

    let session = null;
    let decoder = null;
    let decoderError = null;
    let mode = 'undecided';

    let streamSampleRate =
        normalizeSampleRate(sampleRate);

    let streamChannels = 1;
    let decodedSampleOffset = 0;

    let mp4State = null;

    const ensureWebAudioSession = () => {
        if (session) {
            return true;
        }

        try {
            session =
                createWebAudioSession(
                    engine,
                    streamSampleRate,
                    player
                );

            return true;
        } catch (error) {
            console.warn(
                `[AudioEngine] Web Audio unavailable for AAC live playback (${error.message}).`
            );

            return false;
        }
    };

    const closeWebCodecsPath = async () => {
        if (
            decoder &&
            decoder.state !== 'closed'
        ) {
            decoder.close();
        }

        decoder = null;

        if (
            session?.audioContext &&
            session.audioContext.state !== 'closed'
        ) {
            await session.audioContext
                .close()
                .catch(() => { });
        }

        if (
            engine.activeAudioContext ===
            session?.audioContext
        ) {
            engine.activeAudioContext = null;
        }

        session = null;
    };

    const closeMp4Path = () => {
        if (!mp4State) {
            return;
        }

        if (
            mp4State.mediaSource.readyState === 'open'
        ) {
            try {
                mp4State.mediaSource.endOfStream();
            } catch {
                // A failed SourceBuffer may already have closed the MediaSource.
            }
        }

        mp4State = null;
    };

    const switchToBufferedMode = async error => {
        if (mode === 'buffered') {
            return;
        }

        mode = 'buffered';

        if (error) {
            console.warn(
                `[AudioEngine] AAC live playback unavailable (${error.message}); buffering for normal playback.`
            );
        }

        closeMp4Path();
        await closeWebCodecsPath();
        await engine.releasePreparedPlayback?.();
    };

    const appendSourceBuffer = (
        sourceBuffer,
        bytes
    ) =>
        new Promise((resolve, reject) => {
            const cleanup = () => {
                sourceBuffer.removeEventListener(
                    'updateend',
                    onUpdateEnd
                );

                sourceBuffer.removeEventListener(
                    'error',
                    onError
                );
            };

            const onUpdateEnd = () => {
                cleanup();
                resolve();
            };

            const onError = () => {
                cleanup();
                reject(
                    new Error(
                        'MediaSource failed to append an AAC/fMP4 segment.'
                    )
                );
            };

            sourceBuffer.addEventListener(
                'updateend',
                onUpdateEnd,
                { once: true }
            );

            sourceBuffer.addEventListener(
                'error',
                onError,
                { once: true }
            );

            try {
                sourceBuffer.appendBuffer(bytes);
            } catch (error) {
                cleanup();
                reject(error);
            }
        });

    const configureMp4Mse = async frameInfo => {
        if (
            frameInfo.audioObjectType !== 2 ||
            frameInfo.channelCount !== 1 ||
            frameInfo.rawDataBlockCount !== 1 ||
            !supportsAacMp4Mse(
                frameInfo.sampleRate,
                frameInfo.channelCount
            )
        ) {
            return false;
        }

        await engine.releasePreparedPlayback?.();

        const mediaSource =
            new MediaSource();

        player.srcObject = null;
        player.src =
            URL.createObjectURL(
                mediaSource
            );

        await new Promise((resolve, reject) => {
            const onOpen = () => {
                cleanup();
                resolve();
            };

            const onError = () => {
                cleanup();
                reject(
                    new Error(
                        'MediaSource could not be opened for AAC playback.'
                    )
                );
            };

            const cleanup = () => {
                mediaSource.removeEventListener(
                    'sourceopen',
                    onOpen
                );

                mediaSource.removeEventListener(
                    'sourceended',
                    onError
                );
            };

            mediaSource.addEventListener(
                'sourceopen',
                onOpen,
                { once: true }
            );

            mediaSource.addEventListener(
                'sourceended',
                onError,
                { once: true }
            );
        });

        let sourceBuffer;

        try {
            sourceBuffer =
                mediaSource.addSourceBuffer(
                    AAC_MP4_MIME
                );
        } catch (error) {
            return false;
        }

        const initSegment =
            createAacMp4InitSegment(
                frameInfo.sampleRate,
                frameInfo.channelCount
            );

        await appendSourceBuffer(
            sourceBuffer,
            initSegment
        );

        mp4State = {
            mediaSource,
            sourceBuffer,
            sequenceNumber: 1,
            baseDecodeTime: 0,
            pendingSamples: [],
            framesPerFragment:
                Math.max(
                    1,
                    Math.round(
                        frameInfo.sampleRate *
                        MP4_FRAGMENT_TARGET_SECONDS /
                        AAC_SAMPLES_PER_RAW_BLOCK
                    )
                ),
            playbackStarted: false
        };

        console.info(
            '[AudioEngine] AAC live playback: fragmented MP4 / MediaSource.'
        );

        return true;
    };

    const flushMp4Fragment = async force => {
        if (!mp4State) {
            return;
        }

        if (
            mp4State.pendingSamples.length === 0 ||
            (
                !force &&
                mp4State.pendingSamples.length <
                    mp4State.framesPerFragment
            )
        ) {
            return;
        }

        const samples =
            mp4State.pendingSamples.splice(
                0,
                force
                    ? mp4State.pendingSamples.length
                    : mp4State.framesPerFragment
            );

        const fragment =
            createAacMp4Fragment(
                mp4State.sequenceNumber,
                mp4State.baseDecodeTime,
                samples
            );

        await appendSourceBuffer(
            mp4State.sourceBuffer,
            fragment
        );

        mp4State.sequenceNumber++;

        for (const sample of samples) {
            mp4State.baseDecodeTime +=
                sample.duration;
        }

        if (!mp4State.playbackStarted) {
            mp4State.playbackStarted = true;

            player.play().catch(
                error =>
                    console.warn(
                        `Autoplay blocked: ${error.message}`
                    )
            );
        }
    };

    const queueMp4Frame = async frameInfo => {
        mp4State.pendingSamples.push({
            data:
                adtsPayload(
                    frameInfo.frame,
                    frameInfo.headerLength
                ),
            duration:
                AAC_SAMPLES_PER_RAW_BLOCK
        });

        await flushMp4Fragment(false);
    };

    const configureNativeDecoder = async frameInfo => {
        if (
            typeof AudioDecoder === 'undefined' ||
            typeof EncodedAudioChunk === 'undefined'
        ) {
            return false;
        }

        if (frameInfo.audioObjectType !== 2) {
            return false;
        }

        streamSampleRate =
            frameInfo.sampleRate;

        streamChannels =
            frameInfo.channelCount;

        const config = {
            codec: AAC_LC_CODEC,
            sampleRate:
                streamSampleRate,
            numberOfChannels:
                streamChannels
        };

        try {
            const support =
                await AudioDecoder
                    .isConfigSupported(
                        config
                    );

            if (!support.supported) {
                return false;
            }
        } catch {
            // configure() below remains authoritative.
        }

        if (!ensureWebAudioSession()) {
            return false;
        }

        decoderError = null;

        decoder =
            new AudioDecoder({
                output:
                    audioData => {
                        try {
                            const channels =
                                copyAudioData(
                                    audioData
                                );

                            queueAudioChannels(
                                session,
                                channels,
                                audioData.sampleRate,
                                audioData.numberOfFrames
                            );
                        } catch (error) {
                            decoderError =
                                decoderError ||
                                error;
                        } finally {
                            audioData.close();
                        }
                    },

                error:
                    error => {
                        decoderError =
                            decoderError ||
                            error;
                    }
            });

        try {
            decoder.configure(config);

            console.info(
                '[AudioEngine] AAC live playback: WebCodecs / Web Audio.'
            );

            return true;
        } catch (error) {
            decoderError = error;
            decoder.close();
            decoder = null;
            return false;
        }
    };

    const decodeFrame = async frameInfo => {
        if (
            !decoder ||
            decoder.state !== 'configured'
        ) {
            throw new Error(
                'AAC decoder is not configured.'
            );
        }

        const frameSamples =
            AAC_SAMPLES_PER_RAW_BLOCK *
            frameInfo.rawDataBlockCount;

        const timestamp =
            Math.round(
                decodedSampleOffset *
                1_000_000 /
                streamSampleRate
            );

        const duration =
            Math.round(
                frameSamples *
                1_000_000 /
                streamSampleRate
            );

        decoder.decode(
            new EncodedAudioChunk({
                type: 'key',
                timestamp,
                duration,
                data: frameInfo.frame
            })
        );

        decodedSampleOffset +=
            frameSamples;

        // Apply backpressure only if the native decoder queue grows unusually large.
        // Normal live playback does not flush on every network chunk.
        if (decoder.decodeQueueSize > 16) {
            await decoder.flush();

            if (decoderError) {
                throw decoderError;
            }
        }
    };

    const processFrame = async frameInfo => {
        if (mode === 'buffered') {
            return;
        }

        if (mode === 'undecided') {
            streamSampleRate =
                frameInfo.sampleRate;

            streamChannels =
                frameInfo.channelCount;

            if (
                frameInfo.audioObjectType !== 2 ||
                streamChannels !== 1
            ) {
                await switchToBufferedMode(
                    new Error(
                        'The stream is outside the Tsubaki AAC-LC mono live subset.'
                    )
                );

                return;
            }

            try {
                const mseConfigured =
                    await configureMp4Mse(
                        frameInfo
                    );

                if (mseConfigured) {
                    mode = 'mse-mp4';
                }
            } catch (error) {
                console.warn(
                    `[AudioEngine] AAC fragmented-MP4 MSE setup failed (${error.message}).`
                );
            }

            if (mode === 'undecided') {
                const configured =
                    await configureNativeDecoder(
                        frameInfo
                    );

                if (configured) {
                    mode = 'native';
                } else {
                    await switchToBufferedMode(
                        new Error(
                            'Neither AAC fragmented-MP4 MSE nor AAC WebCodecs is available.'
                        )
                    );

                    return;
                }
            }
        }

        if (mode === 'mse-mp4') {
            await queueMp4Frame(frameInfo);
            return;
        }

        if (mode === 'native') {
            await decodeFrame(frameInfo);
        }
    };

    const drainFrames = async final => {
        while (pending.length > 0) {
            const parsed =
                tryTakeAdtsFrame(
                    pending
                );

            if (!parsed) {
                if (final) {
                    throw new Error(
                        'Incomplete AAC ADTS frame at end of stream.'
                    );
                }

                break;
            }

            pending =
                parsed.remainder;

            await processFrame(parsed);
        }
    };

    while (true) {
        const {
            done,
            value
        } = await reader.read();

        if (done) {
            break;
        }

        receivedChunks.push(value);
        onChunk(value.length);

        if (mode === 'buffered') {
            continue;
        }

        pending =
            concatBytes(
                pending,
                value
            );

        try {
            await drainFrames(false);
        } catch (error) {
            await switchToBufferedMode(error);
        }
    }

    if (mode !== 'buffered') {
        try {
            await drainFrames(true);

            if (mode === 'mse-mp4') {
                await flushMp4Fragment(true);

                if (
                    mp4State?.mediaSource.readyState === 'open'
                ) {
                    mp4State.mediaSource.endOfStream();
                }
            }

            if (
                mode === 'native' &&
                decoder &&
                decoder.state === 'configured'
            ) {
                await decoder.flush();
            }

            if (decoderError) {
                throw decoderError;
            }
        } catch (error) {
            await switchToBufferedMode(error);
        }
    }

    const finalBlob =
        new Blob(
            receivedChunks,
            {
                type:
                    mimeType ||
                    'audio/aac'
            }
        );

    if (
        mode === 'mse-mp4' &&
        mp4State
    ) {
        const bufferedDuration =
            mp4State.sourceBuffer.buffered.length > 0
                ? mp4State.sourceBuffer.buffered.end(
                    mp4State.sourceBuffer.buffered.length - 1
                )
                : NaN;

        await onComplete(
            finalBlob,
            Number.isFinite(bufferedDuration) &&
                bufferedDuration > 0
                ? bufferedDuration
                : null
        );

        return;
    }

    if (
        mode === 'native' &&
        session
    ) {
        if (
            decoder &&
            decoder.state !== 'closed'
        ) {
            decoder.close();
        }

        await onComplete(
            finalBlob,
            session.totalDuration
        );

        scheduleReplay(
            engine,
            session,
            player,
            finalBlob
        );

        return;
    }

    await onComplete(finalBlob);

    engine.setReplaySource(
        player,
        finalBlob
    );

    player.play().catch(
        error =>
            console.warn(
                `Autoplay blocked: ${error.message}`
            )
    );
}

function tryTakeAdtsFrame(bytes) {
    if (bytes.length < 7) {
        return null;
    }

    if (
        bytes[0] !== 0xFF ||
        (bytes[1] & 0xF6) !== 0xF0
    ) {
        throw new Error(
            'Invalid AAC ADTS sync word or header.'
        );
    }

    const protectionAbsent =
        bytes[1] & 0x01;

    const headerLength =
        protectionAbsent
            ? 7
            : 9;

    if (bytes.length < headerLength) {
        return null;
    }

    const audioObjectType =
        ((bytes[2] >> 6) & 0x03) + 1;

    const sampleRateIndex =
        (bytes[2] >> 2) & 0x0F;

    const sampleRate =
        AAC_SAMPLE_RATES[
            sampleRateIndex
        ];

    if (!sampleRate) {
        throw new Error(
            `Unsupported AAC sample-rate index: ${sampleRateIndex}.`
        );
    }

    const channelCount =
        (
            ((bytes[2] & 0x01) << 2) |
            ((bytes[3] >> 6) & 0x03)
        );

    if (channelCount <= 0) {
        throw new Error(
            'AAC Program Config Element channel layouts are not supported by the dashboard.'
        );
    }

    const frameLength =
        (
            ((bytes[3] & 0x03) << 11) |
            (bytes[4] << 3) |
            (bytes[5] >> 5)
        );

    if (frameLength < headerLength) {
        throw new Error(
            `Invalid AAC ADTS frame length: ${frameLength}.`
        );
    }

    if (bytes.length < frameLength) {
        return null;
    }

    const rawDataBlockCount =
        (bytes[6] & 0x03) + 1;

    return {
        audioObjectType,
        sampleRate,
        channelCount,
        rawDataBlockCount,
        headerLength,
        frame:
            bytes.slice(
                0,
                frameLength
            ),
        remainder:
            bytes.slice(
                frameLength
            )
    };
}

function copyAudioData(audioData) {
    const channels = [];

    for (
        let channel = 0;
        channel < audioData.numberOfChannels;
        channel++
    ) {
        const samples =
            new Float32Array(
                audioData.numberOfFrames
            );

        audioData.copyTo(
            samples,
            {
                planeIndex: channel,
                format: 'f32-planar'
            }
        );

        channels.push(samples);
    }

    return channels;
}

function concatBytes(left, right) {
    if (!left.length) {
        return right.slice();
    }

    if (!right.length) {
        return left;
    }

    const combined =
        new Uint8Array(
            left.length +
            right.length
        );

    combined.set(left, 0);
    combined.set(right, left.length);

    return combined;
}

function normalizeSampleRate(sampleRate) {
    const value = Number(sampleRate);

    return (
        Number.isFinite(value) &&
        value > 0
    )
        ? value
        : 22050;
}
