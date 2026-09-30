// Keep the capture clock in the audio thread. Transport boundaries never cut speech.
class PcmCapture extends AudioWorkletProcessor {
  constructor() {
    super();
    this.buffer = new ArrayBuffer(2048 * 4);
    this.view = new DataView(this.buffer);
    this.count = 0;
    this.stopped = false;
    this.port.onmessage = event => {
      if (event.data !== "stop") return;
      this.stopped = true;
      this.flush();
      this.port.postMessage("stopped");
    };
  }

  flush() {
    if (!this.count) return;
    const bytes = this.count === 2048 ? this.buffer : this.buffer.slice(0, this.count * 4);
    this.port.postMessage(bytes, [bytes]);
    this.buffer = new ArrayBuffer(2048 * 4);
    this.view = new DataView(this.buffer);
    this.count = 0;
  }

  process(inputs) {
    if (this.stopped) return false;
    const channels = inputs[0];
    if (!channels?.length) return true;
    for (let i = 0; i < channels[0].length; i++) {
      let sample = 0;
      for (const channel of channels) sample += channel[i];
      this.view.setFloat32(this.count++ * 4, sample / channels.length, true);
      if (this.count === 2048) this.flush();
    }
    // Outputs stay silent, preventing microphone feedback through the speakers.
    return true;
  }
}

registerProcessor("pcm-capture", PcmCapture);
