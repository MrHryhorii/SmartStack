// Uses same-origin Tsubaki routes so the dashboard follows the server host and port.
const BASE_URL = '/tsbk/audio';

// Fetches available voice IDs, falling back to the base Piper voice on failure.
export async function getVoices() {
    try {
        const res = await fetch(`${BASE_URL}/voices`);
        if (!res.ok) throw new Error(`HTTP ${res.status}`);
        return await res.json();
    } catch (e) {
        console.error('API Error (Voices):', e);
        return { voices: ["piper_base"] };
    }
}

// Fetches available voice effects, falling back to None on failure.
export async function getEffects() {
    try {
        const res = await fetch(`${BASE_URL}/effects`);
        if (!res.ok) throw new Error(`HTTP ${res.status}`);
        return await res.json();
    } catch (e) {
        console.error('API Error (Effects):', e);
        return { effects: ["None"] };
    }
}

// Fetches available spatial environments, falling back to None on failure.
export async function getEnvironments() {
    try {
        const res = await fetch(`${BASE_URL}/environments`);
        if (!res.ok) throw new Error(`HTTP ${res.status}`);
        return await res.json();
    } catch (e) {
        console.error('API Error (Environments):', e);
        return { environments: ["None"] };
    }
}

// Posts a speech request and returns its response, surfacing server validation errors.
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