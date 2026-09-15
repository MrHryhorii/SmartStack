export const AudioEngine = {
    activeAudioContext: null,
    mpegDecoderLoader: null,
    replayObjectUrl: null,
    playbackToken: 0,

    // Stops active Web Audio playback and invalidates delayed replay setup.
    async stopAll() {
        this.playbackToken++;

        if (this.activeAudioContext) {
            await this.activeAudioContext.close();
            this.activeAudioContext = null;
        }

        if (this.replayObjectUrl) {
            URL.revokeObjectURL(this.replayObjectUrl);
            this.replayObjectUrl = null;
        }
    },

    /**
     * Loads the local mpg123 decoder only when the MP3 fallback is required.
     */
    async loadMPEGDecoder() {
        const globalName = 'mpg123-decoder';
        const existing = window[globalName]?.MPEGDecoder;

        if (existing) {
            return existing;
        }

        if (!this.mpegDecoderLoader) {
            this.mpegDecoderLoader = new Promise((resolve, reject) => {
                const script = document.createElement('script');

                script.src = new URL(
                    './lib/mpg123-decoder.min.js',
                    import.meta.url
                ).href;

                script.charset = 'UTF-8';
                script.async = true;

                script.onload = () => {
                    const MPEGDecoder = window[globalName]?.MPEGDecoder;

                    if (!MPEGDecoder) {
                        reject(
                            new Error(
                                'mpg123-decoder loaded but MPEGDecoder was not exposed.'
                            )
                        );

                        return;
                    }

                    resolve(MPEGDecoder);
                };

                script.onerror = () => {
                    this.mpegDecoderLoader = null;

                    reject(
                        new Error(
                            'Failed to load local mpg123-decoder.min.js.'
                        )
                    );
                };

                document.head.appendChild(script);
            });
        }

        return this.mpegDecoderLoader;
    },

    /**
     * Handles raw PCM 16-bit (S16LE) byte streams using the Web Audio API.
     * Network completion is independent from completion of queued playback.
     */
    async streamPCM(
        reader,
        sampleRate,
        player,
        onChunk,
        onComplete
    ) {
        const AudioContextClass =
            window.AudioContext ||
            window.webkitAudioContext;

        const audioContext =
            new AudioContextClass({
                sampleRate
            });

        const playbackToken =
            ++this.playbackToken;

        this.activeAudioContext =
            audioContext;

        const streamDestination =
            audioContext.createMediaStreamDestination();

        player.srcObject =
            streamDestination.stream;

        player.play().catch(
            e => console.warn(`Autoplay blocked: ${e.message}`)
        );

        let nextStartTime =
            audioContext.currentTime;

        const audioChunks = [];
        let leftover = new Uint8Array(0);

        while (true) {
            const { done, value } =
                await reader.read();

            if (done) {
                const finalBlob =
                    new Blob(
                        audioChunks,
                        { type: 'audio/pcm' }
                    );

                // Expose the requested PCM file as soon as the HTTP response is complete.
                await onComplete(finalBlob);

                const remainingSeconds =
                    Math.max(
                        0,
                        nextStartTime - audioContext.currentTime
                    );

                // Replace the live MediaStream with a replayable WAV only after queued PCM finishes.
                setTimeout(
                    async () => {
                        if (
                            this.playbackToken !== playbackToken ||
                            this.activeAudioContext !== audioContext ||
                            audioContext.state === 'closed'
                        ) {
                            return;
                        }

                        try {
                            const arrayBuffer =
                                await finalBlob.arrayBuffer();

                            if (
                                this.playbackToken !== playbackToken ||
                                this.activeAudioContext !== audioContext ||
                                audioContext.state === 'closed'
                            ) {
                                return;
                            }

                            const playableWavBlob =
                                this.addWavHeader(
                                    arrayBuffer,
                                    sampleRate
                                );

                            const replayUrl =
                                URL.createObjectURL(
                                    playableWavBlob
                                );

                            if (
                                this.playbackToken !== playbackToken ||
                                this.activeAudioContext !== audioContext
                            ) {
                                URL.revokeObjectURL(replayUrl);
                                return;
                            }

                            if (this.replayObjectUrl) {
                                URL.revokeObjectURL(
                                    this.replayObjectUrl
                                );
                            }

                            this.replayObjectUrl =
                                replayUrl;

                            this.activeAudioContext =
                                null;

                            player.srcObject =
                                null;

                            player.src =
                                replayUrl;

                            player.load();

                            audioContext
                                .close()
                                .catch(() => { });
                        } catch (error) {
                            console.warn(
                                `[AudioEngine] PCM replay source setup failed: ${error.message}`
                            );
                        }
                    },
                    Math.ceil(
                        remainingSeconds * 1000
                    ) + 50
                );

                break;
            }

            audioChunks.push(value);
            onChunk(value.length);

            const combined =
                new Uint8Array(
                    leftover.length +
                    value.length
                );

            combined.set(leftover);
            combined.set(
                value,
                leftover.length
            );

            const evenLength =
                combined.length -
                (combined.length % 2);

            leftover =
                combined.slice(evenLength);

            const dataView =
                new DataView(
                    combined.buffer,
                    0,
                    evenLength
                );

            const numSamples =
                evenLength / 2;

            const float32Array =
                new Float32Array(numSamples);

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

            if (
                numSamples > 0 &&
                audioContext.state !== 'closed'
            ) {
                const audioBuffer =
                    audioContext.createBuffer(
                        1,
                        numSamples,
                        sampleRate
                    );

                audioBuffer
                    .getChannelData(0)
                    .set(float32Array);

                const source =
                    audioContext.createBufferSource();

                source.buffer =
                    audioBuffer;

                source.connect(
                    streamDestination
                );

                const currentTime =
                    audioContext.currentTime;

                if (
                    nextStartTime <
                    currentTime
                ) {
                    nextStartTime =
                        currentTime;
                }

                source.start(
                    nextStartTime
                );

                nextStartTime +=
                    audioBuffer.duration;
            }
        }
    },

    /**
     * MP3 streaming fallback for browsers without audio/mpeg MSE support.
     * Encoded bytes are kept for download while mpg123 feeds Web Audio incrementally.
     */
    async streamMP3(
        reader,
        expectedSampleRate,
        player,
        onChunk,
        onComplete
    ) {
        const MPEGDecoder =
            await this.loadMPEGDecoder();

        const decoder =
            new MPEGDecoder();

        await decoder.ready;

        const AudioContextClass =
            window.AudioContext ||
            window.webkitAudioContext;

        const audioContext =
            new AudioContextClass({
                sampleRate: expectedSampleRate
            });

        const playbackToken =
            ++this.playbackToken;

        this.activeAudioContext =
            audioContext;

        const streamDestination =
            audioContext.createMediaStreamDestination();

        player.removeAttribute('src');

        player.srcObject =
            streamDestination.stream;

        player.play().catch(
            e => console.warn(`Autoplay blocked: ${e.message}`)
        );

        const audioChunks = [];

        let nextStartTime =
            audioContext.currentTime;

        try {
            while (true) {
                const { done, value } =
                    await reader.read();

                if (done) {
                    break;
                }

                audioChunks.push(value);
                onChunk(value.length);

                const decoded =
                    decoder.decode(value);

                if (
                    !decoded.samplesDecoded ||
                    !decoded.sampleRate ||
                    audioContext.state === 'closed'
                ) {
                    continue;
                }

                const channels =
                    decoded.channelData.filter(
                        channel =>
                            channel?.length
                    );

                if (
                    channels.length === 0
                ) {
                    continue;
                }

                const audioBuffer =
                    audioContext.createBuffer(
                        channels.length,
                        decoded.samplesDecoded,
                        decoded.sampleRate
                    );

                for (
                    let channel = 0;
                    channel < channels.length;
                    channel++
                ) {
                    audioBuffer
                        .getChannelData(channel)
                        .set(
                            channels[channel]
                        );
                }

                const source =
                    audioContext.createBufferSource();

                source.buffer =
                    audioBuffer;

                source.connect(
                    streamDestination
                );

                const currentTime =
                    audioContext.currentTime;

                if (
                    nextStartTime <
                    currentTime
                ) {
                    nextStartTime =
                        currentTime;
                }

                source.start(
                    nextStartTime
                );

                nextStartTime +=
                    audioBuffer.duration;
            }

            const finalBlob =
                new Blob(
                    audioChunks,
                    { type: 'audio/mpeg' }
                );

            // Return after network completion; scheduled Web Audio buffers continue independently.
            await onComplete(finalBlob);

            const remainingSeconds =
                Math.max(
                    0,
                    nextStartTime -
                    audioContext.currentTime
                );

            // Switch to the completed MP3 after live playback so the player can replay and seek it.
            setTimeout(
                async () => {
                    if (
                        this.playbackToken !== playbackToken ||
                        this.activeAudioContext !== audioContext ||
                        audioContext.state === 'closed'
                    ) {
                        return;
                    }

                    const replayUrl =
                        URL.createObjectURL(
                            finalBlob
                        );

                    if (
                        this.playbackToken !== playbackToken ||
                        this.activeAudioContext !== audioContext
                    ) {
                        URL.revokeObjectURL(
                            replayUrl
                        );

                        return;
                    }

                    if (
                        this.replayObjectUrl
                    ) {
                        URL.revokeObjectURL(
                            this.replayObjectUrl
                        );
                    }

                    this.replayObjectUrl =
                        replayUrl;

                    this.activeAudioContext =
                        null;

                    player.srcObject =
                        null;

                    player.src =
                        replayUrl;

                    player.load();

                    audioContext
                        .close()
                        .catch(() => { });
                },
                Math.ceil(
                    remainingSeconds * 1000
                ) + 50
            );
        } finally {
            decoder.free();
        }
    },

    /**
     * Handles standard encoded formats (MP3/Opus) via MediaSource Extensions.
     */
    async streamMSE(
        reader,
        mimeType,
        player,
        onChunk,
        onComplete
    ) {
        const mediaSource =
            new MediaSource();

        player.src =
            URL.createObjectURL(
                mediaSource
            );

        const audioChunks = [];

        await new Promise(
            resolve =>
                mediaSource.addEventListener(
                    'sourceopen',
                    resolve,
                    { once: true }
                )
        );

        const sourceBuffer =
            mediaSource.addSourceBuffer(
                mimeType
            );

        let isFirstChunk = true;

        const appendChunk =
            async (chunk) => {
                return new Promise(
                    (resolve) => {
                        sourceBuffer.addEventListener(
                            'updateend',
                            resolve,
                            { once: true }
                        );

                        sourceBuffer.appendBuffer(
                            chunk
                        );
                    }
                );
            };

        while (true) {
            const { done, value } =
                await reader.read();

            if (done) {
                if (
                    mediaSource.readyState ===
                    'open'
                ) {
                    mediaSource.endOfStream();
                }

                const finalBlob =
                    new Blob(
                        audioChunks,
                        { type: mimeType }
                    );

                await onComplete(
                    finalBlob
                );

                break;
            }

            audioChunks.push(value);
            onChunk(value.length);

            await appendChunk(value);

            if (isFirstChunk) {
                player.play().catch(
                    e =>
                        console.warn(
                            `Autoplay blocked: ${e.message}`
                        )
                );

                isFirstChunk = false;
            }
        }
    },

    /**
     * Converts raw PCM bytes to a playable WAV Blob by injecting a RIFF/WAVE header.
     * This allows native <audio> tags to play non-streamed PCM files.
     */
    addWavHeader(
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
            view,
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
            view,
            0,
            'RIFF'
        );

        view.setUint32(
            4,
            36 + dataSize,
            true
        );

        writeString(
            view,
            8,
            'WAVE'
        );

        writeString(
            view,
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
            view,
            36,
            'data'
        );

        view.setUint32(
            40,
            dataSize,
            true
        );

        // Copy raw PCM data directly after the 44-byte header
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
            { type: 'audio/wav' }
        );
    }
};