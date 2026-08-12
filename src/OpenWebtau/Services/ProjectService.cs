using OpenUtau.Core;
using OpenUtau.Core.Format;
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

    /// <summary>Serializes the current project to .ustx bytes for download.</summary>
    public byte[] SaveToUstx(string fileName) {
        Directory.CreateDirectory(ScratchDir);
        string path = Path.Combine(ScratchDir, fileName);
        Ustx.Save(path, Project);
        return File.ReadAllBytes(path);
    }
}
