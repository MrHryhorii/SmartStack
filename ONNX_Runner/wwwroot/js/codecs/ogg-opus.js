import {
    createWebAudioSession,
    queueAudioChannels,
    scheduleReplay
} from './web-audio.js';

import {
    loadCodecLibrary
} from './script-loader.js';

const OPUS_SAMPLE_RATES = new Set([
    8000,
    12000,
    16000,
    24000,
    48000
]);

// Resolves the Ogg Opus decoder exported by the local UMD bundle.
async function loadOggOpusDecoder() {
    return loadCodecLibrary(
        './lib/ogg-opus-decoder.min.js',
        () => window['ogg-opus-decoder']?.OggOpusDecoder,
        'OggOpusDecoder'
    );
}

// Queues decoded Opus PCM when the decoder produced audio samples.
function queueDecodedResult(
    session,
    decoded
) {
    if (
        !decoded?.samplesDecoded ||
        !decoded?.sampleRate
    ) {
        return;
    }

    queueAudioChannels(
        session,
        decoded.channelData,
        decoded.sampleRate,
        decoded.samplesDecoded
    );
}

// Streams Ogg/Opus through the WebAssembly decoder when native MSE is unavailable.
export async function streamOggOpus({
    engine,
    reader,
    mimeType,
    sampleRate,
    player,
    onChunk,
    onComplete
}) {
    const OggOpusDecoder =
        await loadOggOpusDecoder();

    // Opus decoders support a fixed set of PCM output rates.
    const outputSampleRate =
        OPUS_SAMPLE_RATES.has(sampleRate)
            ? sampleRate
            : 48000;

    const decoder =
        new OggOpusDecoder({
            sampleRate:
                outputSampleRate,

            // Prevents loading the optional Opus ML enhancement bundle.
            speechQualityEnhancement:
                'none'
        });

    await decoder.ready;

    const session =
        createWebAudioSession(
            engine,
            outputSampleRate,
            player
        );

    const audioChunks = [];

    try {
        while (true) {
            const {
                done,
                value
            } = await reader.read();

            if (done) {
                break;
            }

            audioChunks.push(
                value
            );

            onChunk(
                value.length
            );

            const decoded =
                await decoder.decode(
                    value
                );

            queueDecodedResult(
                session,
                decoded
            );
        }

        // Flush buffered Ogg/Opus data after the final transport chunk.
        const flushed =
            await decoder.flush();

        queueDecodedResult(
            session,
            flushed
        );

        const finalBlob =
            new Blob(
                audioChunks,
                {
                    type:
                        mimeType ||
                        'audio/ogg; codecs=opus'
                }
            );

        // Network completion is independent from queued playback completion.
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
    } finally {
        decoder.free();
    }
}