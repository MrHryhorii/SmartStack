import { initTheme } from './theme.js';
import { getVoices, getEffects, getEnvironments, synthesizeSpeech } from './api.js';
import { AudioEngine } from './audio.js';

let currentDownloadUrl = null;
let currentExtension = 'mp3';

// UI Helpers
function log(msg) {
    const logger = document.getElementById('statusLog');
    const time = new Date().toLocaleTimeString('en-US', { hour12: false });
    logger.innerHTML += `[${time}] ${msg}<br>`;
    logger.scrollTop = logger.scrollHeight; 
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
    const [voicesData, effectsData, envData] = await Promise.all([getVoices(), getEffects(), getEnvironments()]);
    
    document.getElementById('voiceSelect').innerHTML = voicesData.voices.map(v => `<option value="${v}">${v}</option>`).join('');
    document.getElementById('effectSelect').innerHTML = effectsData.effects.map(e => `<option value="${e}">${e}</option>`).join('');
    document.getElementById('environmentSelect').innerHTML = envData.environments.map(e => `<option value="${e}">${e}</option>`).join('');
    log('Resources synchronized successfully.');

    // Set up UI bindings for sliders and toggles
    syncInputs('speedSlider', 'speedNum');
    syncInputs('effectIntSlider', 'effectIntNum');
    syncInputs('envIntSlider', 'envIntNum');
    syncInputs('nsSlider', 'nsNum');
    syncInputs('nwSlider', 'nwNum');
    syncInputs('pitchSlider', 'pitchNum');
    syncInputs('volumeSlider', 'volumeNum');
    // Bind toggles to enable/disable related controls
    bindToggle('useEffect', ['effectSelect', 'effectIntSlider', 'effectIntNum']);
    bindToggle('useEnvironment', ['environmentSelect', 'envIntSlider', 'envIntNum']); 
    bindToggle('useNoiseScale', ['nsSlider', 'nsNum']);
    bindToggle('useNoiseW', ['nwSlider', 'nwNum']);
    bindToggle('usePitch', ['pitchSlider', 'pitchNum']);
    bindToggle('useVolume', ['volumeSlider', 'volumeNum']);

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
        // ----------------

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
            stream: document.getElementById('streamToggle').checked
        };

        if (document.getElementById('useEffect').checked) {
            payload.effect = document.getElementById('effectSelect').value;
            payload.effect_intensity = parseFloat(document.getElementById('effectIntNum').value);
        }
        
        // Include environment parameters if enabled
        if (document.getElementById('useEnvironment').checked) {
            payload.environment = document.getElementById('environmentSelect').value;
            payload.environment_intensity = parseFloat(document.getElementById('envIntNum').value);
        }
        // Include noise parameters if enabled
        if (document.getElementById('useNoiseScale').checked) payload.noise_scale = parseFloat(document.getElementById('nsNum').value);
        if (document.getElementById('useNoiseW').checked) payload.noise_w = parseFloat(document.getElementById('nwNum').value);

        // Include voice shift parameters if enabled
        if (document.getElementById('usePitch').checked) {
            payload.pitch = parseFloat(document.getElementById('pitchNum').value);
        }
        // Include volume adjustment if enabled
        if (document.getElementById('useVolume').checked) {
            payload.volume = parseFloat(document.getElementById('volumeNum').value);
        }

        log(`Transmitting payload to backend...`);
        // Log payload details while masking sensitive info
        try {
            const response = await synthesizeSpeech(payload);
            const mimeType = response.headers.get('Content-Type') || 'audio/mpeg';
            const supportsMSE = window.MediaSource && MediaSource.isTypeSupported(mimeType);
            const targetSampleRate = parseInt(response.headers.get('X-Audio-Sample-Rate') || "22050");
            
            currentExtension = payload.response_format === 'opus' ? 'ogg' : payload.response_format;
            let totalBytes = 0;
            // Callbacks for streaming progress and completion
            const onChunk = (chunkSize) => {
                totalBytes += chunkSize;
                log(`⬇️ Chunk decoded: ${chunkSize} bytes (Total: ${(totalBytes / 1024).toFixed(2)} KB)`);
            };
            // Completion callback to enable download and log final status
            const onComplete = async (finalBlob) => {
                log("✅ Transmission complete.");
                
                if (payload.response_format === 'pcm') {
                    // Create a downloadable URL for the raw PCM data (for users who want the original stream)
                    currentDownloadUrl = URL.createObjectURL(finalBlob);
                    
                    // Convert raw PCM to WAV format for browser playback
                    const arrayBuffer = await finalBlob.arrayBuffer();
                    const playableWavBlob = AudioEngine.addWavHeader(arrayBuffer, targetSampleRate);
                    const playerUrl = URL.createObjectURL(playableWavBlob);
                    
                    // Set the player's source to the playable WAV URL instead of the raw PCM stream
                    player.srcObject = null; 
                    player.src = playerUrl;
                } else {
                    currentDownloadUrl = URL.createObjectURL(finalBlob);
                }
                
                downloadBtn.disabled = false;
            };
            // Handle different response formats and streaming capabilities
            if (payload.response_format === 'pcm') {
                if (payload.stream) {
                    log('Routing Raw PCM via Web Audio API Queue...');
                    await AudioEngine.streamPCM(response.body.getReader(), targetSampleRate, player, onChunk, onComplete);
                } else {
                    log('Buffering complete Raw PCM payload...');
                    const blob = await response.blob();
                    // Create a playable WAV Blob by adding the appropriate header for raw PCM data
                    currentDownloadUrl = URL.createObjectURL(blob);
                    downloadBtn.disabled = false;
                    // Convert raw PCM to WAV format for browser playback
                    const arrayBuffer = await blob.arrayBuffer();
                    const playableWavBlob = AudioEngine.addWavHeader(arrayBuffer, targetSampleRate);
                    player.src = URL.createObjectURL(playableWavBlob);
                    player.play().catch(e => log(`⚠️ Autoplay blocked: ${e.message}`));
                    
                    log(`✅ PCM Blob ready & Wrapped for playback. Size: ${(blob.size / 1024).toFixed(2)} KB`);
                }
            } 
            // For compressed formats (MP3, OGG/Opus), prefer MSE streaming if supported, otherwise fallback to buffering
            else if (payload.stream && supportsMSE) {
                await AudioEngine.streamMSE(response.body.getReader(), mimeType, player, onChunk, onComplete);
            } 
            // Fallback for browsers that don't support MSE or if streaming is disabled: buffer the entire response and then play
            else {
                if (payload.stream) log("⚠️ Native MSE unavailable for this format. Buffering...");
                const blob = await response.blob();
                
                currentDownloadUrl = URL.createObjectURL(blob);
                downloadBtn.disabled = false; 
                
                player.src = currentDownloadUrl;
                player.play().catch(e => log(`⚠️ Autoplay blocked: ${e.message}`));
                log(`✅ File reconstructed. Size: ${(blob.size / 1024).toFixed(2)} KB`);
            }
        } catch (error) {
            log(`❌ CRITICAL ERROR: ${error.message}`);
        } finally {
            btn.disabled = false;
            btn.innerText = "Generate";
        }
    });
}
// Initialize the engine once the DOM is fully loaded
if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', bootEngine);
} else {
    bootEngine(); 
}