using System.Text.Json;
using OpenUtau.Api;
using OpenUtau.Core;
using OpenUtau.Core.Ustx;

namespace OpenWebtau.Agent;

/// <summary>
/// What an MCP tool call needs from whoever holds the project: the editor page
/// (Pages/Editor.Agent.cs), which shows every edit live, or the headless host
/// (src/OpenWebtau.Headless), which answers when no editor tab is connected.
/// </summary>
public interface IAgentHost {
    UProject Project { get; }
    string ProjectId { get; }
    string ProjectName { get; }
    /// <summary>The track that edits without a "track" argument go to.</summary>
    int TrackIndex { get; }
    Task SelectTrack(int index);
    UVoicePart EnsurePart();
    /// <summary>Runs after every edit: redraw, autosave.</summary>
    Task AfterEdit();
    void ClearSelection();

    Task<object?> Play(int? from);
    Task<object?> Stop();
    /// <summary>ustx, ust (the current track) or wav.</summary>
    Task<object?> Export(string format);

    Task<object?> ListProjects();
    Task<object?> OpenProject(string id);
    Task<object?> CreateProject(string name);
}

/// <summary>
/// The MCP tools (mcp/server.mjs) on top of OpenUtau.Core. Edits run through the
/// same UCommand paths as the mouse, so they validate, re-phonemize and undo the
/// same way. A "track" argument switches the current track first.
/// </summary>
public class AgentCommands {
    // camelCase like the rest of the bridge; Hangul and kana stay readable.
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    readonly IAgentHost host;

    public AgentCommands(IAgentHost host) {
        this.host = host;
    }

    UProject Project => host.Project;

    /// <summary>{"ok":true,"result":...} or {"ok":false,"error":"..."}, as mcp/server.mjs expects.</summary>
    public async Task<string> Call(string method, string? argsJson) {
        try {
            using var doc = JsonDocument.Parse(string.IsNullOrEmpty(argsJson) ? "{}" : argsJson);
            var result = await Dispatch(method, doc.RootElement);
            return JsonSerializer.Serialize(new { ok = true, result }, Json);
        } catch (Exception e) {
            Serilog.Log.Warning(e, "Agent call {Method} failed", method);
            return JsonSerializer.Serialize(new { ok = false, error = e.Message }, Json);
        }
    }

    async Task<object?> Dispatch(string method, JsonElement a) {
        switch (method) {
            case "list_projects":
                return await host.ListProjects();

            case "open_project":
                return await host.OpenProject(Str(a, "id"));

            case "create_project": {
                var name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
                return await host.CreateProject(string.IsNullOrWhiteSpace(name) ? "Untitled" : name!);
            }

            case "get_project":
                return ProjectInfo(a.TryGetProperty("track", out var only) ? only.GetInt32() : null);

            case "list_voices":
                return new {
                    singers = SingerManager.Inst.Singers.Values.Select(s => new { id = s.Id, name = s.Name }),
                    phonemizers = PhonemizerFactory.GetAll().Select(f => new { id = f.type.FullName, f.tag, f.name, f.language }),
                };

            case "add_track": {
                await host.SelectTrack(TrackOps.AddTrack(Project));
                await ApplyTrackSettings(Project.tracks[host.TrackIndex], a);
                return new { track = host.TrackIndex };
            }

            case "set_track": {
                await ApplyTrackSettings(await UseTrack(a), a);
                return new { track = host.TrackIndex };
            }

            case "remove_track": {
                if (!TrackOps.RemoveTrack(Project, await UseTrack(a))) {
                    throw new InvalidOperationException("a project needs at least one track");
                }
                await host.SelectTrack(Math.Clamp(host.TrackIndex, 0, Project.tracks.Count - 1));
                await host.AfterEdit();
                return new { tracks = Project.tracks.Count };
            }

            case "add_notes": {
                await UseTrack(a);
                var part = host.EnsurePart();
                var notes = a.GetProperty("notes").EnumerateArray().Select(n => {
                    var note = Project.CreateNote(
                        Tone(n.GetProperty("tone")),
                        n.GetProperty("pos").GetInt32() - part.position,
                        Math.Max(15, n.GetProperty("dur").GetInt32()));
                    if (n.TryGetProperty("lyric", out var l)) note.lyric = l.GetString() ?? note.lyric;
                    if (note.position < 0) throw new ArgumentException("pos must be >= 0");
                    return note;
                }).ToList();
                if (notes.Count == 0) return new { added = 0 };
                DocManager.Inst.StartUndoGroup();
                DocManager.Inst.ExecuteCmd(new AddNoteCommand(part, notes));
                TrackOps.GrowPartToFit(Project, part, notes.Max(n => n.End));
                DocManager.Inst.EndUndoGroup();
                await host.AfterEdit();
                return new { added = notes.Count };
            }

            case "remove_notes": {
                await UseTrack(a);
                var part = host.EnsurePart();
                var notes = PickNotes(part, a);
                if (notes.Count == 0) return new { removed = 0 };
                DocManager.Inst.StartUndoGroup();
                DocManager.Inst.ExecuteCmd(new RemoveNoteCommand(part, notes));
                DocManager.Inst.EndUndoGroup();
                host.ClearSelection();
                await host.AfterEdit();
                return new { removed = notes.Count };
            }

            case "set_lyrics": {
                await UseTrack(a);
                var part = host.EnsurePart();
                var words = a.GetProperty("lyrics").EnumerateArray().Select(x => x.GetString() ?? "").ToArray();
                int start = a.TryGetProperty("start", out var s) ? s.GetInt32() : 0;
                var notes = part.notes.Skip(start).Take(words.Length).ToArray();
                if (notes.Length == 0) return new { set = 0 };
                DocManager.Inst.StartUndoGroup();
                DocManager.Inst.ExecuteCmd(new ChangeNoteLyricCommand(part, notes, words.Take(notes.Length).ToArray()));
                DocManager.Inst.EndUndoGroup();
                await host.AfterEdit();
                return new { set = notes.Length };
            }

            case "set_expression": {
                var track = await UseTrack(a);
                var part = host.EnsurePart();
                var abbr = a.GetProperty("abbr").GetString() ?? "";
                if (!Project.expressions.TryGetValue(abbr, out var d) || d.type == UExpressionType.Curve) {
                    throw new ArgumentException($"'{abbr}' is not a per-note expression");
                }
                var notes = PickNotes(part, a);
                float value = Math.Clamp(a.GetProperty("value").GetSingle(), d.min, d.max);
                DocManager.Inst.StartUndoGroup();
                DocManager.Inst.ExecuteCmd(new SetNotesSameExpressionCommand(Project, track, part, notes, abbr, value));
                DocManager.Inst.EndUndoGroup();
                await host.AfterEdit();
                return new { set = notes.Count, value };
            }

            case "set_tempo": {
                double bpm = a.GetProperty("bpm").GetDouble();
                if (bpm < 10 || bpm > 600) throw new ArgumentException("bpm must be 10-600");
                DocManager.Inst.StartUndoGroup();
                DocManager.Inst.ExecuteCmd(new BpmCommand(Project, bpm));
                DocManager.Inst.EndUndoGroup();
                await host.AfterEdit();
                return new { bpm };
            }

            case "play":
                return await host.Play(a.TryGetProperty("from", out var from) ? from.GetInt32() : null);

            case "stop":
                return await host.Stop();

            case "undo":
                DocManager.Inst.Undo();
                await host.AfterEdit();
                return null;

            case "redo":
                DocManager.Inst.Redo();
                await host.AfterEdit();
                return null;

            case "get_ustx": {
                var dir = Path.Combine(Path.GetTempPath(), "openwebtau-agent");
                Directory.CreateDirectory(dir);
                var path = Path.Combine(dir, "get.ustx");
                OpenUtau.Core.Format.Ustx.Save(path, Project);
                var text = File.ReadAllText(path);
                File.Delete(path);
                return text;
            }

            case "export": {
                var format = a.GetProperty("format").GetString() ?? "";
                if (format is not ("ustx" or "ust" or "wav")) throw new ArgumentException("format must be ustx, ust or wav");
                if (format == "ust") await UseTrack(a);
                return await host.Export(format);
            }
        }
        throw new ArgumentException($"Unknown method '{method}'");
    }

    object ProjectInfo(int? only) => new {
        id = host.ProjectId,
        name = host.ProjectName,
        bpm = Project.tempos.Count > 0 ? Project.tempos[0].bpm : 120,
        resolution = Project.resolution,
        beatsPerBar = Project.timeSignatures[0].beatPerBar,
        beatUnit = Project.timeSignatures[0].beatUnit,
        currentTrack = host.TrackIndex,
        tracks = Project.tracks.Select((t, i) => new {
            index = i,
            name = t.TrackName,
            singer = t.Singer?.Name,
            singerId = t.Singer?.Id,
            phonemizer = t.Phonemizer?.GetType().FullName,
            mute = t.Mute,
            solo = t.Solo,
            volume = t.Volume,
            pan = t.Pan,
            notes = only != null && only != i ? null : Project.parts.OfType<UVoicePart>()
                // The editor edits a track's first voice part only; report the same one.
                .Where(p => p.trackNo == i).Take(1)
                .SelectMany(p => p.notes.Select((n, k) => new {
                    index = k,
                    pos = n.position + p.position,
                    dur = n.duration,
                    tone = n.tone,
                    name = MusicMath.GetToneName(n.tone),
                    lyric = n.lyric,
                })).ToList(),
        }),
    };

    async Task<UTrack> UseTrack(JsonElement a) {
        if (a.TryGetProperty("track", out var t)) {
            int index = t.GetInt32();
            if (index < 0 || index >= Project.tracks.Count) {
                throw new ArgumentException($"track must be 0-{Project.tracks.Count - 1}");
            }
            await host.SelectTrack(index);
        }
        host.EnsurePart();
        return Project.tracks[host.TrackIndex];
    }

    /// Notes by "indices", else by a [from, to) tick range, else all of them.
    static List<UNote> PickNotes(UVoicePart part, JsonElement a) {
        var all = part.notes.ToList();
        if (a.TryGetProperty("indices", out var ix)) {
            return ix.EnumerateArray().Select(x => x.GetInt32())
                .Where(i => i >= 0 && i < all.Count).Select(i => all[i]).ToList();
        }
        int from = a.TryGetProperty("from", out var f) ? f.GetInt32() : int.MinValue;
        int to = a.TryGetProperty("to", out var e) ? e.GetInt32() : int.MaxValue;
        return all.Where(n => n.position + part.position >= from && n.position + part.position < to).ToList();
    }

    static int Tone(JsonElement v) {
        int tone = v.ValueKind == JsonValueKind.Number ? v.GetInt32() : MusicMath.NameToTone(v.GetString() ?? "");
        if (tone < 0 || tone > 127) throw new ArgumentException($"bad tone {v}; use a MIDI number or a name like C4 (=60)");
        return tone;
    }

    static string Str(JsonElement a, string name) =>
        a.TryGetProperty(name, out var v) && v.GetString() is string s && s.Length > 0
            ? s : throw new ArgumentException($"{name} is required");

    async Task ApplyTrackSettings(UTrack track, JsonElement a) {
        if (a.TryGetProperty("singer", out var s)) {
            var key = s.GetString() ?? "";
            var singer = SingerManager.Inst.Singers.Values.FirstOrDefault(x => x.Id == key || x.Name == key)
                ?? throw new ArgumentException($"no singer '{key}'; see list_voices");
            TrackOps.SetSinger(Project, track, singer);
        }
        if (a.TryGetProperty("phonemizer", out var p)) {
            var key = p.GetString() ?? "";
            var f = PhonemizerFactory.GetAll().FirstOrDefault(x => x.type.FullName == key || x.tag == key)
                ?? throw new ArgumentException($"no phonemizer '{key}'; see list_voices");
            TrackOps.SetPhonemizer(Project, track, f);
        }
        if (a.TryGetProperty("name", out var n) && n.GetString() is string name && name != track.TrackName) {
            DocManager.Inst.StartUndoGroup();
            DocManager.Inst.ExecuteCmd(new RenameTrackCommand(Project, track, name));
            DocManager.Inst.EndUndoGroup();
        }
        if (a.TryGetProperty("volume", out var v)) track.Volume = Math.Clamp(v.GetDouble(), -24, 12);
        if (a.TryGetProperty("pan", out var pan)) track.Pan = Math.Clamp(pan.GetDouble(), -100, 100);
        if (a.TryGetProperty("mute", out var m)) track.Mute = m.GetBoolean();
        if (a.TryGetProperty("solo", out var so)) track.Solo = so.GetBoolean();
        await host.AfterEdit();
    }
}
