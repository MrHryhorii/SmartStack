// Creates a Web Audio session routed through the existing HTML audio player.
export function createWebAudioSession(
    engine,
    sampleRate,
    player
) {
    const AudioContextClass =
        window.AudioContext ||
        window.webkitAudioContext;

    if (!AudioContextClass) {
        throw new Error(
            'Web Audio API is not available in this browser.'
        );
    }

    const audioContext =
        new AudioContextClass({
            sampleRate
        });

    const playbackToken =
        ++engine.playbackToken;

    const streamDestination =
        audioContext
            .createMediaStreamDestination();

    engine.activeAudioContext =
        audioContext;

    player.removeAttribute('src');

    player.srcObject =
        streamDestination.stream;

    player.play().catch(
        e =>
            console.warn(
                `Autoplay blocked: ${e.message}`
            )
    );

    return {
        audioContext,
        playbackToken,
        streamDestination,
        nextStartTime:
            audioContext.currentTime,
        totalDuration: 0
    };
}

// Schedules decoded Float32 channel data on the current Web Audio timeline.
export function queueAudioChannels(
    session,
    channelData,
    sampleRate,
    frameCount = null
) {
    const {
        audioContext,
        streamDestination
    } = session;

    if (
        audioContext.state ===
        'closed'
    ) {
        return false;
    }

    const channels =
        channelData.filter(
            channel =>
                channel?.length
        );

    if (
        channels.length === 0
    ) {
        return false;
    }

    const availableFrames =
        Math.min(
            ...channels.map(
                channel =>
                    channel.length
            )
        );

    const length =
        frameCount
            ? Math.min(
                frameCount,
                availableFrames
            )
            : availableFrames;

    if (length <= 0) {
        return false;
    }

    const audioBuffer =
        audioContext.createBuffer(
            channels.length,
            length,
            sampleRate
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
                    .subarray(
                        0,
                        length
                    )
            );
    }

    const source =
        audioContext
            .createBufferSource();

    source.buffer =
        audioBuffer;

    source.connect(
        streamDestination
    );

    const currentTime =
        audioContext.currentTime;

    if (
        session.nextStartTime <
        currentTime
    ) {
        session.nextStartTime =
            currentTime;
    }

    source.start(
        session.nextStartTime
    );

    session.nextStartTime +=
        audioBuffer.duration;

    session.totalDuration +=
        audioBuffer.duration;

    return true;
}

// Replaces live Web Audio playback with a replayable file after queued audio finishes.
export function scheduleReplay(
    engine,
    session,
    player,
    sourceBlob,
    toPlayableBlob =
        async blob => blob
) {
    const {
        audioContext
    } = session;

    const remainingSeconds =
        Math.max(
            0,
            session.nextStartTime -
            audioContext.currentTime
        );

    setTimeout(
        async () => {
            if (
                !isSessionCurrent(
                    engine,
                    session
                )
            ) {
                return;
            }

            try {
                const playableBlob =
                    await toPlayableBlob(
                        sourceBlob
                    );

                if (
                    !isSessionCurrent(
                        engine,
                        session
                    )
                ) {
                    return;
                }

                engine.setReplaySource(
                    player,
                    playableBlob
                );

                engine.activeAudioContext =
                    null;

                if (
                    audioContext.state !==
                    'closed'
                ) {
                    audioContext
                        .close()
                        .catch(() => { });
                }
            } catch (error) {
                console.warn(
                    `[AudioEngine] Replay source setup failed: ${error.message}`
                );
            }
        },
        Math.ceil(
            remainingSeconds * 1000
        ) + 50
    );
}

function isSessionCurrent(
    engine,
    session
) {
    return (
        engine.playbackToken ===
        session.playbackToken &&
        engine.activeAudioContext ===
        session.audioContext &&
        session.audioContext.state !==
        'closed'
    );
}