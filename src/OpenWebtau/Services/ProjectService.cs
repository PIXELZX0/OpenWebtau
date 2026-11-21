using System.Text;
using OpenUtau.Classic;
using OpenUtau.Core;
using NAudio.Wave;
using OpenUtau.Core.Format;
using OpenUtau.Core.Render;
using OpenUtau.Core.SignalChain;
using OpenUtau.Core.Ustx;
using Microsoft.AspNetCore.Components.WebAssembly.Http;
using Serilog;

namespace OpenWebtau.Services;

/// <summary>
/// Bridges browser file I/O to OpenUtau.Core, which is written against a real
/// filesystem. The wasm runtime gives us an in-memory VFS, so uploaded files are
/// staged there and Core loads them by path, unmodified.
/// </summary>
public class ProjectService {
    const string ScratchDir = "/tmp/openwebtau";

    public UProject Project => DocManager.Inst.Project;

    /// <summary>Raised after the current project is swapped or edited.</summary>
    public event Action? Changed;

    public void NotifyChanged() => Changed?.Invoke();

    public void NewProject() {
        DocManager.Inst.ExecuteCmd(new LoadProjectNotification(Ustx.Create()));
        NotifyChanged();
    }

    public void Open(string fileName, byte[] content) {
        Directory.CreateDirectory(ScratchDir);
        string path = Path.Combine(ScratchDir, fileName);
        File.WriteAllBytes(path, content);
        Formats.LoadProject(new[] { path });
        Log.Information("Opened project {FileName}: {Tracks} tracks, {Parts} parts",
            fileName, Project.tracks.Count, Project.parts.Count);
        NotifyChanged();
    }

    /// <summary>
    /// Installs an uploaded UTAU voicebank archive (.zip / .uar / .vogeon) into the
    /// browser filesystem, then rescans so it shows up as a singer.
    /// </summary>
    public void InstallSinger(string fileName, byte[] content, string encoding = "shift_jis") {
        Directory.CreateDirectory(ScratchDir);
        string path = Path.Combine(ScratchDir, fileName);
        File.WriteAllBytes(path, content);

        // Entry names and oto.ini/character.txt share one legacy code page: shift-jis
        // for Japanese banks, cp949 for Korean ones, gbk for Chinese.
        var enc = Encoding.GetEncoding(encoding);
        var installer = new VoicebankInstaller(
            PathManager.Inst.SingersInstallPath,
            (progress, message) => Log.Information("Install {Progress}%: {Message}", (int)progress, message),
            enc,
            enc);
        installer.Install(path, SingerTypeUtils.SingerTypeNames[USingerType.Classic]);

        File.Delete(path);
        SingerManager.Inst.SearchAllSingers();
        NotifyChanged();
    }

    /// <summary>
    /// Copies a bank the server extracted into the browser filesystem. It comes
    /// as one tar stream, unpacked as it downloads, so peak memory is the bank
    /// itself rather than an archive plus its contents. Call RescanSingers after.
    /// </summary>
    public async Task LoadBankAsync(HttpClient http, string url, string dir, Action<long>? progress = null) {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.SetBrowserResponseStreamingEnabled(true);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync();
        var root = PathManager.Inst.SingersPath;
        // A reinstall replaces the folder rather than leaving stale samples behind.
        var target = Path.Combine(root, dir);
        if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
        int files = await BankTar.ExtractAsync(stream, root, dir, progress);
        Log.Information("Loaded voicebank {Dir}: {Files} files", dir, files);
    }

    public void RescanSingers() {
        SingerManager.Inst.SearchAllSingers();
        NotifyChanged();
    }

    /// <summary>Writes one voice part as a classic UTAU .ust (shift-jis).</summary>
    public byte[] SavePartToUst(UVoicePart part) {
        Directory.CreateDirectory(ScratchDir);
        string path = Path.Combine(ScratchDir, "export.ust");
        OpenUtau.Classic.Ust.SavePart(Project, part, path);
        var bytes = File.ReadAllBytes(path);
        File.Delete(path);
        return bytes;
    }

    /// <summary>
    /// Renders the whole project to a 16-bit wav. PlaybackManager.RenderMixdown wraps
    /// the same work in Task.Run, which never runs on the single browser thread, so
    /// this drives the render engine directly.
    /// </summary>
    public byte[] RenderMixdownWav() {
        Directory.CreateDirectory(ScratchDir);
        string path = Path.Combine(ScratchDir, "mixdown.wav");
        CancellationTokenSource? cancellation = null;
        var engine = new RenderEngine(Project);
        var mix = engine.RenderMixdown(DocManager.Inst.MainScheduler, ref cancellation, wait: true).Item1;
        WaveFileWriter.CreateWaveFile16(path, new ExportAdapter(mix));
        var bytes = File.ReadAllBytes(path);
        File.Delete(path);
        return bytes;
    }

    /// <summary>Serializes the current project to .ustx bytes for download.</summary>
    public byte[] SaveToUstx(string fileName) {
        Directory.CreateDirectory(ScratchDir);
        string path = Path.Combine(ScratchDir, fileName);
        Ustx.Save(path, Project);
        return File.ReadAllBytes(path);
    }
}
