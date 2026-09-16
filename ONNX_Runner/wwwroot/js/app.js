import { initTheme } from './theme.js';
import { getVoices, getEffects, getEnvironments, synthesizeSpeech } from './api.js';
import { AudioEngine } from './audio.js';
import { SUPPORTED_LANGUAGES } from './languages.js';

let currentDownloadUrl = null;
let currentExtension = 'mp3';

// UI Helpers
function log(msg) {
    const logger = document.getElementById('statusLog');
    const time = new Date().toLocaleTimeString('en-US', { hour12: false });
    logger.innerHTML += `[${time}] ${msg}<br>`;
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

// Main Boot Sequence
async function bootEngine() {
    initTheme();
    document.getElementById('statusLog').innerHTML = '';
    log('SYSTEM READY... Awaiting commands.');

    // Fetch available voices, effects, and environments from backend
    const [voicesData, effectsData, envData] = await Promise.all([
        getVoices(),
        getEffects(),
        getEnvironments()
    ]);

    document.getElementById('voiceSelect').innerHTML = voicesData.voices
        .map(v => `<option value="${v}" ${v === 'piper_base' ? 'selected' : ''}>${v}</option>`)
        .join('');

    document.getElementById('effectSelect').innerHTML = effectsData.effects
        .map(e => `<option value="${e}">${e}</option>`)
        .join('');

    document.getElementById('environmentSelect').innerHTML = envData.environments
        .map(e => `<option value="${e}">${e}</option>`)
        .join('');

    // --- DYNAMICLY FILL THE LANGUAGE LIST ---
    document.getElementById('languageSelect').innerHTML = SUPPORTED_LANGUAGES.map(
        lang => `<option value="${lang.code}">${lang.name}</option>`
    ).join('');

    log('Resources synchronized successfully.');

    // Set up UI bindings for sliders and toggles
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

    // Bind toggles to enable/disable related controls
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

    // Handle download button click to save the generated audio file
    downloadBtn.addEventListener('click', () => {
        if (!currentDownloadUrl) return;

        const a = document.createElement('a');
        a.href = currentDownloadUrl;
        a.download = `tsubaki_voice_${Date.now()}.${currentExtension}`;
        a.click();
    });

    // Main click handler for generating speech
    btn.addEventListener('click', async () => {
        const text = document.getElementById('textInput').value.trim();
        if (!text) return alert("Please enter text!");

        document.getElementById('statusLog').innerHTML = '';
        btn.disabled = true;
        downloadBtn.disabled = true;
        btn.innerText = "Processing...";

        // Reset audio player for new playback
        player.pause();
        player.removeAttribute('src');
        player.srcObject = null;
        player.load();

        if (currentDownloadUrl) {
            URL.revokeObjectURL(currentDownloadUrl);
            currentDownloadUrl = null;
        }

        await AudioEngine.stopAll();

        const payload = {
            input: text,
            voice: document.getElementById('voiceSelect').value,
            response_format: document.getElementById('formatSelect').value,
            speed: parseFloat(document.getElementById('speedNum').value),
            stream: document.getElementById('streamToggle').checked,
            language: document.getElementById('languageSelect').value
        };

        if (document.getElementById('useEffect').checked) {
            payload.effect = document.getElementById('effectSelect').value;
            payload.effect_intensity = parseFloat(
                document.getElementById('effectIntNum').value
            );
        }

        // Include environment parameters if enabled
        if (document.getElementById('useEnvironment').checked) {
            payload.environment = document.getElementById('environmentSelect').value;
            payload.environment_intensity = parseFloat(
                document.getElementById('envIntNum').value
            );
            payload.extend_reverb_tail =
                document.getElementById('extendTailToggle').checked;
        }

        // Include noise parameters if enabled
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

        // Include voice shift parameters if enabled
        if (document.getElementById('usePitch').checked) {
            payload.pitch = parseFloat(
                document.getElementById('pitchNum').value
            );
        }

        // Include volume adjustment if enabled
        if (document.getElementById('useVolume').checked) {
            payload.volume = parseFloat(
                document.getElementById('volumeNum').value
            );
        }

        // Include voice cloning parameters if enabled
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

            const targetSampleRate = parseInt(
                response.headers.get('X-Audio-Sample-Rate') ||
                '22050'
            );

            currentExtension =
                payload.response_format === 'opus'
                    ? 'ogg'
                    : payload.response_format;

            let totalBytes = 0;

            // Reports encoded bytes received from the HTTP response.
            const onChunk = (chunkSize) => {
                totalBytes += chunkSize;

                log(
                    `⬇️ Chunk received: ${chunkSize} bytes ` +
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
            // Streaming methods return when transport ends, not when queued audio finishes.
            btn.disabled = false;
            btn.innerText = 'Generate';
        }
    });
}

// Initialize the engine once the DOM is fully loaded
if (document.readyState === 'loading') {
    document.addEventListener(
        'DOMContentLoaded',
        bootEngine
    );
} else {
    bootEngine();
}