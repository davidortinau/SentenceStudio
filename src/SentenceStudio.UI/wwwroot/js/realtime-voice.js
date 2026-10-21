// Microphone capture and speaker playback for the Conversation activity's voice mode.
// Audio in both directions is PCM16 mono at 24 kHz, the format the realtime session uses.
// The browser only moves audio; the realtime session itself lives on the server.

const SAMPLE_RATE = 24000;
const CHUNK_SAMPLES = 2400; // 100 ms per chunk sent to .NET

// AudioWorklet that converts float samples to PCM16 and posts fixed-size chunks.
const workletSource = `
class PcmCapture extends AudioWorkletProcessor {
  constructor(options) {
    super();
    this.size = options.processorOptions.chunkSamples;
    this.buffer = new Int16Array(this.size);
    this.offset = 0;
  }
  process(inputs) {
    const channel = inputs[0] && inputs[0][0];
    if (!channel) return true;
    let sum = 0;
    for (let i = 0; i < channel.length; i++) {
      const s = Math.max(-1, Math.min(1, channel[i]));
      sum += s * s;
      this.buffer[this.offset++] = s < 0 ? s * 0x8000 : s * 0x7fff;
      if (this.offset === this.size) {
        this.port.postMessage({ pcm: this.buffer.buffer.slice(0), level: Math.sqrt(sum / channel.length) });
        this.offset = 0;
      }
    }
    return true;
  }
}
registerProcessor('pcm-capture', PcmCapture);
`;

let state = null;

export async function start(dotnetRef, options) {
  await stop();
  const halfDuplex = options?.halfDuplex ?? true;

  const stream = await navigator.mediaDevices.getUserMedia({
    audio: { channelCount: 1, echoCancellation: true, noiseSuppression: true, autoGainControl: true },
  });

  const context = new AudioContext({ sampleRate: SAMPLE_RATE });
  const moduleUrl = URL.createObjectURL(new Blob([workletSource], { type: 'application/javascript' }));
  await context.audioWorklet.addModule(moduleUrl);
  URL.revokeObjectURL(moduleUrl);

  const source = context.createMediaStreamSource(stream);
  const capture = new AudioWorkletNode(context, 'pcm-capture', { processorOptions: { chunkSamples: CHUNK_SAMPLES } });
  // Keep the worklet pulling audio without sending the microphone to the speakers.
  const mute = context.createGain();
  mute.gain.value = 0;
  source.connect(capture);
  capture.connect(mute).connect(context.destination);

  state = { dotnetRef, stream, context, source, capture, mute, halfDuplex, nextPlayTime: 0, playing: new Set(), lastLevelSent: 0 };

  capture.port.onmessage = (event) => {
    const s = state;
    if (!s) return;
    // Half duplex: while the partner is speaking, do not send the microphone.
    // This keeps a room speaker from feeding the partner's voice back as learner speech.
    if (s.halfDuplex && isPlaying(s)) return;
    s.dotnetRef.invokeMethodAsync('OnMicAudio', new Uint8Array(event.data.pcm)).catch(() => {});
    const now = performance.now();
    if (now - s.lastLevelSent > 250) {
      s.lastLevelSent = now;
      s.dotnetRef.invokeMethodAsync('OnMicLevel', event.data.level).catch(() => {});
    }
  };

  if (context.state === 'suspended') await context.resume();
  return true;
}

function isPlaying(s) {
  // Small tail so the end of the partner's sentence is not picked up as learner speech.
  return s.playing.size > 0 || s.context.currentTime < s.nextPlayTime + 0.25;
}

export function enqueue(base64) {
  const s = state;
  if (!s || !base64) return;
  const bytes = Uint8Array.from(atob(base64), (c) => c.charCodeAt(0));
  const samples = new Int16Array(bytes.buffer, 0, Math.floor(bytes.byteLength / 2));
  if (samples.length === 0) return;

  const buffer = s.context.createBuffer(1, samples.length, SAMPLE_RATE);
  const channel = buffer.getChannelData(0);
  for (let i = 0; i < samples.length; i++) channel[i] = samples[i] / 0x8000;

  const node = s.context.createBufferSource();
  node.buffer = buffer;
  node.connect(s.context.destination);
  const startAt = Math.max(s.context.currentTime + 0.02, s.nextPlayTime);
  node.start(startAt);
  s.nextPlayTime = startAt + buffer.duration;
  s.playing.add(node);
  node.onended = () => {
    s.playing.delete(node);
    if (s.playing.size === 0 && state === s) {
      s.dotnetRef.invokeMethodAsync('OnPlaybackIdle').catch(() => {});
    }
  };
}

// The learner started talking over the partner: drop queued audio.
export function clearPlayback() {
  const s = state;
  if (!s) return;
  for (const node of s.playing) {
    try { node.stop(); } catch { /* already stopped */ }
  }
  s.playing.clear();
  s.nextPlayTime = 0;
}

export async function stop() {
  const s = state;
  state = null;
  if (!s) return;
  clearPlaybackFor(s);
  try { s.capture.port.onmessage = null; s.source.disconnect(); s.capture.disconnect(); s.mute.disconnect(); } catch { /* ignore */ }
  s.stream.getTracks().forEach((t) => t.stop());
  try { await s.context.close(); } catch { /* ignore */ }
}

function clearPlaybackFor(s) {
  for (const node of s.playing) {
    try { node.stop(); } catch { /* ignore */ }
  }
  s.playing.clear();
}
