import {
    createWebAudioSession,
    queueAudioChannels,
    scheduleReplay
} from './web-audio.js';

let decoderLoader = null;

// Loads the local mpg123 WebAssembly decoder only when the fallback is selected.
async function loadMPEGDecoder() {
    const globalName =
        'mpg123-decoder';

    const existing =
        window[globalName]
            ?.MPEGDecoder;

    if (existing) {
        return existing;
    }

    if (!decoderLoader) {
        decoderLoader =
            new Promise(
                (
                    resolve,
                    reject
                ) => {
                    const script =
                        document.createElement(
                            'script'
                        );

                    script.src =
                        new URL(
                            './lib/mpg123-decoder.min.js',
                            import.meta.url
                        ).href;

                    script.charset =
                        'UTF-8';

                    script.async =
                        true;

                    script.onload =
                        () => {
                            const MPEGDecoder =
                                window[
                                    globalName
                                ]?.MPEGDecoder;

                            if (!MPEGDecoder) {
                                decoderLoader =
                                    null;

                                reject(
                                    new Error(
                                        'mpg123-decoder loaded but MPEGDecoder was not exposed.'
                                    )
                                );

                                return;
                            }

                            resolve(
                                MPEGDecoder
                            );
                        };

                    script.onerror =
                        () => {
                            decoderLoader =
                                null;

                            reject(
                                new Error(
                                    'Failed to load local mpg123-decoder.min.js.'
                                )
                            );
                        };

                    document.head
                        .appendChild(
                            script
                        );
                }
            );
    }

    return decoderLoader;
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
                    finalBlob
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