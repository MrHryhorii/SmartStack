// Checks whether the browser accepts the response MIME type through MSE.
export function supportsMSE(
    mimeType
) {
    if (
        !window.MediaSource ||
        !mimeType
    ) {
        return false;
    }

    try {
        return MediaSource
            .isTypeSupported(
                mimeType
            );
    } catch {
        return false;
    }
}

// Streams encoded audio through the browser-native MediaSource pipeline.
export async function streamMSE({
    reader,
    mimeType,
    player,
    onChunk,
    onComplete
}) {
    const mediaSource =
        new MediaSource();

    player.srcObject = null;

    player.src =
        URL.createObjectURL(
            mediaSource
        );

    const audioChunks = [];

    await new Promise(
        resolve =>
            mediaSource
                .addEventListener(
                    'sourceopen',
                    resolve,
                    {
                        once: true
                    }
                )
    );

    const sourceBuffer =
        mediaSource
            .addSourceBuffer(
                mimeType
            );

    let isFirstChunk = true;

    const appendChunk =
        chunk =>
            new Promise(
                (
                    resolve,
                    reject
                ) => {
                    const onUpdateEnd =
                        () => {
                            cleanup();
                            resolve();
                        };

                    const onError =
                        () => {
                            cleanup();

                            reject(
                                new Error(
                                    'MediaSource failed to append an audio chunk.'
                                )
                            );
                        };

                    const cleanup =
                        () => {
                            sourceBuffer
                                .removeEventListener(
                                    'updateend',
                                    onUpdateEnd
                                );

                            sourceBuffer
                                .removeEventListener(
                                    'error',
                                    onError
                                );
                        };

                    sourceBuffer
                        .addEventListener(
                            'updateend',
                            onUpdateEnd,
                            {
                                once: true
                            }
                        );

                    sourceBuffer
                        .addEventListener(
                            'error',
                            onError,
                            {
                                once: true
                            }
                        );

                    sourceBuffer
                        .appendBuffer(
                            chunk
                        );
                }
            );

    while (true) {
        const {
            done,
            value
        } = await reader.read();

        if (done) {
            const bufferedDuration =
                sourceBuffer.buffered.length > 0
                    ? sourceBuffer.buffered.end(
                        sourceBuffer.buffered.length - 1
                    )
                    : NaN;

            if (
                mediaSource.readyState ===
                'open'
            ) {
                mediaSource
                    .endOfStream();
            }


            const finalBlob =
                new Blob(
                    audioChunks,
                    {
                        type:
                            mimeType
                    }
                );

            await onComplete(
                finalBlob,
                Number.isFinite(bufferedDuration) &&
                    bufferedDuration > 0
                    ? bufferedDuration
                    : null
            );

            return;
        }

        audioChunks.push(
            value
        );

        onChunk(
            value.length
        );

        await appendChunk(
            value
        );

        if (isFirstChunk) {
            player.play().catch(
                e =>
                    console.warn(
                        `Autoplay blocked: ${e.message}`
                    )
            );

            isFirstChunk =
                false;
        }
    }
}