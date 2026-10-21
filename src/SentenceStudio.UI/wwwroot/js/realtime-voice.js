// Microphone capture and speaker playback for the Conversation activity's voice mode.
// Audio in both directions is PCM16 mono at 24 kHz, the format the realtime session uses.
// The browser only moves audio; the realtime session itself lives on the server.

import { distanceFromBottom, NEAR_BOTTOM_THRESHOLD_PX } from './coach-autoscroll-policy.js';

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
  // Keep the microphone paused a little past the end of the partner's audio. Scheduled time does not
  // include the device's output latency, and a room speaker adds reverb, so without this tail the end
  // of the partner's sentence comes back as a noise-only learner turn.
  const outputLatency = s.context.outputLatency || s.context.baseLatency || 0;
  return s.playing.size > 0 || s.context.currentTime < s.nextPlayTime + 0.6 + outputLatency;
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

// Keep the latest spoken turn in view while voice mode runs.
//
// Transcripts arrive as many small text updates, so this watches the page for changes rather than
// asking .NET to scroll after every delta. It follows the Coach's rules
// (coach-autoscroll-policy.js): a reader at the bottom keeps up with new turns; a reader who has
// scrolled up to reread something is left where they are until they scroll back down.
//
// The thread is found by id on every update, because Blazor can re-create the element (for example
// while the conversation is still loading), and an observer on the old element would go silent.
let follower = null;

export function followThread(threadId) {
  unfollowThread();
  if (!threadId) return;

  const f = { threadId, pinned: true, frame: 0 };

  f.thread = () => document.getElementById(f.threadId);
  f.scroller = () => {
    const thread = f.thread();
    return thread ? scrollerFor(thread) : null;
  };
  f.toBottom = () => {
    const el = f.scroller();
    if (el) el.scrollTop = el.scrollHeight;
  };
  // Scroll events do not bubble, so listen in the capture phase to see whichever element scrolls.
  f.onScroll = () => {
    const el = f.scroller();
    if (el) f.pinned = distanceFromBottom(el) <= NEAR_BOTTOM_THRESHOLD_PX;
  };
  f.observer = new MutationObserver(() => {
    if (!f.pinned || f.frame) return;
    f.frame = requestAnimationFrame(() => {
      f.frame = 0;
      if (f.pinned) f.toBottom();
    });
  });

  document.addEventListener('scroll', f.onScroll, { capture: true, passive: true });
  f.observer.observe(document.body, { childList: true, subtree: true, characterData: true });
  follower = f;
  f.toBottom();
}

export function unfollowThread() {
  const f = follower;
  follower = null;
  if (!f) return;
  f.observer.disconnect();
  document.removeEventListener('scroll', f.onScroll, { capture: true });
  if (f.frame) cancelAnimationFrame(f.frame);
}

// The thread may scroll itself or, when the layout lets it grow, an ancestor does. Use whichever
// element actually scrolls right now.
function scrollerFor(el) {
  for (let node = el; node && node !== document.body; node = node.parentElement) {
    const overflowY = getComputedStyle(node).overflowY;
    if ((overflowY === 'auto' || overflowY === 'scroll') && node.scrollHeight > node.clientHeight + 1) {
      return node;
    }
  }
  return document.scrollingElement || document.documentElement;
}

export async function stop() {
  unfollowThread();
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
