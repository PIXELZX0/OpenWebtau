using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using OpenUtau.Api;
using OpenUtau.Core;
using OpenUtau.Core.Format;
using OpenUtau.Core.Ustx;

namespace OpenWebtau.Pages;

/// <summary>
/// Commands from the MCP bridge (mcp/server.mjs). They run through the same
/// UCommand paths as the mouse, so the user sees every edit live and can undo it.
/// A "track" argument switches the visible track, so the agent edits in plain view.
/// </summary>
public partial class Editor {
    IJSObjectReference? agentModule;
    string agentState = "off";

    async Task ToggleAgent() {
        agentModule ??= await JS.InvokeAsync<IJSObjectReference>("import", "./js/agent.js");
        if (agentState == "off") {
            await agentModule.InvokeVoidAsync("start", selfRef);
        } else {
            await agentModule.InvokeVoidAsync("stop");
            agentState = "off";
        }
    }

    /// Reconnects after a reload when the user left the bridge switched on.
    async Task ResumeAgent() {
        agentModule ??= await JS.InvokeAsync<IJSObjectReference>("import", "./js/agent.js");
        if (await agentModule.InvokeAsync<bool>("wasEnabled")) {
            await agentModule.InvokeVoidAsync("start", selfRef);
        }
    }

    [JSInvokable]
    public Task OnAgentState(string state) {
        agentState = state;
        StateHasChanged();
        return Task.CompletedTask;
    }

    [JSInvokable]
    public async Task<string> AgentCall(string method, string argsJson) {
        try {
            using var doc = JsonDocument.Parse(string.IsNullOrEmpty(argsJson) ? "{}" : argsJson);
            var result = await Dispatch(method, doc.RootElement);
            StateHasChanged();
            return JsonSerializer.Serialize(new { ok = true, result });
        } catch (Exception e) {
            Serilog.Log.Warning(e, "Agent call {Method} failed", method);
            return JsonSerializer.Serialize(new { ok = false, error = e.Message });
        }
    }

    async Task<object?> Dispatch(string method, JsonElement a) {
        switch (method) {
            case "get_project":
                return ProjectInfo(a.TryGetProperty("track", out var only) ? only.GetInt32() : null);

            case "list_voices":
                return new {
                    singers = SingerManager.Inst.Singers.Values.Select(s => new { id = s.Id, name = s.Name }),
                    phonemizers = PhonemizerFactory.GetAll().Select(f => new { id = f.type.FullName, f.tag, f.name, f.language }),
                };

            case "add_track": {
                await AddTrack();
                await ApplyTrackSettings(Project.tracks[trackIndex], a);
                return new { track = trackIndex };
            }

            case "set_track": {
                await ApplyTrackSettings(await UseTrack(a), a);
                return new { track = trackIndex };
            }

            case "remove_track": {
                if (Project.tracks.Count <= 1) throw new InvalidOperationException("a project needs at least one track");
                await RemoveTrack(await UseTrack(a), confirm: false);
                return new { tracks = Project.tracks.Count };
            }

            case "add_notes": {
                await UseTrack(a);
                var part = EnsurePart();
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
                GrowPartToFit(part, notes.Max(n => n.End));
                DocManager.Inst.EndUndoGroup();
                await AfterEdit();
                return new { added = notes.Count };
            }

            case "remove_notes": {
                await UseTrack(a);
                var part = EnsurePart();
                var notes = PickNotes(part, a);
                if (notes.Count == 0) return new { removed = 0 };
                DocManager.Inst.StartUndoGroup();
                DocManager.Inst.ExecuteCmd(new RemoveNoteCommand(part, notes));
                DocManager.Inst.EndUndoGroup();
                selection.Clear();
                await AfterEdit();
                return new { removed = notes.Count };
            }

            case "set_lyrics": {
                await UseTrack(a);
                var part = EnsurePart();
                var words = a.GetProperty("lyrics").EnumerateArray().Select(x => x.GetString() ?? "").ToArray();
                int start = a.TryGetProperty("start", out var s) ? s.GetInt32() : 0;
                var notes = part.notes.Skip(start).Take(words.Length).ToArray();
                if (notes.Length == 0) return new { set = 0 };
                DocManager.Inst.StartUndoGroup();
                DocManager.Inst.ExecuteCmd(new ChangeNoteLyricCommand(part, notes, words.Take(notes.Length).ToArray()));
                DocManager.Inst.EndUndoGroup();
                await AfterEdit();
                return new { set = notes.Length };
            }

            case "set_expression": {
                var track = await UseTrack(a);
                var part = EnsurePart();
                var abbr = a.GetProperty("abbr").GetString() ?? "";
                if (!Project.expressions.TryGetValue(abbr, out var d) || d.type == UExpressionType.Curve) {
                    throw new ArgumentException($"'{abbr}' is not a per-note expression");
                }
                var notes = PickNotes(part, a);
                float value = Math.Clamp(a.GetProperty("value").GetSingle(), d.min, d.max);
                DocManager.Inst.StartUndoGroup();
                DocManager.Inst.ExecuteCmd(new SetNotesSameExpressionCommand(Project, track, part, notes, abbr, value));
                DocManager.Inst.EndUndoGroup();
                await AfterEdit();
                return new { set = notes.Count, value };
            }

            case "set_tempo": {
                double bpm = a.GetProperty("bpm").GetDouble();
                if (bpm < 10 || bpm > 600) throw new ArgumentException("bpm must be 10-600");
                DocManager.Inst.StartUndoGroup();
                DocManager.Inst.ExecuteCmd(new BpmCommand(Project, bpm));
                DocManager.Inst.EndUndoGroup();
                await AfterEdit();
                return new { bpm };
            }

            case "play": {
                if (a.TryGetProperty("from", out var from)) OnSeek(from.GetInt32());
                if (!playing) PlayOrPause();
                return new { playing };
            }

            case "stop":
                Stop();
                return new { playing };

            case "undo": await OnUndo(); return null;
            case "redo": await OnRedo(); return null;

            case "get_ustx":
                return System.Text.Encoding.UTF8.GetString(Projects.SaveToUstx(projectName + ".ustx"));

            case "export": {
                var format = a.GetProperty("format").GetString();
                switch (format) {
                    case "ustx": await Download(); break;
                    case "ust": await UseTrack(a); await ExportUst(); break;
                    case "wav": await ExportWav(); break;
                    default: throw new ArgumentException("format must be ustx, ust or wav");
                }
                return status;
            }
        }
        throw new ArgumentException($"Unknown method '{method}'");
    }

    object ProjectInfo(int? only) => new {
        name = projectName,
        bpm = Bpm,
        resolution = Project.resolution,
        beatsPerBar = Project.timeSignatures[0].beatPerBar,
        beatUnit = Project.timeSignatures[0].beatUnit,
        currentTrack = trackIndex,
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
            await SelectTrack(index);
        }
        return Project.tracks[trackIndex];
    }

    /// Notes by "indices", else by a [from, to) tick range, else all of them.
    List<UNote> PickNotes(UVoicePart part, JsonElement a) {
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

    async Task ApplyTrackSettings(UTrack track, JsonElement a) {
        if (a.TryGetProperty("singer", out var s)) {
            var key = s.GetString() ?? "";
            var singer = SingerManager.Inst.Singers.Values.FirstOrDefault(x => x.Id == key || x.Name == key)
                ?? throw new ArgumentException($"no singer '{key}'; see list_voices");
            OnSingerChanged(track, new ChangeEventArgs { Value = singer.Id });
        }
        if (a.TryGetProperty("phonemizer", out var p)) {
            var key = p.GetString() ?? "";
            var f = PhonemizerFactory.GetAll().FirstOrDefault(x => x.type.FullName == key || x.tag == key)
                ?? throw new ArgumentException($"no phonemizer '{key}'; see list_voices");
            OnPhonemizerChanged(track, new ChangeEventArgs { Value = f.type.FullName });
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
        await AfterEdit();
    }
}
