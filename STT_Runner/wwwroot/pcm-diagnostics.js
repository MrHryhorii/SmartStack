// Retain an opt-in, bounded copy of the exact PCM payload sent to the server.
class PcmSample {
  constructor(sampleRate) {
    this.sampleRate = sampleRate;
    this.limit = sampleRate * 30 * 4;
    this.length = 0;
    this.parts = [];
  }

  append(buffer) {
    const count = Math.min(buffer.byteLength, this.limit - this.length);
    if (count <= 0) return;
    this.parts.push(buffer.slice(0, count));
    this.length += count;
  }

  toWave() {
    const header = new ArrayBuffer(56);
    const view = new DataView(header);
    const text = (offset, value) => {
      for (let i = 0; i < value.length; i++) view.setUint8(offset + i, value.charCodeAt(i));
    };
    text(0, 'RIFF');
    view.setUint32(4, 48 + this.length, true);
    text(8, 'WAVE');
    text(12, 'fmt ');
    view.setUint32(16, 16, true);
    view.setUint16(20, 3, true); // IEEE float, preserving every original PCM bit.
    view.setUint16(22, 1, true);
    view.setUint32(24, this.sampleRate, true);
    view.setUint32(28, this.sampleRate * 4, true);
    view.setUint16(32, 4, true);
    view.setUint16(34, 32, true);
    text(36, 'fact');
    view.setUint32(40, 4, true);
    view.setUint32(44, this.length / 4, true);
    text(48, 'data');
    view.setUint32(52, this.length, true);
    return new Blob([header, ...this.parts], { type: 'audio/wav' });
  }
}
