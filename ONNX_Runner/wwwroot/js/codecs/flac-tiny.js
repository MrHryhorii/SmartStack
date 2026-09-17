// Minimal decoder for the exact FLAC subset emitted by Tsubaki's managed encoder.
// It is a browser fallback, not a general-purpose FLAC implementation.

const BitsPerSample = 16;
const MaxUnaryZeros = 1 << 20;

export function decodeTsubakiFlacFrame(
    frame,
    frameInfo
) {
    validateFrameCrc(frame);

    if (frameInfo.channelAssignment !== 0) {
        throw new Error(
            'Tsubaki FLAC fallback supports mono frames only.'
        );
    }

    if (frameInfo.sampleSizeCode !== 4) {
        throw new Error(
            'Tsubaki FLAC fallback supports 16-bit frames only.'
        );
    }

    const reader = new BitReader(
        frame,
        frameInfo.headerLength,
        frame.length - 2
    );

    if (reader.readBits(1) !== 0) {
        throw new Error('Invalid FLAC subframe padding bit.');
    }

    const subframeType = reader.readBits(6);
    const hasWastedBits = reader.readBits(1) !== 0;

    if (hasWastedBits) {
        throw new Error(
            'Tsubaki FLAC fallback does not support wasted-bit subframes.'
        );
    }

    if (subframeType === 0) {
        return decodeConstant(
            reader,
            frameInfo.blockSize
        );
    }

    if (subframeType === 1) {
        return decodeVerbatim(
            reader,
            frameInfo.blockSize
        );
    }

    if (
        subframeType >= 8 &&
        subframeType <= 10
    ) {
        return decodeFixed(
            reader,
            frameInfo.blockSize,
            subframeType - 8
        );
    }

    throw new Error(
        `Unsupported Tsubaki FLAC subframe type: ${subframeType}.`
    );
}

function decodeConstant(reader, blockSize) {
    const sample = reader.readSigned(BitsPerSample);
    const normalized = sample / 32768;
    const output = new Float32Array(blockSize);
    output.fill(normalized);
    return output;
}

function decodeVerbatim(reader, blockSize) {
    const output = new Float32Array(blockSize);

    for (let i = 0; i < blockSize; i++) {
        output[i] =
            reader.readSigned(BitsPerSample) /
            32768;
    }

    return output;
}

function decodeFixed(
    reader,
    blockSize,
    predictorOrder
) {
    if (blockSize < predictorOrder) {
        throw new Error('Invalid FLAC fixed-predictor block size.');
    }

    const output = new Float32Array(blockSize);
    let previous2 = 0;
    let previous1 = 0;

    for (let i = 0; i < predictorOrder; i++) {
        const sample = reader.readSigned(BitsPerSample);
        output[i] = sample / 32768;
        previous2 = previous1;
        previous1 = sample;
    }

    const codingMethod = reader.readBits(2);
    const partitionOrder = reader.readBits(4);

    if (codingMethod !== 0 || partitionOrder !== 0) {
        throw new Error(
            'Tsubaki FLAC fallback supports Rice method 0 with partition order 0 only.'
        );
    }

    const riceParameter = reader.readBits(4);

    if (riceParameter === 15) {
        throw new Error(
            'Tsubaki FLAC fallback does not support Rice escape coding.'
        );
    }

    for (
        let i = predictorOrder;
        i < blockSize;
        i++
    ) {
        const residual =
            readRiceSigned(
                reader,
                riceParameter
            );

        let sample;

        if (predictorOrder === 0) {
            sample = residual;
        } else if (predictorOrder === 1) {
            sample = previous1 + residual;
        } else {
            sample =
                (2 * previous1) -
                previous2 +
                residual;
        }

        if (
            sample < -32768 ||
            sample > 32767
        ) {
            throw new Error(
                'Decoded FLAC sample exceeded PCM16 range.'
            );
        }

        output[i] = sample / 32768;
        previous2 = previous1;
        previous1 = sample;
    }

    return output;
}

function readRiceSigned(reader, riceParameter) {
    let quotient = 0;

    while (reader.readBits(1) === 0) {
        quotient++;

        if (quotient > MaxUnaryZeros) {
            throw new Error('Invalid FLAC Rice unary code.');
        }
    }

    const remainder =
        riceParameter === 0
            ? 0
            : reader.readBits(riceParameter);

    const folded =
        quotient * (2 ** riceParameter) +
        remainder;

    return (folded & 1) === 0
        ? folded / 2
        : -((folded + 1) / 2);
}

function validateFrameCrc(frame) {
    if (frame.length < 3) {
        throw new Error('Incomplete FLAC frame.');
    }

    const expected =
        (frame[frame.length - 2] << 8) |
        frame[frame.length - 1];

    const actual =
        computeCrc16(
            frame,
            0,
            frame.length - 2
        );

    if (expected !== actual) {
        throw new Error('FLAC frame CRC-16 mismatch.');
    }
}

function computeCrc16(buffer, start, end) {
    let crc = 0;

    for (let i = start; i < end; i++) {
        crc ^= buffer[i] << 8;

        for (let bit = 0; bit < 8; bit++) {
            crc =
                (crc & 0x8000) !== 0
                    ? ((crc << 1) ^ 0x8005) & 0xFFFF
                    : (crc << 1) & 0xFFFF;
        }
    }

    return crc;
}

class BitReader {
    constructor(buffer, startByte, endByte) {
        this.buffer = buffer;
        this.bitPosition = startByte * 8;
        this.endBit = endByte * 8;
    }

    readSigned(bitCount) {
        const value = this.readBits(bitCount);
        const signBit = 2 ** (bitCount - 1);
        const fullRange = 2 ** bitCount;

        return value >= signBit
            ? value - fullRange
            : value;
    }

    readBits(bitCount) {
        if (
            bitCount < 0 ||
            bitCount > 24 ||
            this.bitPosition + bitCount > this.endBit
        ) {
            throw new Error('Unexpected end of FLAC frame.');
        }

        let value = 0;

        for (let i = 0; i < bitCount; i++) {
            const byteIndex =
                this.bitPosition >> 3;
            const bitIndex =
                7 - (this.bitPosition & 7);

            value =
                (value * 2) +
                ((this.buffer[byteIndex] >> bitIndex) & 1);

            this.bitPosition++;
        }

        return value;
    }
}
