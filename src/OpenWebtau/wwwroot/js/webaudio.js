// Web Audio backend for OpenUtau's IAudioOutput.
//
// ponytail: schedules back-to-back AudioBufferSourceNodes instead of an
// AudioWorklet. ~200ms latency, which is fine for transport playback but
// audible when auditioning single notes. Move to an AudioWorklet + SharedArrayBuffer
// ring if live note preview needs to feel tight (needs COOP/COEP headers).

let ctx = null;
let gain = null;
// Audio-clock timestamp where the next chunk should start.
let nextStartTime = 0;
// Audio-clock timestamp of the first sample of the current playback run.
let runStartTime = 0;
let pausedAtMs = 0;
let running = false;
const scheduled = new Set();

const CHANNELS = 2;

export function init(sampleRate) {
    if (ctx == null) {
        ctx = new AudioContext({ sampleRate, latencyHint: 'playback' });
        gain = ctx.createGain();
        gain.connect(ctx.destination);
    }
    reset();
    return ctx.sampleRate;
}

function reset() {
    for (const node of scheduled) {
        try { node.stop(); } catch { /* already ended */ }
    }
    scheduled.clear();
    nextStartTime = 0;
    runStartTime = 0;
}

export function start() {
    if (ctx == null) return;
    // Browsers hand back a suspended context until a user gesture resumes it.
    ctx.resume();
    running = true;
    nextStartTime = 0;
    runStartTime = 0;
}

export function pause() {
    running = false;
    pausedAtMs = playedMs();
    ctx?.suspend();
}

export function stop() {
    running = false;
    pausedAtMs = 0;
    reset();
    ctx?.suspend();
}

// Interleaved stereo float samples, as read from the C# sample provider.
// .NET marshals Span<byte> as a MemoryView, not a TypedArray, so buffer/byteOffset
// are undefined on it; slice() copies the bytes out into a real Uint8Array.
export function enqueue(bytes) {
    if (ctx == null || !running) return;
    const u8 = typeof bytes.slice === 'function' ? bytes.slice() : new Uint8Array(bytes);
    const interleaved = new Float32Array(u8.buffer, u8.byteOffset, u8.byteLength / 4);
    const frames = interleaved.length / CHANNELS;
    if (frames === 0) return;

    const buffer = ctx.createBuffer(CHANNELS, frames, ctx.sampleRate);
    for (let ch = 0; ch < CHANNELS; ch++) {
        const out = buffer.getChannelData(ch);
        for (let i = 0; i < frames; i++) {
            out[i] = interleaved[i * CHANNELS + ch];
        }
    }

    const node = ctx.createBufferSource();
    node.buffer = buffer;
    node.connect(gain);
    // First chunk of a run starts slightly ahead so scheduling jitter can't underrun it.
    if (nextStartTime === 0) {
        nextStartTime = ctx.currentTime + 0.1;
        runStartTime = nextStartTime;
    } else if (nextStartTime < ctx.currentTime) {
        // Underran: the pump fell behind. Resync rather than pile up late nodes.
        nextStartTime = ctx.currentTime;
    }
    node.start(nextStartTime);
    nextStartTime += frames / ctx.sampleRate;

    scheduled.add(node);
    node.onended = () => scheduled.delete(node);
}

// Seconds of audio already handed to the device but not yet played.
export function bufferedAhead() {
    if (ctx == null || nextStartTime === 0) return 0;
    return Math.max(0, nextStartTime - ctx.currentTime);
}

export function playedMs() {
    if (ctx == null || !running || runStartTime === 0) return pausedAtMs;
    return Math.max(0, (ctx.currentTime - runStartTime) * 1000);
}

export function setVolume(v) {
    if (gain != null) gain.gain.value = v;
}
