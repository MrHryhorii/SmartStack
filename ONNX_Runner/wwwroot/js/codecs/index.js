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

import {
    streamOggOpus
} from './ogg-opus.js';


const STREAMING_FORMATS = new Set([
    'pcm',
    'mp3',
    'opus',
    'ogg'
]);

// Reports whether the dashboard has an implemented streaming path for the format.
export function supportsStreamingFormat(
    format
) {
    return STREAMING_FORMATS.has(
        format
    );
}

// Resolves a streaming backend for the requested response format.
export function resolveStreamHandler(
    format,
    mimeType
) {
    if (!supportsStreamingFormat(format)) {
        return null;
    }

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

    if (
        format === 'opus' ||
        format === 'ogg'
    ) {
        if (supportsMSE(mimeType)) {
            return streamMSE;
        }

        // Development diagnostics. Keep disabled for normal dashboard use.
        // console.debug(
        //     '[AudioEngine] Ogg/Opus MSE unavailable; using WebAssembly fallback.'
        // );

        return streamOggOpus;
    }

    return null;
}

export {
    addWavHeader
};