import {
    createWebAudioSession,
    queueAudioChannels,
    scheduleReplay
} from './web-audio.js';

// Streams raw PCM S16LE through Web Audio without codec decoding.
export async function streamPCM({
    engine,
    reader,
    sampleRate,
    player,
    onChunk,
    onComplete
}) {
    const session =
        createWebAudioSession(
            engine,
            sampleRate,
            player
        );

    const audioChunks = [];
    let leftover =
        new Uint8Array(0);

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
                            'audio/pcm'
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
                finalBlob,
                async blob => {
                    const arrayBuffer =
                        await blob.arrayBuffer();

                    return addWavHeader(
                        arrayBuffer,
                        sampleRate
                    );
                }
            );

            return;
        }

        audioChunks.push(
            value
        );

        onChunk(
            value.length
        );

        const combined =
            new Uint8Array(
                leftover.length +
                value.length
            );

        combined.set(
            leftover
        );

        combined.set(
            value,
            leftover.length
        );

        const evenLength =
            combined.length -
            (combined.length % 2);

        leftover =
            combined.slice(
                evenLength
            );

        if (
            evenLength === 0
        ) {
            continue;
        }

        const dataView =
            new DataView(
                combined.buffer,
                combined.byteOffset,
                evenLength
            );

        const numSamples =
            evenLength / 2;

        const float32Array =
            new Float32Array(
                numSamples
            );

        for (
            let i = 0;
            i < numSamples;
            i++
        ) {
            const int16 =
                dataView.getInt16(
                    i * 2,
                    true
                );

            float32Array[i] =
                int16 < 0
                    ? int16 / 32768.0
                    : int16 / 32767.0;
        }

        queueAudioChannels(
            session,
            [float32Array],
            sampleRate,
            numSamples
        );
    }
}

// Wraps raw mono PCM S16LE in a standard RIFF/WAVE header.
export function addWavHeader(
    pcmArrayBuffer,
    sampleRate
) {
    const numChannels = 1;
    const bitsPerSample = 16;

    const byteRate =
        sampleRate *
        numChannels *
        (bitsPerSample / 8);

    const blockAlign =
        numChannels *
        (bitsPerSample / 8);

    const dataSize =
        pcmArrayBuffer.byteLength;

    const buffer =
        new ArrayBuffer(
            44 + dataSize
        );

    const view =
        new DataView(buffer);

    const writeString = (
        offset,
        string
    ) => {
        for (
            let i = 0;
            i < string.length;
            i++
        ) {
            view.setUint8(
                offset + i,
                string.charCodeAt(i)
            );
        }
    };

    writeString(
        0,
        'RIFF'
    );

    view.setUint32(
        4,
        36 + dataSize,
        true
    );

    writeString(
        8,
        'WAVE'
    );

    writeString(
        12,
        'fmt '
    );

    view.setUint32(
        16,
        16,
        true
    );

    view.setUint16(
        20,
        1,
        true
    );

    view.setUint16(
        22,
        numChannels,
        true
    );

    view.setUint32(
        24,
        sampleRate,
        true
    );

    view.setUint32(
        28,
        byteRate,
        true
    );

    view.setUint16(
        32,
        blockAlign,
        true
    );

    view.setUint16(
        34,
        bitsPerSample,
        true
    );

    writeString(
        36,
        'data'
    );

    view.setUint32(
        40,
        dataSize,
        true
    );

    new Uint8Array(
        buffer,
        44
    ).set(
        new Uint8Array(
            pcmArrayBuffer
        )
    );

    return new Blob(
        [buffer],
        {
            type:
                'audio/wav'
        }
    );
}