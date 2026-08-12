using System.Runtime.InteropServices;
using System.Runtime.InteropServices.JavaScript;
using NAudio.Wave;
using OpenUtau.Audio;

namespace OpenWebtau.Services;

/// <summary>
/// IAudioOutput backed by Web Audio. OpenUtau pulls audio from an ISampleProvider,
/// the browser wants it pushed, so a timer pumps samples across the gap.
/// </summary>
public partial class WebAudioOutput : IAudioOutput, IAsyncDisposable {
    const int SampleRate = 44100;
    const int Channels = 2;
    // Frames per pump chunk. 4096 @ 44.1k = ~93ms.
    const int ChunkFrames = 4096;
    // Keep this much audio queued ahead of the playhead.
    const double TargetBufferSeconds = 0.4;

    ISampleProvider? sampleProvider;
    float[] buffer = new float[ChunkFrames * Channels];
    CancellationTokenSource? pumpCts;
    bool initialized;
    bool eof;

    public PlaybackState PlaybackState { get; private set; } = PlaybackState.Stopped;
    public int DeviceNumber => 0;

    public List<AudioOutputDevice> GetOutputDevices() => new() {
        new AudioOutputDevice { name = "Browser default", api = "WebAudio", deviceNumber = 0, guid = Guid.Empty },
    };

    // The browser picks the device; there is no enumeration API worth exposing.
    public void SelectDevice(Guid guid, int deviceNumber) { }

    public async Task InitJsAsync() {
        if (initialized) return;
        await JSHost.ImportAsync("webaudio", "../js/webaudio.js");
        JsInit(SampleRate);
        initialized = true;
    }

    public void Init(ISampleProvider sampleProvider) {
        this.sampleProvider = sampleProvider.WaveFormat.Channels == Channels
            ? sampleProvider
            : sampleProvider.ToStereo();
        eof = false;
    }

    public void Play() {
        if (!initialized) {
            throw new InvalidOperationException("InitJsAsync must run once before playback.");
        }
        if (PlaybackState == PlaybackState.Playing) return;
        eof = false;
        JsStart();
        PlaybackState = PlaybackState.Playing;
        StartPump();
    }

    public void Pause() {
        if (PlaybackState != PlaybackState.Playing) return;
        StopPump();
        JsPause();
        PlaybackState = PlaybackState.Paused;
    }

    public void Stop() {
        StopPump();
        if (initialized) {
            JsStop();
        }
        PlaybackState = PlaybackState.Stopped;
    }

    /// <summary>
    /// Position in bytes, matching what MiniAudioOutput reports: PlaybackManager
    /// divides by sizeof(float) to recover a sample count.
    /// </summary>
    public long GetPosition() {
        if (eof && PlaybackState == PlaybackState.Playing && JsBufferedAhead() <= 0) {
            Stop();
        }
        if (!initialized) return 0;
        return (long)(JsPlayedMs() / 1000 * SampleRate * 2 * Channels);
    }

    void StartPump() {
        StopPump();
        pumpCts = new CancellationTokenSource();
        _ = PumpAsync(pumpCts.Token);
    }

    void StopPump() {
        pumpCts?.Cancel();
        pumpCts = null;
    }

    async Task PumpAsync(CancellationToken token) {
        // Wasm is single-threaded, so this yields to the JS event loop between chunks.
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(20));
        while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false)) {
            if (sampleProvider == null) continue;
            while (JsBufferedAhead() < TargetBufferSeconds) {
                int read = sampleProvider.Read(buffer, 0, buffer.Length);
                if (read == 0) {
                    eof = true;
                    return;
                }
                if (read < buffer.Length) {
                    Array.Clear(buffer, read, buffer.Length - read);
                }
                JsEnqueue(MemoryMarshal.AsBytes(buffer.AsSpan()));
            }
        }
    }

    public async ValueTask DisposeAsync() {
        Stop();
        await Task.CompletedTask;
    }

    [JSImport("init", "webaudio")]
    internal static partial int JsInit(int sampleRate);

    [JSImport("start", "webaudio")]
    internal static partial void JsStart();

    [JSImport("pause", "webaudio")]
    internal static partial void JsPause();

    [JSImport("stop", "webaudio")]
    internal static partial void JsStop();

    // MemoryView only marshals byte/int/double spans, so floats cross as raw bytes
    // and JS reinterprets them as a Float32Array.
    [JSImport("enqueue", "webaudio")]
    internal static partial void JsEnqueue([JSMarshalAs<JSType.MemoryView>] Span<byte> interleavedBytes);

    [JSImport("bufferedAhead", "webaudio")]
    internal static partial double JsBufferedAhead();

    [JSImport("playedMs", "webaudio")]
    internal static partial double JsPlayedMs();
}
