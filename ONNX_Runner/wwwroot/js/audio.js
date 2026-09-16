import {
    resolveStreamHandler,
    addWavHeader
} from './codecs/index.js';

export const AudioEngine = {
    activeAudioContext: null,
    replayObjectUrl: null,
    playbackToken: 0,

    // Stops active Web Audio playback and invalidates delayed replay setup.
    async stopAll() {
        this.playbackToken++;

        if (this.activeAudioContext) {
            const audioContext =
                this.activeAudioContext;

            this.activeAudioContext = null;

            if (
                audioContext.state !==
                'closed'
            ) {
                await audioContext.close();
            }
        }

        if (this.replayObjectUrl) {
            URL.revokeObjectURL(
                this.replayObjectUrl
            );

            this.replayObjectUrl = null;
        }
    },

    // Replaces the player source with a replayable Blob URL.
    setReplaySource(player, blob) {
        if (this.replayObjectUrl) {
            URL.revokeObjectURL(
                this.replayObjectUrl
            );
        }

        this.replayObjectUrl =
            URL.createObjectURL(blob);

        player.srcObject = null;
        player.src =
            this.replayObjectUrl;

        player.load();
    },

    // Plays a complete buffered response using the browser's normal media path.
    async playBuffered(
        format,
        blob,
        sampleRate,
        player
    ) {
        let playableBlob = blob;

        if (format === 'pcm') {
            const arrayBuffer =
                await blob.arrayBuffer();

            playableBlob =
                addWavHeader(
                    arrayBuffer,
                    sampleRate
                );
        }

        this.setReplaySource(
            player,
            playableBlob
        );

        player.play().catch(
            e =>
                console.warn(
                    `Autoplay blocked: ${e.message}`
                )
        );
    },

    // Reports whether the dashboard has a live-playback handler for this audio format.
    supportsStreamingFormat(format) {
        return (
            format === 'mp3' ||
            format === 'opus' ||
            format === 'ogg' ||
            format === 'pcm'
        );
    },

    // Selects the streaming implementation without exposing codec details to the UI.
    async stream({
        format,
        body,
        mimeType,
        sampleRate,
        player,
        onChunk,
        onComplete
    }) {
        const handler =
            resolveStreamHandler(
                format,
                mimeType
            );

        if (!handler) {
            return false;
        }

        // Development diagnostics. Keep disabled for normal dashboard use.
        // console.debug(
        //     `[AudioEngine] Streaming ${format} with ${handler.name || 'selected handler'}.`
        // );

        await handler({
            engine: this,
            reader: body.getReader(),
            mimeType,
            sampleRate,
            player,
            onChunk,
            onComplete
        });

        return true;
    },

    // Preserves the existing public helper for callers that need a WAV wrapper.
    addWavHeader(
        pcmArrayBuffer,
        sampleRate
    ) {
        return addWavHeader(
            pcmArrayBuffer,
            sampleRate
        );
    }
};