const AAC_LC_CODEC = 'mp4a.40.2';
export const AAC_MP4_MIME = `audio/mp4; codecs="${AAC_LC_CODEC}"`;

const AAC_SAMPLE_RATES = [
    96000, 88200, 64000, 48000, 44100, 32000, 24000,
    22050, 16000, 12000, 11025, 8000, 7350
];

export function supportsAacMp4Mse(sampleRate, channelCount = 1) {
    if (!globalThis.MediaSource || channelCount !== 1 || sampleRate > 48000) {
        return false;
    }

    try {
        return MediaSource.isTypeSupported(AAC_MP4_MIME);
    } catch {
        return false;
    }
}

export function createAacMp4InitSegment(sampleRate, channelCount = 1, bitrate = 96000) {
    validateAudioConfig(sampleRate, channelCount);

    if (sampleRate > 48000) {
        throw new RangeError('The dashboard fragmented-MP4 AAC path supports sample rates up to 48 kHz.');
    }

    const ftyp = box(
        'ftyp',
        ascii('isom'),
        u32(0x00000200),
        ascii('isom'),
        ascii('iso6'),
        ascii('mp41')
    );

    const matrix = concat(
        u32(0x00010000), u32(0), u32(0),
        u32(0), u32(0x00010000), u32(0),
        u32(0), u32(0), u32(0x40000000)
    );

    const mvhd = fullBox(
        'mvhd',
        0,
        0,
        u32(0),
        u32(0),
        u32(sampleRate),
        u32(0),
        u32(0x00010000),
        u16(0x0100),
        u16(0),
        u32(0),
        u32(0),
        matrix,
        zeros(24),
        u32(2)
    );

    const tkhd = fullBox(
        'tkhd',
        0,
        0x000007,
        u32(0),
        u32(0),
        u32(1),
        u32(0),
        u32(0),
        u32(0),
        u32(0),
        u16(0),
        u16(0),
        u16(0x0100),
        u16(0),
        matrix,
        u32(0),
        u32(0)
    );

    const mdhd = fullBox(
        'mdhd',
        0,
        0,
        u32(0),
        u32(0),
        u32(sampleRate),
        u32(0),
        u16(0x55C4), // und
        u16(0)
    );

    const hdlr = fullBox(
        'hdlr',
        0,
        0,
        u32(0),
        ascii('soun'),
        u32(0),
        u32(0),
        u32(0),
        ascii('SoundHandler\0')
    );

    const smhd = fullBox(
        'smhd',
        0,
        0,
        u16(0),
        u16(0)
    );

    const url = fullBox('url ', 0, 0x000001);
    const dref = fullBox(
        'dref',
        0,
        0,
        u32(1),
        url
    );
    const dinf = box('dinf', dref);

    const asc = createAudioSpecificConfig(sampleRate, channelCount);
    const decoderSpecificInfo = descriptor(0x05, asc);

    const decoderConfig = descriptor(
        0x04,
        concat(
            u8(0x40), // MPEG-4 Audio
            u8(0x15), // AudioStream, upstream=0, reserved=1
            u24(0),
            u32(bitrate),
            u32(bitrate),
            decoderSpecificInfo
        )
    );

    const slConfig = descriptor(0x06, u8(0x02));

    const esDescriptor = descriptor(
        0x03,
        concat(
            u16(1),
            u8(0),
            decoderConfig,
            slConfig
        )
    );

    const esds = fullBox(
        'esds',
        0,
        0,
        esDescriptor
    );

    const mp4a = box(
        'mp4a',
        zeros(6),
        u16(1),
        u32(0),
        u32(0),
        u16(channelCount),
        u16(16),
        u16(0),
        u16(0),
        u32(sampleRate << 16),
        esds
    );

    const stsd = fullBox(
        'stsd',
        0,
        0,
        u32(1),
        mp4a
    );

    const stts = fullBox('stts', 0, 0, u32(0));
    const stsc = fullBox('stsc', 0, 0, u32(0));
    const stsz = fullBox('stsz', 0, 0, u32(0), u32(0));
    const stco = fullBox('stco', 0, 0, u32(0));
    const stbl = box('stbl', stsd, stts, stsc, stsz, stco);
    const minf = box('minf', smhd, dinf, stbl);
    const mdia = box('mdia', mdhd, hdlr, minf);
    const trak = box('trak', tkhd, mdia);

    const trex = fullBox(
        'trex',
        0,
        0,
        u32(1),
        u32(1),
        u32(1024),
        u32(0),
        u32(0)
    );

    const mvex = box('mvex', trex);
    const moov = box('moov', mvhd, trak, mvex);

    return concat(ftyp, moov);
}

export function createAacMp4Fragment(sequenceNumber, baseDecodeTime, samples) {
    if (!Number.isInteger(sequenceNumber) || sequenceNumber <= 0) {
        throw new RangeError('sequenceNumber must be a positive integer.');
    }

    if (!Number.isSafeInteger(baseDecodeTime) || baseDecodeTime < 0) {
        throw new RangeError('baseDecodeTime must be a non-negative safe integer.');
    }

    if (!Array.isArray(samples) || samples.length === 0) {
        throw new TypeError('samples must contain at least one AAC access unit.');
    }

    const mdatPayload = concat(...samples.map(sample => sample.data));

    const mfhd = fullBox('mfhd', 0, 0, u32(sequenceNumber));
    const tfhd = fullBox('tfhd', 0, 0x020000, u32(1));
    const tfdt = fullBox('tfdt', 1, 0, u64(BigInt(baseDecodeTime)));

    const buildTrun = dataOffset => fullBox(
        'trun',
        0,
        0x000301,
        u32(samples.length),
        i32(dataOffset),
        ...samples.flatMap(sample => [
            u32(sample.duration),
            u32(sample.data.length)
        ])
    );

    let trun = buildTrun(0);
    let traf = box('traf', tfhd, tfdt, trun);
    let moof = box('moof', mfhd, traf);

    trun = buildTrun(moof.length + 8);
    traf = box('traf', tfhd, tfdt, trun);
    moof = box('moof', mfhd, traf);

    const mdat = box('mdat', mdatPayload);
    return concat(moof, mdat);
}

export function adtsPayload(frame, headerLength) {
    if (!(frame instanceof Uint8Array)) {
        throw new TypeError('frame must be a Uint8Array.');
    }

    if (headerLength !== 7 && headerLength !== 9) {
        throw new RangeError('ADTS header length must be 7 or 9 bytes.');
    }

    if (frame.length <= headerLength) {
        throw new Error('AAC ADTS frame has no raw payload.');
    }

    return frame.slice(headerLength);
}

function createAudioSpecificConfig(sampleRate, channelCount) {
    const sampleRateIndex = AAC_SAMPLE_RATES.indexOf(sampleRate);

    if (sampleRateIndex < 0) {
        throw new RangeError(`Unsupported AAC sample rate for MP4: ${sampleRate}.`);
    }

    const audioObjectType = 2; // AAC-LC

    return Uint8Array.of(
        (audioObjectType << 3) | (sampleRateIndex >> 1),
        ((sampleRateIndex & 1) << 7) | (channelCount << 3)
    );
}

function validateAudioConfig(sampleRate, channelCount) {
    if (!AAC_SAMPLE_RATES.includes(sampleRate)) {
        throw new RangeError(`Unsupported AAC sample rate: ${sampleRate}.`);
    }

    if (channelCount !== 1) {
        throw new RangeError('Tsubaki dashboard AAC MP4 remuxing currently supports mono only.');
    }
}

function descriptor(tag, payload) {
    return concat(
        u8(tag),
        descriptorLength(payload.length),
        payload
    );
}

function descriptorLength(length) {
    if (!Number.isInteger(length) || length < 0 || length > 0x0FFFFFFF) {
        throw new RangeError('Invalid MPEG-4 descriptor length.');
    }

    const bytes = [length & 0x7F];
    let value = length >> 7;

    while (value > 0) {
        bytes.unshift((value & 0x7F) | 0x80);
        value >>= 7;
    }

    return Uint8Array.from(bytes);
}

function fullBox(type, version, flags, ...payloads) {
    return box(
        type,
        Uint8Array.of(
            version & 0xFF,
            (flags >>> 16) & 0xFF,
            (flags >>> 8) & 0xFF,
            flags & 0xFF
        ),
        ...payloads
    );
}

function box(type, ...payloads) {
    const payload = concat(...payloads);
    const size = 8 + payload.length;
    return concat(u32(size), ascii(type), payload);
}

function ascii(value) {
    const bytes = new Uint8Array(value.length);
    for (let i = 0; i < value.length; i++) {
        bytes[i] = value.charCodeAt(i) & 0xFF;
    }
    return bytes;
}

function zeros(length) {
    return new Uint8Array(length);
}

function u8(value) {
    return Uint8Array.of(value & 0xFF);
}

function u16(value) {
    return Uint8Array.of(
        (value >>> 8) & 0xFF,
        value & 0xFF
    );
}

function u24(value) {
    return Uint8Array.of(
        (value >>> 16) & 0xFF,
        (value >>> 8) & 0xFF,
        value & 0xFF
    );
}

function u32(value) {
    const v = Number(value) >>> 0;
    return Uint8Array.of(
        (v >>> 24) & 0xFF,
        (v >>> 16) & 0xFF,
        (v >>> 8) & 0xFF,
        v & 0xFF
    );
}

function i32(value) {
    return u32(value);
}

function u64(value) {
    const v = BigInt(value);
    return Uint8Array.of(
        Number((v >> 56n) & 0xFFn),
        Number((v >> 48n) & 0xFFn),
        Number((v >> 40n) & 0xFFn),
        Number((v >> 32n) & 0xFFn),
        Number((v >> 24n) & 0xFFn),
        Number((v >> 16n) & 0xFFn),
        Number((v >> 8n) & 0xFFn),
        Number(v & 0xFFn)
    );
}

function concat(...parts) {
    const total = parts.reduce((sum, part) => sum + part.length, 0);
    const result = new Uint8Array(total);
    let offset = 0;

    for (const part of parts) {
        result.set(part, offset);
        offset += part.length;
    }

    return result;
}
