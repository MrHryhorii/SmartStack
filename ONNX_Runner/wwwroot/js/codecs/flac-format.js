// FLAC framing helpers shared by the native and Tsubaki-specific live decoders.

export function tryExtractFlacMetadata(buffer) {
    if (buffer.length < 8) {
        return null;
    }

    if (
        buffer[0] !== 0x66 ||
        buffer[1] !== 0x4C ||
        buffer[2] !== 0x61 ||
        buffer[3] !== 0x43
    ) {
        throw new Error('Invalid FLAC stream marker.');
    }

    let offset = 4;
    let streamInfo = null;

    while (true) {
        if (buffer.length < offset + 4) {
            return null;
        }

        const header = buffer[offset];
        const isLast = (header & 0x80) !== 0;
        const blockType = header & 0x7F;
        const blockLength =
            (buffer[offset + 1] << 16) |
            (buffer[offset + 2] << 8) |
            buffer[offset + 3];

        const bodyOffset = offset + 4;
        const blockEnd = bodyOffset + blockLength;

        if (buffer.length < blockEnd) {
            return null;
        }

        if (blockType === 0 && !streamInfo) {
            if (blockLength < 34) {
                throw new Error('Invalid FLAC STREAMINFO block.');
            }

            streamInfo = parseStreamInfo(buffer, bodyOffset);
        }

        offset = blockEnd;

        if (!isLast) {
            continue;
        }

        if (!streamInfo) {
            throw new Error('FLAC STREAMINFO block is missing.');
        }

        return {
            ...streamInfo,
            description: buffer.slice(0, offset),
            remainder: buffer.slice(offset)
        };
    }
}

export function findFlacFrameStart(
    buffer,
    startOffset,
    expectedFrameNumber
) {
    for (
        let offset = startOffset;
        offset < buffer.length - 5;
        offset++
    ) {
        if (
            buffer[offset] !== 0xFF ||
            (buffer[offset + 1] & 0xFE) !== 0xF8
        ) {
            continue;
        }

        if (
            parseFlacFrameHeader(
                buffer,
                offset,
                expectedFrameNumber
            )
        ) {
            return offset;
        }
    }

    return -1;
}

export function parseFlacFrameHeader(
    buffer,
    offset,
    expectedFrameNumber
) {
    if (buffer.length < offset + 6) {
        return null;
    }

    if (
        buffer[offset] !== 0xFF ||
        (buffer[offset + 1] & 0xFE) !== 0xF8
    ) {
        return null;
    }

    const blockingStrategy = buffer[offset + 1] & 0x01;

    // Tsubaki emits fixed-block FLAC. Requiring that strategy also rejects false syncs
    // inside compressed residual data before the CRC check is reached.
    if (blockingStrategy !== 0) {
        return null;
    }

    const blockSizeCode = buffer[offset + 2] >> 4;
    const sampleRateCode = buffer[offset + 2] & 0x0F;
    const channelAssignment = buffer[offset + 3] >> 4;
    const sampleSizeCode = (buffer[offset + 3] >> 1) & 0x07;

    if (
        blockSizeCode === 0 ||
        sampleRateCode === 15 ||
        channelAssignment > 10 ||
        sampleSizeCode === 3 ||
        sampleSizeCode === 7 ||
        (buffer[offset + 3] & 0x01) !== 0
    ) {
        return null;
    }

    let cursor = offset + 4;
    const frameNumber = readUtf8Integer(buffer, cursor);

    if (!frameNumber || frameNumber.value !== expectedFrameNumber) {
        return null;
    }

    cursor += frameNumber.length;

    let blockSize;

    if (blockSizeCode === 1) {
        blockSize = 192;
    } else if (blockSizeCode >= 2 && blockSizeCode <= 5) {
        blockSize = 576 << (blockSizeCode - 2);
    } else if (blockSizeCode === 6) {
        if (buffer.length <= cursor) {
            return null;
        }

        blockSize = buffer[cursor] + 1;
        cursor++;
    } else if (blockSizeCode === 7) {
        if (buffer.length < cursor + 2) {
            return null;
        }

        blockSize =
            ((buffer[cursor] << 8) |
            buffer[cursor + 1]) + 1;
        cursor += 2;
    } else {
        blockSize = 256 << (blockSizeCode - 8);
    }

    if (sampleRateCode === 12) {
        cursor += 1;
    } else if (sampleRateCode === 13 || sampleRateCode === 14) {
        cursor += 2;
    }

    if (buffer.length <= cursor) {
        return null;
    }

    const expectedCrc = buffer[cursor];
    const actualCrc = computeCrc8(buffer, offset, cursor);

    if (expectedCrc !== actualCrc) {
        return null;
    }

    return {
        blockSize,
        channelAssignment,
        sampleSizeCode,
        headerLength: cursor - offset + 1
    };
}

export function concatBytes(left, right) {
    if (left.length === 0) {
        return right.slice();
    }

    const combined = new Uint8Array(left.length + right.length);
    combined.set(left, 0);
    combined.set(right, left.length);
    return combined;
}


// Creates the final downloadable/replayable FLAC with a known duration.
// The live HTTP bytes remain untouched; only the completed Blob header is patched.
export function createFinalFlacBlob(
    chunks,
    knownTotalSamples = 0
) {
    let totalSamples = knownTotalSamples;

    if (!Number.isSafeInteger(totalSamples) || totalSamples <= 0) {
        const completeFile = concatChunkList(chunks);
        totalSamples = countFlacSamples(completeFile);

        if (totalSamples <= 0) {
            return new Blob(
                [completeFile],
                { type: 'audio/flac' }
            );
        }

        patchStreamInfoTotalSamples(
            completeFile,
            totalSamples
        );

        return new Blob(
            [completeFile],
            { type: 'audio/flac' }
        );
    }

    const prefix = collectHeaderPrefix(
        chunks,
        26
    );

    if (!prefix) {
        return new Blob(
            chunks,
            { type: 'audio/flac' }
        );
    }

    patchStreamInfoTotalSamples(
        prefix.bytes,
        totalSamples
    );

    return new Blob(
        [
            prefix.bytes,
            ...chunks.slice(prefix.consumedChunks)
        ],
        { type: 'audio/flac' }
    );
}

// Counts fixed-block FLAC samples from frame headers when STREAMINFO was streamed with zero.
export function countFlacSamples(fileBytes) {
    const metadata =
        tryExtractFlacMetadata(
            fileBytes
        );

    if (!metadata) {
        return 0;
    }

    if (metadata.totalSamples > 0) {
        return metadata.totalSamples;
    }

    let remaining = metadata.remainder;
    let expectedFrameNumber = 0;
    let totalSamples = 0;

    while (remaining.length > 0) {
        const current =
            parseFlacFrameHeader(
                remaining,
                0,
                expectedFrameNumber
            );

        if (!current) {
            return 0;
        }

        totalSamples += current.blockSize;

        const nextOffset =
            findFlacFrameStart(
                remaining,
                current.headerLength,
                expectedFrameNumber + 1
            );

        if (nextOffset < 0) {
            return totalSamples;
        }

        remaining =
            remaining.slice(nextOffset);

        expectedFrameNumber++;
    }

    return totalSamples;
}

function patchStreamInfoTotalSamples(
    bytes,
    totalSamples
) {
    if (
        bytes.length < 26 ||
        bytes[0] !== 0x66 ||
        bytes[1] !== 0x4C ||
        bytes[2] !== 0x61 ||
        bytes[3] !== 0x43 ||
        (bytes[4] & 0x7F) !== 0
    ) {
        return false;
    }

    const streamInfoLength =
        (bytes[5] << 16) |
        (bytes[6] << 8) |
        bytes[7];

    if (
        streamInfoLength < 34 ||
        !Number.isSafeInteger(totalSamples) ||
        totalSamples <= 0 ||
        totalSamples > 0xFFFFFFFFF
    ) {
        return false;
    }

    let combined = 0n;

    for (let i = 18; i < 26; i++) {
        combined =
            (combined << 8n) |
            BigInt(bytes[i]);
    }

    combined =
        (combined & ~0xFFFFFFFFFn) |
        BigInt(totalSamples);

    for (let i = 25; i >= 18; i--) {
        bytes[i] =
            Number(combined & 0xFFn);

        combined >>= 8n;
    }

    return true;
}

function collectHeaderPrefix(
    chunks,
    requiredBytes
) {
    let length = 0;
    let consumedChunks = 0;

    while (
        consumedChunks < chunks.length &&
        length < requiredBytes
    ) {
        length +=
            chunks[consumedChunks].length;

        consumedChunks++;
    }

    if (length < requiredBytes) {
        return null;
    }

    const bytes = new Uint8Array(length);
    let offset = 0;

    for (
        let i = 0;
        i < consumedChunks;
        i++
    ) {
        bytes.set(
            chunks[i],
            offset
        );

        offset += chunks[i].length;
    }

    return {
        bytes,
        consumedChunks
    };
}

function concatChunkList(chunks) {
    let totalLength = 0;

    for (const chunk of chunks) {
        totalLength += chunk.length;
    }

    const result =
        new Uint8Array(totalLength);

    let offset = 0;

    for (const chunk of chunks) {
        result.set(chunk, offset);
        offset += chunk.length;
    }

    return result;
}

function parseStreamInfo(buffer, bodyOffset) {
    let combined = 0n;

    for (let i = 10; i < 18; i++) {
        combined =
            (combined << 8n) |
            BigInt(buffer[bodyOffset + i]);
    }

    return {
        sampleRate:
            Number((combined >> 44n) & 0xFFFFFn),
        channels:
            Number((combined >> 41n) & 0x7n) + 1,
        bitsPerSample:
            Number((combined >> 36n) & 0x1Fn) + 1,
        totalSamples:
            Number(combined & 0xFFFFFFFFFn)
    };
}

function readUtf8Integer(buffer, offset) {
    if (buffer.length <= offset) {
        return null;
    }

    const first = buffer[offset];
    let length;
    let value;

    if ((first & 0x80) === 0) {
        length = 1;
        value = first;
    } else if ((first & 0xE0) === 0xC0) {
        length = 2;
        value = first & 0x1F;
    } else if ((first & 0xF0) === 0xE0) {
        length = 3;
        value = first & 0x0F;
    } else if ((first & 0xF8) === 0xF0) {
        length = 4;
        value = first & 0x07;
    } else if ((first & 0xFC) === 0xF8) {
        length = 5;
        value = first & 0x03;
    } else if ((first & 0xFE) === 0xFC) {
        length = 6;
        value = first & 0x01;
    } else {
        return null;
    }

    if (buffer.length < offset + length) {
        return null;
    }

    for (let i = 1; i < length; i++) {
        const next = buffer[offset + i];

        if ((next & 0xC0) !== 0x80) {
            return null;
        }

        value = value * 64 + (next & 0x3F);
    }

    return { value, length };
}

function computeCrc8(buffer, start, end) {
    let crc = 0;

    for (let i = start; i < end; i++) {
        crc ^= buffer[i];

        for (let bit = 0; bit < 8; bit++) {
            crc =
                (crc & 0x80) !== 0
                    ? ((crc << 1) ^ 0x07) & 0xFF
                    : (crc << 1) & 0xFF;
        }
    }

    return crc;
}
