export const AudioEngine = {
    activeAudioContext: null,

    // Stops and cleans up Web Audio API instances
    async stopAll() {
        if (this.activeAudioContext) {
            await this.activeAudioContext.close();
            this.activeAudioContext = null;
        }
    },

    /**
     * Handles raw PCM 16-bit (S16LE) byte streams using the Web Audio API.
     */
    async streamPCM(reader, sampleRate, onChunk, onComplete) {
        this.activeAudioContext = new (window.AudioContext || window.webkitAudioContext)({ sampleRate: sampleRate });
        let nextStartTime = this.activeAudioContext.currentTime;
        
        const audioChunks = []; 
        let leftover = new Uint8Array(0);

        while (true) {
            const { done, value } = await reader.read();
            
            if (done) {
                const finalBlob = new Blob(audioChunks, { type: 'audio/pcm' });
                onComplete(finalBlob);
                break;
            }

            audioChunks.push(value); 
            onChunk(value.length);

            const combined = new Uint8Array(leftover.length + value.length);
            combined.set(leftover);
            combined.set(value, leftover.length);

            const byteLength = combined.length;
            const evenLength = byteLength - (byteLength % 2);
            leftover = combined.slice(evenLength);

            const dataView = new DataView(combined.buffer, 0, evenLength);
            const numSamples = evenLength / 2;
            const float32Array = new Float32Array(numSamples);

            for (let i = 0; i < numSamples; i++) {
                const int16 = dataView.getInt16(i * 2, true);
                float32Array[i] = int16 < 0 ? int16 / 32768.0 : int16 / 32767.0;
            }

            if (numSamples > 0 && this.activeAudioContext.state !== 'closed') {
                const audioBuffer = this.activeAudioContext.createBuffer(1, numSamples, sampleRate);
                audioBuffer.getChannelData(0).set(float32Array);

                const source = this.activeAudioContext.createBufferSource();
                source.buffer = audioBuffer;
                source.connect(this.activeAudioContext.destination);

                const currentTime = this.activeAudioContext.currentTime;
                if (nextStartTime < currentTime) nextStartTime = currentTime;

                source.start(nextStartTime);
                nextStartTime += audioBuffer.duration;
            }
        }
    },

    /**
     * Handles standard encoded formats (MP3/Opus) via MediaSource Extensions.
     */
    async streamMSE(reader, mimeType, player, onChunk, onComplete) {
        const mediaSource = new MediaSource();
        player.src = URL.createObjectURL(mediaSource);
        const audioChunks = []; 

        await new Promise(resolve => mediaSource.addEventListener('sourceopen', resolve, { once: true }));
        const sourceBuffer = mediaSource.addSourceBuffer(mimeType);
        let isFirstChunk = true;

        const appendChunk = async (chunk) => {
            return new Promise((resolve) => {
                sourceBuffer.addEventListener('updateend', resolve, { once: true });
                sourceBuffer.appendBuffer(chunk);
            });
        };

        while (true) {
            const { done, value } = await reader.read();
            if (done) {
                if (mediaSource.readyState === 'open') mediaSource.endOfStream();
                const finalBlob = new Blob(audioChunks, { type: mimeType });
                onComplete(finalBlob);
                break;
            }

            audioChunks.push(value);
            onChunk(value.length);
            await appendChunk(value);

            if (isFirstChunk) {
                player.play().catch(e => console.warn(`Autoplay blocked: ${e.message}`));
                isFirstChunk = false;
            }
        }
    },

    /**
     * Converts raw PCM bytes to a playable WAV Blob by injecting a RIFF/WAVE header.
     * This allows native <audio> tags to play non-streamed PCM files.
     */
    addWavHeader(pcmArrayBuffer, sampleRate) {
        const numChannels = 1; // Mono
        const bitsPerSample = 16;
        const byteRate = sampleRate * numChannels * (bitsPerSample / 8);
        const blockAlign = numChannels * (bitsPerSample / 8);
        const dataSize = pcmArrayBuffer.byteLength;
        const buffer = new ArrayBuffer(44 + dataSize);
        const view = new DataView(buffer);

        const writeString = (view, offset, string) => {
            for (let i = 0; i < string.length; i++) view.setUint8(offset + i, string.charCodeAt(i));
        };

        writeString(view, 0, 'RIFF');
        view.setUint32(4, 36 + dataSize, true);
        writeString(view, 8, 'WAVE');
        writeString(view, 12, 'fmt ');
        view.setUint32(16, 16, true);
        view.setUint16(20, 1, true); // PCM format
        view.setUint16(22, numChannels, true);
        view.setUint32(24, sampleRate, true);
        view.setUint32(28, byteRate, true);
        view.setUint16(32, blockAlign, true);
        view.setUint16(34, bitsPerSample, true);
        writeString(view, 36, 'data');
        view.setUint32(40, dataSize, true);

        // Copy raw PCM data directly after the 44-byte header
        new Uint8Array(buffer, 44).set(new Uint8Array(pcmArrayBuffer));

        return new Blob([buffer], { type: 'audio/wav' });
    }
};