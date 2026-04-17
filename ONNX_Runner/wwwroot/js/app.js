// js/app.js
import { initTheme } from './theme.js';
import { getVoices, getEffects, synthesizeSpeech } from './api.js';

// Global variables to handle file downloading
let currentDownloadUrl = null;
let currentExtension = 'mp3';

// Helper: Append messages to the terminal log
function log(msg) {
    const logger = document.getElementById('statusLog');
    const time = new Date().toLocaleTimeString('en-US', { hour12: false });
    logger.innerHTML += `[${time}] ${msg}<br>`;
    logger.scrollTop = logger.scrollHeight; // Auto-scroll to bottom
}

// Helper: Sync range slider and number input two-way
function syncInputs(sliderId, numId) {
    const slider = document.getElementById(sliderId);
    const num = document.getElementById(numId);
    
    slider.addEventListener('input', (e) => num.value = e.target.value);
    num.addEventListener('input', (e) => slider.value = e.target.value);
}

// Helper: Bind a checkbox to enable/disable related inputs
function bindToggle(chkId, elementsToToggle) {
    const chk = document.getElementById(chkId);
    chk.addEventListener('change', (e) => {
        elementsToToggle.forEach(id => {
            document.getElementById(id).disabled = !e.target.checked;
        });
    });
}

document.addEventListener('DOMContentLoaded', async () => {
    // 1. Initialize UI theme
    initTheme();
    
    // 2. Initialize terminal
    document.getElementById('statusLog').innerHTML = '';
    log('SYSTEM READY... Awaiting commands.');
    log('Uplink to core established. Fetching resources...');

    // 3. Fetch server data
    const [voicesData, effectsData] = await Promise.all([getVoices(), getEffects()]);
    
    const voiceSelect = document.getElementById('voiceSelect');
    voiceSelect.innerHTML = voicesData.voices.map(v => `<option value="${v}">${v}</option>`).join('');
    
    const effectSelect = document.getElementById('effectSelect');
    effectSelect.innerHTML = effectsData.effects.map(e => `<option value="${e}">${e}</option>`).join('');
    log('Resources synchronized successfully.');

    // 4. Setup UI bindings (Sliders + Number inputs)
    syncInputs('speedSlider', 'speedNum');
    syncInputs('effectIntSlider', 'effectIntNum');
    syncInputs('nsSlider', 'nsNum');
    syncInputs('nwSlider', 'nwNum');

    // 5. Setup UI toggles (Checkboxes)
    bindToggle('useEffect', ['effectSelect', 'effectIntSlider', 'effectIntNum']);
    bindToggle('useNoiseScale', ['nsSlider', 'nsNum']);
    bindToggle('useNoiseW', ['nwSlider', 'nwNum']);

    // 6. Handle Generation & UI Elements
    const btn = document.getElementById('generateBtn');
    const downloadBtn = document.getElementById('downloadBtn');
    const player = document.getElementById('audioPlayer');

    // Download Button Logic
    downloadBtn.addEventListener('click', () => {
        if (!currentDownloadUrl) return;
        const a = document.createElement('a');
        a.href = currentDownloadUrl;
        a.download = `tsubaki_voice_${Date.now()}.${currentExtension}`;
        a.click();
    });

    // Generate Button Logic
    btn.addEventListener('click', async () => {
        const text = document.getElementById('textInput').value.trim();
        if (!text) return alert("Please enter text!");

        // Reset UI for new generation
        document.getElementById('statusLog').innerHTML = '';
        btn.disabled = true;
        downloadBtn.disabled = true;
        btn.innerText = "Processing...";
        player.removeAttribute('src');

        // Clean up previous file URL from browser memory
        if (currentDownloadUrl) {
            URL.revokeObjectURL(currentDownloadUrl);
            currentDownloadUrl = null;
        }

        log('Initializing synthesis sequence...');
        
        // Build payload. We read values from the number inputs since they are more precise.
        const payload = {
            input: text,
            voice: document.getElementById('voiceSelect').value,
            response_format: document.getElementById('formatSelect').value,
            speed: parseFloat(document.getElementById('speedNum').value),
            stream: document.getElementById('streamToggle').checked
        };

        // Append optional advanced parameters if their checkboxes are ticked
        if (document.getElementById('useEffect').checked) {
            payload.effect = document.getElementById('effectSelect').value;
            payload.effect_intensity = parseFloat(document.getElementById('effectIntNum').value);
            log(`DSP Engaged: ${payload.effect} | Intensity: ${payload.effect_intensity}`);
        }
        if (document.getElementById('useNoiseScale').checked) {
            payload.noise_scale = parseFloat(document.getElementById('nsNum').value);
        }
        if (document.getElementById('useNoiseW').checked) {
            payload.noise_w = parseFloat(document.getElementById('nwNum').value);
        }

        log(`Payload assembled. Transmitting to backend...`);

        try {
            const response = await synthesizeSpeech(payload);
            const mimeType = response.headers.get('Content-Type') || 'audio/mpeg';
            const supportsMSE = window.MediaSource && MediaSource.isTypeSupported(mimeType);
            
            // Set correct extension for downloading
            currentExtension = payload.response_format === 'opus' ? 'ogg' : payload.response_format;

            log(`Data stream incoming. Format: ${mimeType}`);

            if (payload.response_format === 'pcm') {
                log('Raw PCM stream detected. Buffering for file download...');
                const blob = await response.blob();
                
                currentDownloadUrl = URL.createObjectURL(blob);
                downloadBtn.disabled = false; // Enable manual download
                
                log(`✅ File ready. Size: ${(blob.size / 1024).toFixed(2)} KB`);
                downloadBtn.click(); // Auto-download for PCM since standard player can't play it yet
            } 
            else if (payload.stream && supportsMSE) {
                // Pass downloadBtn to the streaming function so it can unlock it when finished
                await handleStreamedResponse(response.body.getReader(), mimeType, player, downloadBtn);
            } 
            else {
                if (payload.stream) log("⚠️ Native MSE unavailable for this format. Buffering full payload...");
                const blob = await response.blob();
                
                currentDownloadUrl = URL.createObjectURL(blob);
                downloadBtn.disabled = false; // Enable download button
                
                log(`✅ File reconstructed. Size: ${(blob.size / 1024).toFixed(2)} KB`);
                
                player.src = currentDownloadUrl;
                player.play().catch(e => log(`⚠️ Autoplay blocked: ${e.message}`));
                log('🎵 Playback engaged.');
            }
        } catch (error) {
            log(`❌ CRITICAL ERROR: ${error.message}`);
        } finally {
            btn.disabled = false;
            btn.innerText = "Generate";
        }
    });
});

// Helper: Handle Chunked Streaming & Combine Chunks for Download
async function handleStreamedResponse(reader, mimeType, player, downloadBtn) {
    const mediaSource = new MediaSource();
    player.src = URL.createObjectURL(mediaSource);
    
    const audioChunks = []; // Store chunks to build the final file for downloading

    await new Promise(resolve => mediaSource.addEventListener('sourceopen', resolve, { once: true }));
    
    const sourceBuffer = mediaSource.addSourceBuffer(mimeType);
    let totalBytes = 0;
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
            if (mediaSource.readyState === 'open') {
                mediaSource.endOfStream();
            }
            log("✅ Transmission complete. Reconstructing file for download...");
            
            // Combine all streamed chunks into a single Blob for the download button
            const finalBlob = new Blob(audioChunks, { type: mimeType });
            currentDownloadUrl = URL.createObjectURL(finalBlob);
            downloadBtn.disabled = false; // Unlock the download button
            
            log(`💾 Download ready. Total size: ${(totalBytes / 1024).toFixed(2)} KB`);
            break;
        }

        audioChunks.push(value); // Save chunk
        totalBytes += value.length;
        log(`⬇️ Chunk decoded: ${value.length} bytes (Total: ${(totalBytes / 1024).toFixed(2)} KB)`);
        
        await appendChunk(value);

        if (isFirstChunk) {
            player.play().catch(e => log(`⚠️ Autoplay blocked: ${e.message}`));
            log("🎵 Playback engaged.");
            isFirstChunk = false;
        }
    }
}