// js/app.js
import { initTheme } from './theme.js';
import { getVoices, getEffects, synthesizeSpeech } from './api.js';

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

    // 6. Handle Generation
    const btn = document.getElementById('generateBtn');
    const player = document.getElementById('audioPlayer');

    btn.addEventListener('click', async () => {
        const text = document.getElementById('textInput').value.trim();
        if (!text) return alert("Please enter text!");

        // Reset UI for new generation
        document.getElementById('statusLog').innerHTML = '';
        btn.disabled = true;
        btn.innerText = "Processing...";
        player.removeAttribute('src');
        player.style.display = 'none';

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

            log(`Data stream incoming. Format: ${mimeType}`);

            if (payload.response_format === 'pcm') {
                log('Raw PCM stream detected. Buffering for file download...');
                const blob = await response.blob();
                log(`✅ File ready. Size: ${(blob.size / 1024).toFixed(2)} KB`);
                
                // Trigger automatic download for PCM
                const url = URL.createObjectURL(blob);
                const a = document.createElement('a');
                a.href = url;
                a.download = `voice_${Date.now()}.pcm`;
                a.click();
            } 
            else if (payload.stream && supportsMSE) {
                player.style.display = 'block';
                await handleStreamedResponse(response.body.getReader(), mimeType, player);
            } 
            else {
                if (payload.stream) log("⚠️ Native MSE unavailable for this format. Buffering full payload...");
                const blob = await response.blob();
                log(`✅ File reconstructed. Size: ${(blob.size / 1024).toFixed(2)} KB`);
                
                player.src = URL.createObjectURL(blob);
                player.style.display = 'block';
                player.play().catch(e => log(`⚠️ Autoplay blocked: ${e.message}`));
                log('🎵 Playback engaged.');
            }
        } catch (error) {
            log(`❌ CRITICAL ERROR: ${error.message}`);
        } finally {
            btn.disabled = false;
            btn.innerText = "Генерувати Аудіо";
        }
    });
});

// Helper: Handle Chunked Streaming via MediaSource Extensions
async function handleStreamedResponse(reader, mimeType, player) {
    const mediaSource = new MediaSource();
    player.src = URL.createObjectURL(mediaSource);

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
            log("✅ Transmission complete.");
            break;
        }

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