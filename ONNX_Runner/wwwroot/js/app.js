import { initTheme } from './theme.js';
import { getVoices, getEffects, synthesizeSpeech } from './api.js';
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

    const [voicesData, effectsData] = await Promise.all([getVoices(), getEffects()]);
    
    document.getElementById('voiceSelect').innerHTML = voicesData.voices.map(v => `<option value="${v}">${v}</option>`).join('');
    document.getElementById('effectSelect').innerHTML = effectsData.effects.map(e => `<option value="${e}">${e}</option>`).join('');
    log('Resources synchronized successfully.');

    syncInputs('speedSlider', 'speedNum');
    syncInputs('effectIntSlider', 'effectIntNum');
    syncInputs('nsSlider', 'nsNum');
    syncInputs('nwSlider', 'nwNum');

    bindToggle('useEffect', ['effectSelect', 'effectIntSlider', 'effectIntNum']);
    bindToggle('useNoiseScale', ['nsSlider', 'nsNum']);
    bindToggle('useNoiseW', ['nwSlider', 'nwNum']);

    const btn = document.getElementById('generateBtn');
    const downloadBtn = document.getElementById('downloadBtn');
    const player = document.getElementById('audioPlayer');

    downloadBtn.addEventListener('click', () => {
        if (!currentDownloadUrl) return;
        const a = document.createElement('a');
        a.href = currentDownloadUrl;
        a.download = `tsubaki_voice_${Date.now()}.${currentExtension}`;
        a.click();
    });

    btn.addEventListener('click', async () => {
        const text = document.getElementById('textInput').value.trim();
        if (!text) return alert("Please enter text!");

        document.getElementById('statusLog').innerHTML = '';
        btn.disabled = true;
        downloadBtn.disabled = true;
        btn.innerText = "Processing...";
        player.removeAttribute('src');

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
        if (document.getElementById('useNoiseScale').checked) payload.noise_scale = parseFloat(document.getElementById('nsNum').value);
        if (document.getElementById('useNoiseW').checked) payload.noise_w = parseFloat(document.getElementById('nwNum').value);

        log(`Transmitting payload to backend...`);

        try {
            const response = await synthesizeSpeech(payload);
            const mimeType = response.headers.get('Content-Type') || 'audio/mpeg';
            const supportsMSE = window.MediaSource && MediaSource.isTypeSupported(mimeType);
            const targetSampleRate = parseInt(response.headers.get('X-Audio-Sample-Rate') || "22050");
            
            currentExtension = payload.response_format === 'opus' ? 'ogg' : payload.response_format;
            let totalBytes = 0;

            // Callback for streaming logs
            const onChunk = (chunkSize) => {
                totalBytes += chunkSize;
                log(`⬇️ Chunk decoded: ${chunkSize} bytes (Total: ${(totalBytes / 1024).toFixed(2)} KB)`);
            };

            // Callback when stream finishes
            const onComplete = (finalBlob) => {
                log("✅ Transmission complete.");
                currentDownloadUrl = URL.createObjectURL(finalBlob);
                downloadBtn.disabled = false;
            };

            // BRANCH 1: Raw PCM
            if (payload.response_format === 'pcm') {
                if (payload.stream) {
                    log('Routing Raw PCM via Web Audio API Queue...');
                    await AudioEngine.streamPCM(response.body.getReader(), targetSampleRate, onChunk, onComplete);
                } else {
                    log('Buffering complete Raw PCM payload...');
                    const blob = await response.blob();
                    
                    // Allow downloading the raw PCM
                    currentDownloadUrl = URL.createObjectURL(blob);
                    downloadBtn.disabled = false;
                    
                    // BUT play it in the browser by adding a temporary WAV header!
                    const arrayBuffer = await blob.arrayBuffer();
                    const playableWavBlob = AudioEngine.addWavHeader(arrayBuffer, targetSampleRate);
                    player.src = URL.createObjectURL(playableWavBlob);
                    player.play().catch(e => log(`⚠️ Autoplay blocked: ${e.message}`));
                    
                    log(`✅ PCM Blob ready & Wrapped for playback. Size: ${(blob.size / 1024).toFixed(2)} KB`);
                }
            } 
            // BRANCH 2: Native MSE Streaming (MP3/Opus)
            else if (payload.stream && supportsMSE) {
                await AudioEngine.streamMSE(response.body.getReader(), mimeType, player, onChunk, onComplete);
            } 
            // BRANCH 3: Standard Buffered Playback (WAV/MP3 without stream)
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

// Bootstrap Event
if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', bootEngine);
} else {
    bootEngine(); 
}