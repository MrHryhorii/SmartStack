import { initTheme } from './theme.js';
import { getVoices, getEffects, getEnvironments, synthesizeSpeech } from './api.js';
import { AudioEngine } from './audio.js';
import { SUPPORTED_LANGUAGES } from './languages.js';
import { receiveBase64Json } from './b64-json.js';

let currentDownloadUrl = null;
let currentExtension = 'mp3';

// UI helpers.
function log(msg) {
    const logger = document.getElementById('statusLog');
    const time = new Date().toLocaleTimeString('en-US', { hour12: false });

    // Keep dynamic server/error text out of HTML parsing.
    logger.append(
        document.createTextNode(`[${time}] ${msg}`),
        document.createElement('br')
    );

    logger.scrollTop = logger.scrollHeight;
}

function formatDuration(seconds) {
    if (!Number.isFinite(seconds) || seconds <= 0) {
        return null;
    }

    const totalMinutes =
        Math.floor(seconds / 60);

    const remainingSeconds =
        seconds -
        (totalMinutes * 60);

    const hours =
        Math.floor(totalMinutes / 60);

    const minutes =
        totalMinutes % 60;

    return hours > 0
        ? `${hours}:${String(minutes).padStart(2, '0')}:${remainingSeconds.toFixed(3).padStart(6, '0')}`
        : `${minutes}:${remainingSeconds.toFixed(3).padStart(6, '0')}`;
}

async function getPlayerDuration(player) {
    const readDuration = () =>
        Number.isFinite(player.duration) &&
        player.duration > 0
            ? player.duration
            : null;

    const existingDuration =
        readDuration();

    if (existingDuration !== null) {
        return existingDuration;
    }

    return await new Promise(resolve => {
        let settled = false;

        const cleanup = () => {
            player.removeEventListener(
                'loadedmetadata',
                tryResolve
            );

            player.removeEventListener(
                'durationchange',
                tryResolve
            );

            player.removeEventListener(
                'error',
                resolveWithoutDuration
            );

            clearTimeout(timeoutId);
        };

        const finish = duration => {
            if (settled) {
                return;
            }

            settled = true;
            cleanup();
            resolve(duration);
        };

        const tryResolve = () => {
            const duration =
                readDuration();

            if (duration !== null) {
                finish(duration);
            }
        };

        const resolveWithoutDuration = () =>
            finish(null);

        player.addEventListener(
            'loadedmetadata',
            tryResolve
        );

        player.addEventListener(
            'durationchange',
            tryResolve
        );

        player.addEventListener(
            'error',
            resolveWithoutDuration,
            { once: true }
        );

        const timeoutId =
            setTimeout(
                resolveWithoutDuration,
                2000
            );

        // Covers a metadata event that completed between the initial check
        // and listener registration.
        tryResolve();
    });
}

function syncInputs(sliderId, numId) {
    const slider = document.getElementById(sliderId);
    const num = document.getElementById(numId);
    slider.addEventListener('input', (e) => num.value = e.target.value);
    num.addEventListener('input', (e) => slider.value = e.target.value);
}

function bindToggle(chkId, elementsToToggle) {
    document.getElementById(chkId).addEventListener('change', (e) => {
        elementsToToggle.forEach(id => document.getElementById(id).disabled = !e.target.checked);
    });
}

function populateSelect(selectId, values, selectedValue = null) {
    const select = document.getElementById(selectId);

    // Server-provided names are text, not markup.
    const options = values.map(value => {
        const option = document.createElement('option');
        option.value = value;
        option.textContent = value;
        option.selected = value === selectedValue;
        return option;
    });

    select.replaceChildren(...options);
}

// Populates pronunciation languages and exposes per-language notes as tooltips.
function populateLanguageSelector() {
    const select = document.getElementById('languageSelect');

    select.replaceChildren(...SUPPORTED_LANGUAGES.map(language => {
        const option = document.createElement('option');
        option.value = language.code;
        option.textContent = language.name;

        if (language.note) {
            option.title = language.note;
        }

        return option;
    }));

    const updateTooltip = () => {
        const selected = SUPPORTED_LANGUAGES.find(
            language => language.code === select.value
        );

        select.title = selected?.note ?? '';
    };

    select.addEventListener('change', updateTooltip);
    updateTooltip();
}

// Loads server metadata and binds dashboard controls.
async function bootEngine() {
    initTheme();
    document.getElementById('statusLog').innerHTML = '';
    log('SYSTEM READY... Awaiting commands.');

    const [voicesData, effectsData, envData] = await Promise.all([
        getVoices(),
        getEffects(),
        getEnvironments()
    ]);

    populateSelect('voiceSelect', voicesData.voices, 'piper_base');
    populateSelect('effectSelect', effectsData.effects);
    populateSelect('environmentSelect', envData.environments);

    // Language metadata is local; dynamic audio options come from the server.
    populateLanguageSelector();

    log('Resources synchronized successfully.');

    // Pair sliders with their numeric inputs.
    syncInputs('speedSlider', 'speedNum');
    syncInputs('effectIntSlider', 'effectIntNum');
    syncInputs('envIntSlider', 'envIntNum');
    syncInputs('nsSlider', 'nsNum');
    syncInputs('nwSlider', 'nwNum');
    syncInputs('pitchSlider', 'pitchNum');
    syncInputs('volumeSlider', 'volumeNum');
    syncInputs('cloneIntSlider', 'cloneIntNum');
    syncInputs('toneTempSlider', 'toneTempNum');
    syncInputs('lpqfSlider', 'lpqfNum');

    // Disable dependent controls when an override is off.
    bindToggle('useEffect', ['effectSelect', 'effectIntSlider', 'effectIntNum']);
    bindToggle('useEnvironment', ['environmentSelect', 'envIntSlider', 'envIntNum', 'extendTailToggle']);
    bindToggle('useNoiseScale', ['nsSlider', 'nsNum']);
    bindToggle('useNoiseW', ['nwSlider', 'nwNum']);
    bindToggle('usePitch', ['pitchSlider', 'pitchNum']);
    bindToggle('useVolume', ['volumeSlider', 'volumeNum']);
    bindToggle('useCloneInt', ['cloneIntSlider', 'cloneIntNum']);
    bindToggle('useToneTemp', ['toneTempSlider', 'toneTempNum']);
    bindToggle('useLpqf', ['lpqfSlider', 'lpqfNum']);

    const btn = document.getElementById('generateBtn');
    const downloadBtn = document.getElementById('downloadBtn');
    const player = document.getElementById('audioPlayer');

    // During live Web Audio playback the native player is a control surface.
    // Its silent MediaStream never carries the real PCM.
    player.addEventListener(
        'pause',
        () => {
            if (
                AudioEngine.hasLiveControl()
            ) {
                void AudioEngine
                    .pauseLivePlayback();
            }
        }
    );

    player.addEventListener(
        'play',
        () => {
            if (
                AudioEngine.hasLiveControl()
            ) {
                void AudioEngine
                    .resumeLivePlayback();
            }
        }
    );

    player.addEventListener(
        'volumechange',
        () => {
            if (
                AudioEngine.hasLiveControl()
            ) {
                AudioEngine.setLiveVolume(
                    player.volume,
                    player.muted
                );
            }
        }
    );

    // Downloads the original response representation.
    downloadBtn.addEventListener('click', () => {
        if (!currentDownloadUrl) return;

        const a = document.createElement('a');
        a.href = currentDownloadUrl;
        a.download = `tsubaki_voice_${Date.now()}.${currentExtension}`;
        a.click();
    });

    // Builds and sends one synthesis request.
    btn.addEventListener('click', async () => {
        const text = document.getElementById('textInput').value.trim();
        if (!text) return alert("Please enter text!");

        document.getElementById('statusLog').innerHTML = '';
        btn.disabled = true;
        downloadBtn.disabled = true;
        btn.innerText = "Processing...";

        // Stop previous playback before replacing response state.
        player.pause();
        player.removeAttribute('src');
        player.srcObject = null;
        player.load();

        if (currentDownloadUrl) {
            URL.revokeObjectURL(currentDownloadUrl);
            currentDownloadUrl = null;
        }

        const stopPromise =
            AudioEngine.stopAll();

        const payload = {
            input: text,
            voice: document.getElementById('voiceSelect').value,
            response_format: document.getElementById('formatSelect').value,
            speed: parseFloat(document.getElementById('speedNum').value),
            stream: document.getElementById('streamToggle').checked,
            early_split: document.getElementById('earlySplitToggle').checked,
            language: document.getElementById('languageSelect').value
        };

        // Prime Web Audio before the first await so Safari/other autoplay policies
        // see the context creation/resume as part of the user's Generate click.
        if (
            payload.stream &&
            payload.response_format !==
                'b64_json' &&
            AudioEngine.supportsStreamingFormat(
                payload.response_format
            )
        ) {
            AudioEngine
                .prepareStreamingPlayback(player);
        }

        await stopPromise;

        if (document.getElementById('useEffect').checked) {
            payload.effect = document.getElementById('effectSelect').value;
            payload.effect_intensity = parseFloat(
                document.getElementById('effectIntNum').value
            );
        }

        if (document.getElementById('useEnvironment').checked) {
            payload.environment = document.getElementById('environmentSelect').value;
            payload.environment_intensity = parseFloat(
                document.getElementById('envIntNum').value
            );
            payload.extend_reverb_tail =
                document.getElementById('extendTailToggle').checked;
        }

        if (document.getElementById('useNoiseScale').checked) {
            payload.noise_scale = parseFloat(
                document.getElementById('nsNum').value
            );
        }

        if (document.getElementById('useNoiseW').checked) {
            payload.noise_w = parseFloat(
                document.getElementById('nwNum').value
            );
        }

        if (document.getElementById('usePitch').checked) {
            payload.pitch = parseFloat(
                document.getElementById('pitchNum').value
            );
        }

        if (document.getElementById('useVolume').checked) {
            payload.volume = parseFloat(
                document.getElementById('volumeNum').value
            );
        }

        if (document.getElementById('useCloneInt').checked) {
            payload.clone_intensity = parseFloat(
                document.getElementById('cloneIntNum').value
            );
        }

        if (document.getElementById('useToneTemp').checked) {
            payload.tone_temperature = parseFloat(
                document.getElementById('toneTempNum').value
            );
        }

        if (document.getElementById('useLpqf').checked) {
            payload.low_pass_q_factor = parseFloat(
                document.getElementById('lpqfNum').value
            );
        }

        if (
            payload.stream &&
            payload.response_format === 'b64_json'
        ) {
            log(
                '⚠️ Live playback is unavailable for streamed Base64 JSON. ' +
                'Incoming response chunks will be displayed; playback starts after the complete JSON is received.'
            );
        }
        else if (
            payload.stream &&
            !AudioEngine.supportsStreamingFormat(
                payload.response_format
            )
        ) {
            log(
                `⚠️ Streaming is not available for ${payload.response_format.toUpperCase()}. ` +
                `Waiting for the complete audio file.`
            );
        }

        log('Transmitting payload to backend...');

        try {
            const response = await synthesizeSpeech(payload);

            const mimeType =
                response.headers.get('Content-Type') ||
                'audio/mpeg';

            const sampleRateHeader = Number.parseInt(
                response.headers.get('X-Audio-Sample-Rate') ?? '',
                10
            );

            const targetSampleRate =
                Number.isFinite(sampleRateHeader) &&
                sampleRateHeader > 0
                    ? sampleRateHeader
                    : 22050;

            currentExtension =
                payload.response_format === 'opus'
                    ? 'ogg'
                    : payload.response_format === 'b64_json'
                        ? 'json'
                        : payload.response_format;

            let totalBytes = 0;

            // Reports transport bytes received from the HTTP response.
            const onChunk = (chunkSize) => {
                totalBytes += chunkSize;

                const label =
                    payload.response_format === 'b64_json'
                        ? 'JSON chunk received'
                        : 'Chunk received';

                log(
                    `⬇️ ${label}: ${chunkSize} bytes ` +
                    `(Total: ${(totalBytes / 1024).toFixed(2)} KB)`
                );
            };

            // Makes the original response available for download when transport completes.
            const onComplete = async (
                finalBlob,
                durationSeconds = null
            ) => {
                const formattedDuration =
                    formatDuration(
                        durationSeconds
                    );

                const sizeKb =
                    (finalBlob.size / 1024)
                        .toFixed(2);

                log(
                    formattedDuration
                        ? `✅ Transmission complete. Size: ${sizeKb} KB | Audio duration: ${formattedDuration}`
                        : `✅ Transmission complete. Size: ${sizeKb} KB`
                );

                currentDownloadUrl =
                    URL.createObjectURL(finalBlob);

                downloadBtn.disabled = false;
            };

            if (payload.response_format === 'b64_json') {
                const {
                    jsonBlob,
                    audioBlob
                } = await receiveBase64Json(
                    response,
                    {
                        stream: payload.stream,
                        onChunk
                    }
                );

                currentDownloadUrl =
                    URL.createObjectURL(jsonBlob);

                downloadBtn.disabled = false;

                await AudioEngine.playBuffered(
                    'mp3',
                    audioBlob,
                    targetSampleRate,
                    player
                );

                const durationSeconds =
                    await getPlayerDuration(player);

                const formattedDuration =
                    formatDuration(durationSeconds);

                const jsonSizeKb =
                    (jsonBlob.size / 1024)
                        .toFixed(2);

                const audioSizeKb =
                    (audioBlob.size / 1024)
                        .toFixed(2);

                log(
                    formattedDuration
                        ? `✅ Base64 JSON ready. JSON: ${jsonSizeKb} KB | Decoded MP3: ${audioSizeKb} KB | Audio duration: ${formattedDuration}`
                        : `✅ Base64 JSON ready. JSON: ${jsonSizeKb} KB | Decoded MP3: ${audioSizeKb} KB`
                );

                return;
            }

            let streamed = false;

            if (payload.stream) {
                streamed = await AudioEngine.stream({
                    format: payload.response_format,
                    body: response.body,
                    mimeType,
                    sampleRate: targetSampleRate,
                    player,
                    onChunk,
                    onComplete
                });
            }

            if (!streamed) {
                const blob = await response.blob();

                currentDownloadUrl =
                    URL.createObjectURL(blob);

                downloadBtn.disabled = false;

                await AudioEngine.playBuffered(
                    payload.response_format,
                    blob,
                    targetSampleRate,
                    player
                );

                const durationSeconds =
                    await getPlayerDuration(
                        player
                    );

                const formattedDuration =
                    formatDuration(
                        durationSeconds
                    );

                const sizeKb =
                    (blob.size / 1024)
                        .toFixed(2);

                log(
                    formattedDuration
                        ? `✅ File ready. Size: ${sizeKb} KB | Audio duration: ${formattedDuration}`
                        : `✅ File ready. Size: ${sizeKb} KB`
                );
            }
        } catch (error) {
            log(`❌ CRITICAL ERROR: ${error.message}`);
        } finally {
            // MSE/buffered paths may leave the gesture-unlocked context unused.
            await AudioEngine
                .releasePreparedPlayback();

            // Streaming methods return when transport ends, not when queued audio finishes.
            btn.disabled = false;
            btn.innerText = 'Generate';
        }
    });
}

// Boot after the static DOM is available.
if (document.readyState === 'loading') {
    document.addEventListener(
        'DOMContentLoaded',
        bootEngine
    );
} else {
    bootEngine();
}