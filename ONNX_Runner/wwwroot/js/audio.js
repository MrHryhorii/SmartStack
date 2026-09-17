import {
    resolveStreamHandler,
    addWavHeader
} from './codecs/index.js';

const DEFAULT_SAMPLE_RATE = 22050;

function resolveSampleRate(sampleRate) {
    const value = Number(sampleRate);

    return Number.isFinite(value) && value > 0
        ? value
        : DEFAULT_SAMPLE_RATE;
}

export const AudioEngine = {
    activeAudioContext: null,
    preparedAudioContext: null,

    preparedControlDestination: null,
    preparedControlPlayer: null,

    activeControlDestination: null,
    activeControlPlayer: null,
    activeLiveGain: null,

    replayObjectUrl: null,
    playbackToken: 0,

    // Primes Web Audio and the native player controls while Generate still has user activation.
    prepareStreamingPlayback(
        player,
        sampleRate = DEFAULT_SAMPLE_RATE
    ) {
        if (
            this.preparedAudioContext &&
            this.preparedAudioContext.state !==
                'closed'
        ) {
            return true;
        }

        const AudioContextClass =
            window.AudioContext ||
            window.webkitAudioContext;

        if (!AudioContextClass) {
            return false;
        }

        try {
            const audioContext =
                createAudioContextForRate(
                    AudioContextClass,
                    resolveSampleRate(
                        sampleRate
                    )
                );

            this.preparedAudioContext =
                audioContext;

            this.preparedControlPlayer =
                player;

            this.preparedControlDestination =
                createSilentControlDestination(
                    audioContext,
                    player
                );

            if (
                audioContext.state ===
                'suspended'
            ) {
                audioContext
                    .resume()
                    .catch(error =>
                        console.warn(
                            `[AudioEngine] AudioContext resume failed: ${error.message}`
                        )
                    );
            }

            return true;
        } catch (error) {
            console.warn(
                `[AudioEngine] AudioContext initialization failed: ${error.message}`
            );

            return false;
        }
    },

    // Transfers the gesture-unlocked context and its silent control stream to live playback.
    takePreparedStreamingPlayback(
        player,
        sampleRate = DEFAULT_SAMPLE_RATE
    ) {
        const audioContext =
            this.preparedAudioContext;

        const controlDestination =
            this.preparedControlDestination;

        const controlPlayer =
            this.preparedControlPlayer;

        this.preparedAudioContext =
            null;

        this.preparedControlDestination =
            null;

        this.preparedControlPlayer =
            null;

        if (
            !audioContext ||
            audioContext.state ===
                'closed'
        ) {
            return null;
        }

        const requestedSampleRate =
            resolveSampleRate(
                sampleRate
            );

        if (
            audioContext.sampleRate !==
            requestedSampleRate
        ) {
            detachControlDestination(
                controlPlayer,
                controlDestination
            );

            audioContext
                .close()
                .catch(() => { });

            return null;
        }

        this.activeAudioContext =
            audioContext;

        this.activeControlPlayer =
            player;

        this.activeControlDestination =
            controlDestination &&
            controlPlayer === player
                ? controlDestination
                : createSilentControlDestination(
                    audioContext,
                    player
                );

        ensureControlStreamPlaying(
            player,
            this.activeControlDestination
        );

        return audioContext;
    },

    // Creates the same control surface when no gesture-prepared context was available.
    activateLivePlaybackControl(
        player,
        audioContext
    ) {
        this.activeAudioContext =
            audioContext;

        this.activeControlPlayer =
            player;

        this.activeControlDestination =
            createSilentControlDestination(
                audioContext,
                player
            );

        ensureControlStreamPlaying(
            player,
            this.activeControlDestination
        );
    },

    bindLiveOutput(
        gainNode,
        player
    ) {
        this.activeLiveGain =
            gainNode;

        this.setLiveVolume(
            player.volume,
            player.muted
        );
    },

    // Keeps the native player volume/mute controls meaningful during direct Web Audio playback.
    setLiveVolume(
        volume,
        muted
    ) {
        const gainNode =
            this.activeLiveGain;

        if (!gainNode) {
            return;
        }

        const resolvedVolume =
            Number.isFinite(
                Number(volume)
            )
                ? Math.min(
                    1,
                    Math.max(
                        0,
                        Number(volume)
                    )
                )
                : 1;

        gainNode.gain.value =
            muted
                ? 0
                : resolvedVolume;
    },

    hasLiveControl() {
        return Boolean(
            (
                this.activeAudioContext &&
                this.activeAudioContext.state !==
                    'closed'
            ) ||
            (
                this.preparedAudioContext &&
                this.preparedAudioContext.state !==
                    'closed'
            )
        );
    },

    // Pauses the Web Audio clock without discarding queued PCM.
    async pauseLivePlayback() {
        const audioContext =
            this.activeAudioContext ||
            this.preparedAudioContext;

        if (
            !audioContext ||
            audioContext.state ===
                'closed'
        ) {
            return false;
        }

        if (
            audioContext.state ===
                'running'
        ) {
            await audioContext.suspend();
        }

        return (
            audioContext.state ===
            'suspended'
        );
    },

    // Resumes the same Web Audio timeline from the paused position.
    async resumeLivePlayback() {
        const audioContext =
            this.activeAudioContext ||
            this.preparedAudioContext;

        if (
            !audioContext ||
            audioContext.state ===
                'closed'
        ) {
            return false;
        }

        if (
            audioContext.state ===
                'suspended'
        ) {
            await audioContext.resume();
        }

        return (
            audioContext.state ===
            'running'
        );
    },

    // Closes a gesture-prepared context when the selected path used MSE/buffering instead.
    async releasePreparedPlayback() {
        const audioContext =
            this.preparedAudioContext;

        const controlDestination =
            this.preparedControlDestination;

        const controlPlayer =
            this.preparedControlPlayer;

        this.preparedAudioContext =
            null;

        this.preparedControlDestination =
            null;

        this.preparedControlPlayer =
            null;

        detachControlDestination(
            controlPlayer,
            controlDestination
        );

        if (
            audioContext &&
            audioContext.state !==
                'closed'
        ) {
            await audioContext
                .close()
                .catch(() => { });
        }
    },

    // Removes only the silent HTMLMediaElement control stream; the live context is closed by its owner.
    deactivateLivePlaybackControl(
        player = this.activeControlPlayer
    ) {
        const controlDestination =
            this.activeControlDestination;

        const controlPlayer =
            this.activeControlPlayer ||
            player;

        this.activeControlDestination =
            null;

        this.activeControlPlayer =
            null;

        this.activeLiveGain =
            null;

        detachControlDestination(
            controlPlayer,
            controlDestination
        );
    },

    // Stops active/prepared Web Audio playback and invalidates delayed replay setup.
    async stopAll() {
        this.playbackToken++;

        const activeContext =
            this.activeAudioContext;

        const preparedContext =
            this.preparedAudioContext;

        const activeControlDestination =
            this.activeControlDestination;

        const activeControlPlayer =
            this.activeControlPlayer;

        const preparedControlDestination =
            this.preparedControlDestination;

        const preparedControlPlayer =
            this.preparedControlPlayer;

        this.activeAudioContext =
            null;

        this.preparedAudioContext =
            null;

        this.activeControlDestination =
            null;

        this.activeControlPlayer =
            null;

        this.preparedControlDestination =
            null;

        this.preparedControlPlayer =
            null;

        this.activeLiveGain =
            null;

        detachControlDestination(
            activeControlPlayer,
            activeControlDestination
        );

        detachControlDestination(
            preparedControlPlayer,
            preparedControlDestination
        );

        if (this.replayObjectUrl) {
            URL.revokeObjectURL(
                this.replayObjectUrl
            );

            this.replayObjectUrl =
                null;
        }

        const contexts = [
            activeContext,
            preparedContext
        ].filter(
            (context, index, items) =>
                context &&
                items.indexOf(context) ===
                    index
        );

        await Promise.allSettled(
            contexts.map(
                audioContext =>
                    audioContext.state ===
                        'closed'
                        ? Promise.resolve()
                        : audioContext.close()
            )
        );
    },

    // Replaces live controls with a normal replayable media source.
    setReplaySource(player, blob) {
        this.deactivateLivePlaybackControl(
            player
        );

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
                    resolveSampleRate(sampleRate)
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
            format === 'flac' ||
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

        await handler({
            engine: this,
            reader: body.getReader(),
            mimeType,
            sampleRate:
                resolveSampleRate(sampleRate),
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
            resolveSampleRate(sampleRate)
        );
    }
};

function createAudioContextForRate(
    AudioContextClass,
    sampleRate
) {
    try {
        return new AudioContextClass({
            sampleRate
        });
    } catch (error) {
        console.warn(
            `[AudioEngine] AudioContext ${sampleRate} Hz is unavailable; using the device rate instead.`
        );

        return new AudioContextClass();
    }
}

function createSilentControlDestination(
    audioContext,
    player
) {
    if (
        !player ||
        typeof audioContext
            .createMediaStreamDestination !==
            'function'
    ) {
        return null;
    }

    try {
        const destination =
            audioContext
                .createMediaStreamDestination();

        // Nothing is connected to this destination: it exists only so the native
        // media controls expose stable Play/Pause state while PCM goes direct to speakers.
        player.removeAttribute('src');
        player.srcObject =
            destination.stream;

        return destination;
    } catch (error) {
        console.warn(
            `[AudioEngine] Native live control surface unavailable: ${error.message}`
        );

        return null;
    }
}

function ensureControlStreamPlaying(
    player,
    controlDestination
) {
    if (
        !player ||
        !controlDestination
    ) {
        return;
    }

    if (
        player.srcObject !==
        controlDestination.stream
    ) {
        player.removeAttribute('src');
        player.srcObject =
            controlDestination.stream;
    }

    player.play().catch(
        error =>
            console.warn(
                `[AudioEngine] Live control surface autoplay blocked: ${error.message}`
            )
    );
}

function detachControlDestination(
    player,
    controlDestination
) {
    if (
        !player ||
        !controlDestination ||
        player.srcObject !==
            controlDestination.stream
    ) {
        return;
    }

    player.pause();
    player.srcObject = null;
    player.removeAttribute('src');
    player.load();
}

