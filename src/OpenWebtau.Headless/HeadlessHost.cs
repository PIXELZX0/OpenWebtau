using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using OpenUtau.Classic;
using OpenUtau.Core;
using OpenUtau.Core.Format;
using OpenUtau.Core.Ustx;
using OpenWebtau.Agent;
using Serilog;

namespace OpenWebtau.Headless;

/// <summary>
/// The agent's project when no editor tab is open. Reads and writes the same
/// files as the web app's server storage (wwwroot/js/store.js):
///
///   projects/&lt;id&gt;.ustx + &lt;id&gt;.json   {id, name, tracks, notes, updated}
///   singers/&lt;archive&gt; + &lt;archive&gt;.json  {name, encoding}
///   exports/                            written by the export tool
///
/// Every edit saves right away, so a tab opened later sees it.
/// </summary>
sealed class HeadlessHost : IAgentHost {
    static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    const int PhonemizeTimeoutMs = 60_000;

    readonly string projectsDir, singersDir, exportsDir, installedFile;
    readonly BlockingCollection<Action> queue;
    readonly AgentCommands commands;

    string projectId = "";
    string projectName = "Untitled";
    int trackIndex;
    /// Write time of the .ustx as this host last saved or loaded it. A different
    /// one means the editor saved over it since, so reload before editing.
    DateTime projectStamp;

    public HeadlessHost(string dataDir, BlockingCollection<Action> queue) {
        projectsDir = Path.Combine(dataDir, "projects");
        singersDir = Path.Combine(dataDir, "singers");
        exportsDir = Path.Combine(dataDir, "exports");
        installedFile = Path.Combine(dataDir, ".headless", "installed.json");
        Directory.CreateDirectory(projectsDir);
        Directory.CreateDirectory(singersDir);
        this.queue = queue;
        commands = new AgentCommands(this);
        // Banks extracted by an earlier run; SyncSingers only adds new ones.
        SingerManager.Inst.SearchAllSingers();
    }

    public UProject Project => DocManager.Inst.Project;
    public string ProjectId => projectId;
    public string ProjectName => projectName;
    public int TrackIndex => trackIndex;

    public string Call(string method, string args) {
        SyncSingers();
        if (method is not ("list_projects" or "open_project" or "create_project")) {
            SyncProject();
        }
        var task = commands.Call(method, args);
        Pump(() => task.IsCompleted, int.MaxValue);
        return task.Result;
    }

    /// Runs queued main-thread work (phonemizer results, posted continuations)
    /// until done() or the timeout.
    bool Pump(Func<bool> done, int timeoutMs) {
        var clock = Stopwatch.StartNew();
        while (!done()) {
            if (clock.ElapsedMilliseconds > timeoutMs) return false;
            if (queue.TryTake(out var action, 20)) {
                try { action(); } catch (Exception e) { Log.Error(e, "Queued work failed"); }
            }
        }
        return true;
    }

    // --- IAgentHost -------------------------------------------------------

    public Task SelectTrack(int index) {
        trackIndex = index;
        EnsurePart();
        return Task.CompletedTask;
    }

    public UVoicePart EnsurePart() => TrackOps.EnsurePart(Project, ref trackIndex);

    public Task AfterEdit() {
        Save();
        return Task.CompletedTask;
    }

    public void ClearSelection() { }

    public Task<object?> Play(int? from) => throw new InvalidOperationException(
        "Playback needs the editor open in a browser (Agent switched on). Use export with format \"wav\" to render a file instead.");

    public Task<object?> Stop() => Play(null);

    public Task<object?> Export(string format) {
        Directory.CreateDirectory(exportsDir);
        string name = format switch {
            "ust" => $"{projectName} - {Project.tracks[trackIndex].TrackName}.ust",
            _ => $"{projectName}.{format}",
        };
        name = string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        string path = Path.Combine(exportsDir, name);
        switch (format) {
            case "ustx":
                Ustx.Save(path, Project);
                break;
            case "ust":
                Ust.SavePart(Project, EnsurePart(), path);
                break;
            case "wav":
                RenderWav(path);
                break;
        }
        return Task.FromResult<object?>(new {
            file = "exports/" + name,
            path,
            note = "Saved in the data directory (served at /data/exports/ in the Docker image).",
        });
    }

    public Task<object?> ListProjects() => Task.FromResult<object?>(ReadProjectList());

    public Task<object?> OpenProject(string id) {
        var meta = ReadProjectList().FirstOrDefault(p => p.Id == id)
            ?? throw new ArgumentException($"no project '{id}'; see list_projects");
        Load(meta);
        return Task.FromResult<object?>(new { id, name = projectName });
    }

    public Task<object?> CreateProject(string name) {
        projectId = Guid.NewGuid().ToString("N");
        projectName = name;
        trackIndex = 0;
        DocManager.Inst.ExecuteCmd(new LoadProjectNotification(Ustx.Create()));
        EnsurePart();
        Save();
        return Task.FromResult<object?>(new { id = projectId, name });
    }

    // --- projects ---------------------------------------------------------

    sealed record ProjectMeta(string Id, string Name, long Updated, int Tracks, int Notes);

    List<ProjectMeta> ReadProjectList() {
        var list = new List<ProjectMeta>();
        foreach (var file in Directory.EnumerateFiles(projectsDir, "*.json")) {
            try {
                var meta = JsonSerializer.Deserialize<ProjectMeta>(File.ReadAllText(file), Json);
                if (meta?.Id != null && File.Exists(UstxPath(meta.Id))) list.Add(meta);
            } catch (Exception e) {
                Log.Warning(e, "Skipping unreadable project sidecar {File}", file);
            }
        }
        return list.OrderByDescending(p => p.Updated).ToList();
    }

    string UstxPath(string id) => Path.Combine(projectsDir, id + ".ustx");

    /// Makes sure a project is open and current with what is on disk: the most
    /// recent one when none is open yet, a new one when there are none.
    void SyncProject() {
        if (projectId != "") {
            var path = UstxPath(projectId);
            if (File.Exists(path)) {
                if (File.GetLastWriteTimeUtc(path) == projectStamp) return;
                var meta = ReadProjectList().FirstOrDefault(p => p.Id == projectId);
                if (meta != null) {
                    Log.Information("Project {Id} changed on disk; reloading", projectId);
                    int keep = trackIndex;
                    Load(meta);
                    trackIndex = Math.Clamp(keep, 0, Math.Max(0, Project.tracks.Count - 1));
                    return;
                }
            }
            projectId = "";   // deleted from the project list
        }
        var latest = ReadProjectList().FirstOrDefault();
        if (latest != null) {
            Load(latest);
        } else {
            CreateProject("Untitled");
        }
    }

    void Load(ProjectMeta meta) {
        var path = UstxPath(meta.Id);
        Formats.LoadProject(new[] { path });
        projectId = meta.Id;
        projectName = meta.Name;
        projectStamp = File.GetLastWriteTimeUtc(path);
        trackIndex = 0;
        EnsurePart();
    }

    void Save() {
        // Write beside the target and rename, so the editor never reads half a file.
        var path = UstxPath(projectId);
        var tmp = Path.Combine(projectsDir, $".{projectId}.ustx.tmp");
        Ustx.Save(tmp, Project);
        File.Move(tmp, path, overwrite: true);
        projectStamp = File.GetLastWriteTimeUtc(path);

        int notes = Project.parts.OfType<UVoicePart>().Sum(p => p.notes.Count);
        var meta = new {
            id = projectId, name = projectName, tracks = Project.tracks.Count, notes,
            updated = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };
        var metaTmp = Path.Combine(projectsDir, $".{projectId}.json.tmp");
        File.WriteAllText(metaTmp, JsonSerializer.Serialize(meta));
        File.Move(metaTmp, Path.Combine(projectsDir, projectId + ".json"), overwrite: true);
    }

    // --- voicebanks -------------------------------------------------------

    sealed record SingerMeta(string Name, string? Encoding);

    /// Installs archives the web app uploaded since the last call. installed.json
    /// remembers which archive versions are already extracted.
    void SyncSingers() {
        Dictionary<string, string> installed;
        try {
            installed = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(installedFile)) ?? new();
        } catch {
            installed = new();
        }
        bool changed = false;
        foreach (var sidecar in Directory.EnumerateFiles(singersDir, "*.json")) {
            SingerMeta? meta;
            try { meta = JsonSerializer.Deserialize<SingerMeta>(File.ReadAllText(sidecar), Json); } catch { continue; }
            if (meta?.Name == null) continue;
            var archive = Path.Combine(singersDir, meta.Name);
            if (Path.GetDirectoryName(archive) != singersDir || !File.Exists(archive)) continue;
            var info = new FileInfo(archive);
            var stamp = $"{info.Length}:{info.LastWriteTimeUtc.Ticks}";
            if (installed.TryGetValue(meta.Name, out var had) && had == stamp) continue;
            try {
                Install(archive, meta.Encoding ?? "shift_jis");
                Log.Information("Installed voicebank {Name}", meta.Name);
            } catch (Exception e) {
                Log.Error(e, "Failed to install voicebank {Name}", meta.Name);
            }
            // Recorded even on failure, so a broken archive is not retried on every call.
            installed[meta.Name] = stamp;
            changed = true;
        }
        if (!changed) return;
        Directory.CreateDirectory(Path.GetDirectoryName(installedFile)!);
        File.WriteAllText(installedFile, JsonSerializer.Serialize(installed));
        SingerManager.Inst.SearchAllSingers();
        // Singers are referenced by id in the project; re-resolve them.
        if (projectId != "") projectStamp = default;
    }

    static void Install(string archive, string encoding) {
        // Entry names and oto.ini/character.txt share one legacy code page, as in
        // the web app's installer (Services/ProjectService.cs).
        var enc = Encoding.GetEncoding(encoding);
        var installer = new VoicebankInstaller(
            PathManager.Inst.SingersInstallPath, (_, _) => { }, enc, enc);
        installer.Install(archive, SingerTypeUtils.SingerTypeNames[USingerType.Classic]);
    }

    // --- rendering --------------------------------------------------------

    void RenderWav(string path) {
        // Phonemizer results arrive through the main queue; rendering before they
        // land would sing the previous lyrics or nothing.
        if (!Pump(() => Project.parts.OfType<UVoicePart>().All(p => p.PhonemesUpToDate), PhonemizeTimeoutMs)) {
            throw new TimeoutException("Phonemizing did not finish in time.");
        }
        // The public export path; it reports failures as notifications, not exceptions.
        var errors = new ErrorCatcher();
        DocManager.Inst.AddSubscriber(errors);
        try {
            File.Delete(path);
            var task = PlaybackManager.Inst.RenderMixdown(Project, path);
            Pump(() => task.IsCompleted, int.MaxValue);
            Pump(() => queue.Count == 0, 1000);   // notifications it posted on the way
        } finally {
            DocManager.Inst.RemoveSubscriber(errors);
        }
        if (errors.Error != null) throw new InvalidOperationException("Render failed: " + errors.Error);
        if (!File.Exists(path)) throw new InvalidOperationException("Render failed.");
    }

    sealed class ErrorCatcher : ICmdSubscriber {
        public string? Error;
        public void OnNext(UCommand cmd, bool isUndo) {
            if (cmd is ErrorMessageNotification n) {
                Error ??= n.e?.InnerException?.Message ?? n.e?.Message ?? n.message;
            }
        }
    }
}
