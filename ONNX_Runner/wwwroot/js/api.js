// Using relative path. The browser will automatically resolve the host and port.
const BASE_URL = '/v1/audio';

export async function getVoices() {
    try {
        const res = await fetch(`${BASE_URL}/voices`);
        if (!res.ok) throw new Error(`HTTP ${res.status}`);
        return await res.json();
    } catch (e) {
        console.error('API Error (Voices):', e);
        return { voices: ["piper_base"] }; // Fallback
    }
}

export async function getEffects() {
    try {
        const res = await fetch(`${BASE_URL}/effects`);
        if (!res.ok) throw new Error(`HTTP ${res.status}`);
        return await res.json();
    } catch (e) {
        console.error('API Error (Effects):', e);
        return { effects: ["None"] }; // Fallback
    }
}

export async function synthesizeSpeech(payload) {
    const res = await fetch(`${BASE_URL}/speech`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(payload)
    });

    if (!res.ok) {
        const errorData = await res.json().catch(() => ({}));
        throw new Error(errorData.error || `HTTP Error: ${res.status}`);
    }

    return res;
}