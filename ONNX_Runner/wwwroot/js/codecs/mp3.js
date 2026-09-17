// MP3 fallback: incrementally decodes audio with the local mpg123 bundle when MSE is unavailable.
import {
    createWebAudioSession,
    queueAudioChannels,
    flushQueuedAudio,
    scheduleReplay
} from './web-audio.js';

import {
    loadCodecLibrary
} from './script-loader.js';

// Resolves the mpg123 decoder exported by the local UMD bundle.
async function loadMPEGDecoder() {
    return loadCodecLibrary(
        './lib/mpg123-decoder.min.js',
        () => window['mpg123-decoder']?.MPEGDecoder,
        'MPEGDecoder'
    );
}

// Streams MP3 through mpg123 when native audio/mpeg MSE is unavailable.
export async function streamMP3({
    engine,
    reader,
    sampleRate,
    player,
    onChunk,
    onComplete
}) {
    const MPEGDecoder =
        await loadMPEGDecoder();

    const decoder =
        new MPEGDecoder();

    await decoder.ready;

    const session =
        createWebAudioSession(
            engine,
            sampleRate,
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
                flushQueuedAudio(
            session
        );

        const finalBlob =
                    new Blob(
                        audioChunks,
                        {
                            type:
                                'audio/mpeg'
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

                return;
            }

            audioChunks.push(
                value
            );

            onChunk(
                value.length
            );

            const decoded =
                decoder.decode(
                    value
                );

            if (
                !decoded.samplesDecoded ||
                !decoded.sampleRate
            ) {
                continue;
            }

            queueAudioChannels(
                session,
                decoded.channelData,
                decoded.sampleRate,
                decoded.samplesDecoded
            );
        }
    } finally {
        decoder.free();
    }
}