using System.Text;
using OpenUtau.Classic;
using OpenUtau.Core;
using NAudio.Wave;
using OpenUtau.Core.Format;
using OpenUtau.Core.Render;
using OpenUtau.Core.SignalChain;
using OpenUtau.Core.Ustx;
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
    public void InstallSinger(string fileName, byte[] content) {
        Directory.CreateDirectory(ScratchDir);
        string path = Path.Combine(ScratchDir, fileName);
        File.WriteAllBytes(path, content);

        // Archive entry names in UTAU banks are usually shift-jis, and so is the
        // text inside oto.ini and character.txt.
        var shiftJis = Encoding.GetEncoding("shift_jis");
        var installer = new VoicebankInstaller(
            PathManager.Inst.SingersInstallPath,
            (progress, message) => Log.Information("Install {Progress}%: {Message}", (int)progress, message),
            shiftJis,
            shiftJis);
        installer.Install(path, SingerTypeUtils.SingerTypeNames[USingerType.Classic]);

        File.Delete(path);
        SingerManager.Inst.SearchAllSingers();
        NotifyChanged();
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
