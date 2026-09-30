// Verify sample continuity, downmixing, little-endian encoding, and final flush.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
const path = require('node:path');
let Processor;
const messages = [];
class AudioWorkletProcessor {
  constructor() {
    this.port = { postMessage(data, transfer) {
      messages.push(structuredClone(data, transfer ? { transfer } : undefined));
    } };
  }
}
vm.runInNewContext(fs.readFileSync(path.join(__dirname, '../wwwroot/pcm-worklet.js'), 'utf8'), {
  AudioWorkletProcessor, registerProcessor(name, value) { Processor = value; }
});
const processor = new Processor();
const expected = [];
for (let offset = 0; offset < 5003; offset += 128) {
  const count = Math.min(128, 5003 - offset);
  const left = Float32Array.from({ length: count }, (_, i) => Math.sin((offset + i) / 50));
  const right = Float32Array.from(left, value => value / 2);
  for (let i = 0; i < count; i++) expected.push(Math.fround((left[i] + right[i]) / 2));
  assert.equal(processor.process([[left, right]]), true);
}
processor.port.onmessage({ data: 'stop' });
assert.equal(messages.pop(), 'stopped');
assert.deepEqual(messages.map(value => value.byteLength), [8192, 8192, 907 * 4]);
const actual = messages.flatMap(buffer => {
  const view = new DataView(buffer);
  return Array.from({ length: buffer.byteLength / 4 }, (_, i) => view.getFloat32(i * 4, true));
});
assert.deepEqual(actual, expected);
assert.equal(processor.process([[new Float32Array(128)]]), false);
console.log('PASS: 5003 samples preserved, stereo downmix, little-endian PCM, final flush, stop');
