// FLAC live playback: native WebCodecs first, Tsubaki's tiny decoder second, full buffering last.
import {
    createWebAudioSession,
    queueAudioChannels,
    flushQueuedAudio,
    scheduleReplay
} from './web-audio.js';

import {
    tryExtractFlacMetadata,
    findFlacFrameStart,
    parseFlacFrameHeader,
    concatBytes,
    createFinalFlacBlob
} from './flac-format.js';

import {
    decodeTsubakiFlacFrame
} from './flac-tiny.js';

export async function streamFLAC({
    engine,
    reader,
    sampleRate,
    player,
    onChunk,
    onComplete
}) {
    const receivedChunks = [];
    let pending = new Uint8Array(0);
    let metadata = null;
    let session = null;
    let nativeDecoder = null;
    let nativeError = null;
    let nativeProbeOutput = [];
    let mode = 'undecided';
    let expectedFrameNumber = 0;
    let decodedSampleOffset = 0;
    let streamSampleRate = sampleRate;

    const ensureWebAudioSession = () => {
        if (session) {
            return true;
        }

        try {
            session = createWebAudioSession(
                engine,
                streamSampleRate,
                player
            );
            return true;
        } catch (error) {
            console.warn(
                `[AudioEngine] Web Audio unavailable for FLAC live playback (${error.message}).`
            );
            return false;
        }
    };

    const closeLiveSession = async () => {
        nativeDecoder?.close();
        nativeDecoder = null;
        nativeProbeOutput = [];

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

        engine.deactivateLivePlaybackControl?.(
            player
        );

        session = null;
    };

    const fallbackToBufferedPlayback = async () => {
        mode = 'buffered';
        await closeLiveSession();

        while (true) {
            const { done, value } =
                await reader.read();

            if (done) {
                break;
            }

            receivedChunks.push(value);
            onChunk(value.length);
        }

        // A streamed FLAC header has totalSamples=0. Rebuild only the completed
        // download/replay Blob so desktop players can show duration and seek.
        const finalBlob =
            createFinalFlacBlob(
                receivedChunks
            );

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
    };

    const configureNativeDecoder = async () => {
        if (
            typeof AudioDecoder === 'undefined' ||
            typeof EncodedAudioChunk === 'undefined'
        ) {
            return false;
        }

        if (!ensureWebAudioSession()) {
            return false;
        }

        const config = {
            codec: 'flac',
            sampleRate: streamSampleRate,
            numberOfChannels: metadata.channels,
            description: metadata.description
        };

        // Some browsers have returned false negatives for FLAC capability probes.
        // configure() plus one real frame is the deciding test.
        try {
            await AudioDecoder.isConfigSupported(config);
        } catch {
            // The real configure/decode probe below remains authoritative.
        }

        nativeDecoder = new AudioDecoder({
            output: audioData => {
                try {
                    const channels = copyAudioData(audioData);

                    if (mode === 'native-probe') {
                        nativeProbeOutput.push({
                            channels,
                            sampleRate: audioData.sampleRate,
                            frameCount: audioData.numberOfFrames
                        });
                        return;
                    }

                    queueAudioChannels(
                        session,
                        channels,
                        audioData.sampleRate,
                        audioData.numberOfFrames
                    );
                } catch (error) {
                    nativeError = error;
                } finally {
                    audioData.close();
                }
            },
            error: error => {
                nativeError = error;
            }
        });

        try {
            nativeDecoder.configure(config);
            return true;
        } catch (error) {
            nativeError = error;
            nativeDecoder.close();
            nativeDecoder = null;
            return false;
        }
    };

    const queueTinyFrame = (frame, frameInfo) => {
        if (!ensureWebAudioSession()) {
            throw new Error(
                'Web Audio is unavailable for the Tsubaki FLAC fallback.'
            );
        }

        const samples =
            decodeTsubakiFlacFrame(
                frame,
                frameInfo
            );

        queueAudioChannels(
            session,
            [samples],
            streamSampleRate,
            samples.length
        );
    };

    const probeNativeWithFrame = async (
        frame,
        frameInfo
    ) => {
        mode = 'native-probe';
        nativeError = null;
        nativeProbeOutput = [];

        try {
            decodeNativeFrame(
                nativeDecoder,
                frame,
                frameInfo,
                decodedSampleOffset,
                streamSampleRate
            );

            await nativeDecoder.flush();

            if (nativeError) {
                throw nativeError;
            }

            for (const output of nativeProbeOutput) {
                queueAudioChannels(
                    session,
                    output.channels,
                    output.sampleRate,
                    output.frameCount
                );
            }

            nativeProbeOutput = [];
            mode = 'native';
            return true;
        } catch (error) {
            console.warn(
                `[AudioEngine] Native FLAC decoding unavailable (${error.message}); using Tsubaki JS fallback.`
            );

            nativeProbeOutput = [];
            nativeDecoder?.close();
            nativeDecoder = null;
            nativeError = null;
            mode = 'tiny';
            return false;
        }
    };

    const processFrame = async (
        frame,
        frameInfo
    ) => {
        if (mode === 'undecided') {
            const nativeConfigured =
                await configureNativeDecoder();

            if (nativeConfigured) {
                const nativeWorked =
                    await probeNativeWithFrame(
                        frame,
                        frameInfo
                    );

                if (nativeWorked) {
                    decodedSampleOffset +=
                        frameInfo.blockSize;
                    expectedFrameNumber++;
                    return;
                }
            } else {
                mode = 'tiny';
            }
        }

        if (mode === 'tiny') {
            queueTinyFrame(
                frame,
                frameInfo
            );
        } else if (mode === 'native') {
            decodeNativeFrame(
                nativeDecoder,
                frame,
                frameInfo,
                decodedSampleOffset,
                streamSampleRate
            );
        } else {
            throw new Error(
                `Invalid FLAC playback mode: ${mode}.`
            );
        }

        decodedSampleOffset +=
            frameInfo.blockSize;
        expectedFrameNumber++;
    };

    const drainFrames = async final => {
        while (pending.length > 0) {
            const current =
                parseFlacFrameHeader(
                    pending,
                    0,
                    expectedFrameNumber
                );

            if (!current) {
                if (final) {
                    throw new Error(
                        'Incomplete or invalid FLAC frame at end of stream.'
                    );
                }

                break;
            }

            const nextOffset =
                findFlacFrameStart(
                    pending,
                    current.headerLength,
                    expectedFrameNumber + 1
                );

            if (nextOffset < 0 && !final) {
                break;
            }

            const frame =
                nextOffset < 0
                    ? pending
                    : pending.slice(0, nextOffset);

            await processFrame(
                frame,
                current
            );

            pending =
                nextOffset < 0
                    ? new Uint8Array(0)
                    : pending.slice(nextOffset);
        }

    };

    while (true) {
        const { done, value } =
            await reader.read();

        if (done) {
            break;
        }

        receivedChunks.push(value);
        onChunk(value.length);

        pending = concatBytes(
            pending,
            value
        );

        if (!metadata) {
            metadata =
                tryExtractFlacMetadata(
                    pending
                );

            if (!metadata) {
                continue;
            }

            streamSampleRate =
                metadata.sampleRate ||
                sampleRate;

            if (
                metadata.channels !== 1 ||
                metadata.bitsPerSample !== 16
            ) {
                console.warn(
                    '[AudioEngine] FLAC stream is outside the Tsubaki mono/PCM16 live subset; buffering instead.'
                );
                await fallbackToBufferedPlayback();
                return;
            }

            pending = metadata.remainder;
        }

        try {
            await drainFrames(false);
        } catch (error) {
            console.warn(
                `[AudioEngine] FLAC live decoding failed (${error.message}); buffering the remaining response.`
            );
            await fallbackToBufferedPlayback();
            return;
        }
    }

    if (!metadata) {
        throw new Error(
            'FLAC stream ended before metadata was complete.'
        );
    }

    try {
        await drainFrames(true);

        if (
            mode === 'native' &&
            nativeDecoder
        ) {
            await nativeDecoder.flush();

            if (nativeError) {
                throw nativeError;
            }
        }
    } catch (error) {
        console.warn(
            `[AudioEngine] Final FLAC frame could not be decoded live (${error.message}); using buffered playback.`
        );
        await fallbackToBufferedPlayback();
        return;
    }

    nativeDecoder?.close();

    // Schedule the final short PCM tail before reporting duration or switching to replay.
    if (session) {
        flushQueuedAudio(
            session
        );
    }

    // Live playback used the original bytes. The completed Blob can now carry
    // the exact sample count required by desktop duration/seek implementations.
    const finalBlob =
        createFinalFlacBlob(
            receivedChunks,
            decodedSampleOffset
        );

    await onComplete(
        finalBlob,
        session?.totalDuration
    );

    if (session) {
        scheduleReplay(
            engine,
            session,
            player,
            finalBlob
        );
    } else {
        engine.setReplaySource(
            player,
            finalBlob
        );
    }
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

function decodeNativeFrame(
    decoder,
    frame,
    frameInfo,
    sampleOffset,
    sampleRate
) {
    const timestamp =
        Math.round(
            sampleOffset *
            1_000_000 /
            sampleRate
        );

    const duration =
        Math.round(
            frameInfo.blockSize *
            1_000_000 /
            sampleRate
        );

    decoder.decode(
        new EncodedAudioChunk({
            type: 'key',
            timestamp,
            duration,
            data: frame
        })
    );
}
