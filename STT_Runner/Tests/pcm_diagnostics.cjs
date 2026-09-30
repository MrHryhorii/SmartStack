// The diagnostic WAV must wrap the transmitted samples without another conversion.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
const path = require('node:path');
const Sample = vm.runInNewContext(fs.readFileSync(path.join(__dirname, '../wwwroot/pcm-diagnostics.js'), 'utf8') + '\nPcmSample;', { Blob });
(async () => {
  const rate = 8000;
  const sample = new Sample(rate);
  const input = new Float32Array(rate * 31);
  for (let i = 0; i < input.length; i++) input[i] = Math.sin(i / 100);
  sample.append(input.buffer);
  sample.append(input.buffer);
  assert.equal(sample.length, rate * 30 * 4);
  const bytes = Buffer.from(await sample.toWave().arrayBuffer());
  assert.equal(bytes.toString('ascii', 0, 4), 'RIFF');
  assert.equal(bytes.readUInt32LE(4), bytes.length - 8);
  assert.equal(bytes.readUInt16LE(20), 3);
  assert.equal(bytes.readUInt32LE(24), rate);
  assert.equal(bytes.readUInt32LE(44), rate * 30);
  assert.equal(bytes.readUInt32LE(52), rate * 30 * 4);
  assert.deepEqual(bytes.subarray(56), Buffer.from(input.buffer, 0, sample.length));
  console.log('PASS: diagnostic WAV preserves every captured PCM byte and caps retention at 30 seconds');
})();
