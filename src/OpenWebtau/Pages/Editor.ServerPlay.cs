using System.Text.Json;
using NAudio.Wave;
using OpenUtau.Core;
using OpenWebtau.Services;

namespace OpenWebtau.Pages;

/// <summary>
/// Play rendered on the server (mcp/server.mjs POST /render, by the headless
/// host): native code on every core instead of the browser's only thread, and
/// Core's phrase cache there makes replaying after an edit render only what
/// changed. The wav comes back from the playhead on and goes through the same
/// WebAudioOutput as local playback. Without a server, or when it fails,
/// PlaybackManager renders in the browser as before.
/// </summary>
public partial class Editor {
    CancellationTokenSource? serverPlayCts;
    /// The server's audio is playing (not just requested).
    bool serverPlaying;
    double serverStartMs;

    async Task PlayOnServerOrLocally() {
        var url = await Store.RenderUrlAsync();
        if (url == null) {
            PlayLocally();
            return;
        }
        var cts = new CancellationTokenSource();
        serverPlayCts = cts;
        int from = DocManager.Inst.playPosTick;
        playing = true;
        status = "Rendering on the server...";
        StateHasChanged();
        try {
            var ustx = Projects.SaveToUstx("play.ustx");
            using var content = new ByteArrayContent(ustx);
            using var response = await Http.PostAsync($"{url}?from={from}", content, cts.Token);
            if (!response.IsSuccessStatusCode) {
                throw new InvalidOperationException(await ErrorOf(response));
            }
            var wav = await response.Content.ReadAsByteArrayAsync(cts.Token);
            if (serverPlayCts != cts) return;   // paused or stopped while rendering

            var reader = new WaveFileReader(new MemoryStream(wav));
            Audio.Stop();
            Audio.Init(reader.ToSampleProvider());
            Audio.Play();
            serverStartMs = Project.timeAxis.TickPosToMsPos(from);
            serverPlaying = true;
            status = string.Empty;
            StartPlayheadTimer();
        } catch (OperationCanceledException) {
        } catch (Exception e) {
            if (serverPlayCts != cts) return;
            Serilog.Log.Warning(e, "Server render failed; rendering in the browser");
            serverPlayCts = null;
            playing = false;
            status = $"Server render failed ({e.Message}); playing from the browser";
            PlayLocally();
        }
        StateHasChanged();
    }

    static async Task<string> ErrorOf(HttpResponseMessage response) {
        try {
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            if (doc.RootElement.TryGetProperty("error", out var e) && e.GetString() is string s) return s;
        } catch { }
        return $"HTTP {(int)response.StatusCode}";
    }

    /// Ends server playback, or a render still in flight. The playhead stays
    /// where it got to, so Play continues from there with a fresh render.
    void StopServerPlayback() {
        serverPlayCts?.Cancel();
        serverPlayCts = null;
        if (serverPlaying) Audio.Stop();
        serverPlaying = false;
    }

    /// Called by the playhead loop. False once the server's audio has run out.
    bool UpdateServerPlayhead() {
        // GetPosition also notices the end of the audio and stops the output.
        Audio.GetPosition();
        if (Audio.PlaybackState != PlaybackState.Playing) return false;
        double ms = Audio is WebAudioOutput web ? web.PlayedMs : 0;
        DocManager.Inst.ExecuteCmd(new SetPlayPosTickNotification(Project.timeAxis.MsPosToTickPos(serverStartMs + ms)));
        return true;
    }
}
