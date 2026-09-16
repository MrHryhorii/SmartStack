import {
    streamMSE,
    supportsMSE
} from './mse.js';

import {
    streamPCM,
    addWavHeader
} from './pcm.js';

import {
    streamMP3
} from './mp3.js';

// Resolves a streaming backend for the requested response format.
export function resolveStreamHandler(
    format,
    mimeType
) {
    if (format === 'pcm') {
        return streamPCM;
    }

    if (format === 'mp3') {
        if (supportsMSE(mimeType)) {
            return streamMSE;
        }

        // Development diagnostics. Keep disabled for normal dashboard use.
        // console.debug(
        //     '[AudioEngine] MP3 MSE unavailable; using mpg123 WebAssembly fallback.'
        // );

        return streamMP3;
    }

    if (supportsMSE(mimeType)) {
        return streamMSE;
    }

    return null;
}

export {
    addWavHeader
};