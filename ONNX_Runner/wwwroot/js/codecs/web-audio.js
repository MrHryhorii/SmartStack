// Shared Web Audio helpers for incremental codec playback and replay handoff.

const DEFAULT_SAMPLE_RATE = 22050;
const ACCUMULATOR_SECONDS = 0.160;
const INITIAL_PLAYBACK_LEAD_SECONDS = 0.030;

// Creates a direct Web Audio session; the HTML player is reserved for replay.
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

    const resolvedSampleRate =
        resolveSampleRate(sampleRate);

    const preparedAudioContext =
        engine.takePreparedStreamingPlayback?.(
            player,
            resolvedSampleRate
        );

    let audioContext =
        preparedAudioContext;

    if (!audioContext) {
        try {
            // Keep the Web Audio graph at the source rate when possible. This avoids
            // restarting the browser resampler at every scheduled AudioBuffer boundary.
            audioContext =
                createAudioContextForRate(
                    AudioContextClass,
                    resolvedSampleRate
                );
        } catch (error) {
            throw new Error(
                `Unable to initialize Web Audio: ${error.message}`
            );
        }
    }

    if (!preparedAudioContext) {
        engine.activateLivePlaybackControl?.(
            player,
            audioContext
        );
    }

    const playbackToken =
        ++engine.playbackToken;

    engine.activeAudioContext =
        audioContext;

    const outputGain =
        typeof audioContext.createGain ===
            'function'
            ? audioContext.createGain()
            : null;

    if (outputGain) {
        outputGain.connect(
            audioContext.destination
        );

        engine.bindLiveOutput?.(
            outputGain,
            player
        );
    }

    // The prepared context is normally already running. Retry for browsers that
    // suspend it after asynchronous network/decoder work or backgrounding.
    if (audioContext.state === 'suspended') {
        audioContext
            .resume()
            .catch(error =>
                console.warn(
                    `[AudioEngine] AudioContext resume failed: ${error.message}`
                )
            );
    }

    return {
        audioContext,
        playbackToken,
        outputNode:
            outputGain ||
            audioContext.destination,
        nextStartTime:
            audioContext.currentTime,
        totalDuration: 0,

        // Decoders often produce very small PCM blocks. Accumulating roughly 160 ms
        // prevents the Web Audio timeline from starving between JavaScript callbacks.
        pendingAudio: [],
        pendingFrames: 0,
        pendingSampleRate:
            resolvedSampleRate,
        pendingChannelCount: 0,
        accumulatorFrames:
            getAccumulatorFrameCount(
                resolvedSampleRate
            ),
        hasScheduledAudio: false,
        lastScheduledSource: null,
        underrunCount: 0
    };
}

// Adds decoded PCM to the small playback accumulator.
export function queueAudioChannels(
    session,
    channelData,
    sampleRate,
    frameCount = null
) {
    if (
        session.audioContext.state ===
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

    const channelCount =
        channels.length;

    const resolvedSampleRate =
        resolveSampleRate(
            sampleRate,
            session.pendingSampleRate ||
                session.audioContext.sampleRate
        );

    // A stream should not normally change layout or sample rate mid-flight.
    // Flush the previous layout first if a decoder does so unexpectedly.
    if (
        session.pendingFrames > 0 &&
        (
            session.pendingSampleRate !==
                resolvedSampleRate ||
            session.pendingChannelCount !==
                channelCount
        )
    ) {
        flushQueuedAudio(
            session
        );
    }

    if (
        session.pendingFrames === 0
    ) {
        session.pendingSampleRate =
            resolvedSampleRate;

        session.pendingChannelCount =
            channelCount;

        session.accumulatorFrames =
            getAccumulatorFrameCount(
                resolvedSampleRate
            );
    }

    // Copy decoder-owned buffers because some codec libraries reuse their output storage.
    session.pendingAudio.push({
        channels:
            channels.map(
                channel =>
                    channel
                        .subarray(
                            0,
                            length
                        )
                        .slice()
            ),
        offset: 0,
        length
    });

    session.pendingFrames +=
        length;

    drainAccumulator(
        session,
        false
    );

    return true;
}

// Schedules the final short PCM tail before the response is finalized.
export function flushQueuedAudio(
    session
) {
    return drainAccumulator(
        session,
        true
    );
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
    flushQueuedAudio(
        session
    );

    const {
        audioContext
    } = session;

    let replayScheduled = false;

    const finishLivePlayback =
        async () => {
            if (replayScheduled) {
                return;
            }

            replayScheduled = true;

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
                    await audioContext
                        .close()
                        .catch(() => { });
                }

            } catch (error) {
                replayScheduled = false;

                console.warn(
                    `[AudioEngine] Replay source setup failed: ${error.message}`
                );
            }
        };

    const lastSource =
        session.lastScheduledSource;

    if (
        !lastSource ||
        session.nextStartTime <=
            audioContext.currentTime
    ) {
        void finishLivePlayback();
        return;
    }

    // AudioContext.currentTime stops while suspended, and "ended" follows that
    // media timeline. This keeps replay handoff correct across Pause/Resume.
    lastSource.addEventListener(
        'ended',
        () => {
            void finishLivePlayback();
        },
        {
            once: true
        }
    );
}

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
            `[AudioEngine] Web Audio ${sampleRate} Hz context is unavailable; using the device rate.`
        );

        return new AudioContextClass();
    }
}

function resolveSampleRate(
    sampleRate,
    fallback = DEFAULT_SAMPLE_RATE
) {
    const value = Number(sampleRate);

    if (
        Number.isFinite(value) &&
        value > 0
    ) {
        return value;
    }

    const fallbackValue =
        Number(fallback);

    return (
        Number.isFinite(fallbackValue) &&
        fallbackValue > 0
    )
        ? fallbackValue
        : DEFAULT_SAMPLE_RATE;
}

function getAccumulatorFrameCount(
    sampleRate
) {
    const resolvedSampleRate =
        resolveSampleRate(sampleRate);

    return Math.max(
        1024,
        Math.round(
            resolvedSampleRate *
            ACCUMULATOR_SECONDS
        )
    );
}

function drainAccumulator(
    session,
    force
) {
    let scheduled = false;

    while (
        session.pendingFrames >=
            session.accumulatorFrames ||
        (
            force &&
            session.pendingFrames > 0
        )
    ) {
        const length =
            force
                ? Math.min(
                    session.pendingFrames,
                    session.accumulatorFrames
                )
                : session.accumulatorFrames;

        schedulePendingBlock(
            session,
            length
        );

        scheduled = true;
    }

    return scheduled;
}

function schedulePendingBlock(
    session,
    length
) {
    const {
        audioContext
    } = session;

    const channelCount =
        session.pendingChannelCount;

    const sampleRate =
        session.pendingSampleRate;

    const audioBuffer =
        audioContext.createBuffer(
            channelCount,
            length,
            sampleRate
        );

    let writeOffset = 0;
    let framesRemaining = length;

    while (
        framesRemaining > 0
    ) {
        const chunk =
            session.pendingAudio[0];

        const chunkRemaining =
            chunk.length -
            chunk.offset;

        const copyLength =
            Math.min(
                framesRemaining,
                chunkRemaining
            );

        for (
            let channel = 0;
            channel < channelCount;
            channel++
        ) {
            audioBuffer
                .getChannelData(channel)
                .set(
                    chunk.channels[channel]
                        .subarray(
                            chunk.offset,
                            chunk.offset +
                                copyLength
                        ),
                    writeOffset
                );
        }

        chunk.offset +=
            copyLength;

        writeOffset +=
            copyLength;

        framesRemaining -=
            copyLength;

        session.pendingFrames -=
            copyLength;

        if (
            chunk.offset >=
            chunk.length
        ) {
            session.pendingAudio.shift();
        }
    }

    const source =
        audioContext
            .createBufferSource();

    source.buffer =
        audioBuffer;

    // Real PCM stays on the Web Audio clock; the HTML MediaStream remains silent.
    source.connect(
        session.outputNode
    );

    const currentTime =
        audioContext.currentTime;

    if (
        !session.hasScheduledAudio
    ) {
        session.nextStartTime =
            Math.max(
                session.nextStartTime,
                currentTime +
                    INITIAL_PLAYBACK_LEAD_SECONDS
            );

        session.hasScheduledAudio =
            true;
    } else if (
        session.nextStartTime <
        currentTime
    ) {
        const underrunSeconds =
            currentTime -
            session.nextStartTime;

        session.underrunCount++;

        if (
            underrunSeconds >=
            0.005
        ) {
            console.warn(
                `[AudioEngine] Web Audio underrun #${session.underrunCount}: ${Math.round(underrunSeconds * 1000)} ms.`
            );
        }

        // Give the renderer a tiny scheduling margin after a real underrun.
        session.nextStartTime =
            currentTime +
            0.010;
    }

    source.start(
        session.nextStartTime
    );

    session.lastScheduledSource =
        source;

    session.nextStartTime +=
        audioBuffer.duration;

    session.totalDuration +=
        audioBuffer.duration;
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
