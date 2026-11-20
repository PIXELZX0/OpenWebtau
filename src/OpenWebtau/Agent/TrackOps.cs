using OpenUtau.Api;
using OpenUtau.Core;
using OpenUtau.Core.Ustx;
using OpenWebtau.Phonemizers;

namespace OpenWebtau.Agent;

/// <summary>
/// Track and part edits shared by the editor page and the headless host
/// (src/OpenWebtau.Headless). Plain OpenUtau.Core, no Blazor, so both compile it.
/// Every edit is a UCommand in its own undo group.
/// </summary>
public static class TrackOps {
    public static PhonemizerFactory AutoFactory => PhonemizerFactory.Get(typeof(AutoPhonemizer))!;

    public static UTrack NewTrack(UProject project, int trackNo) =>
        new UTrack(project) { TrackNo = trackNo, Phonemizer = AutoFactory.Create() };

    /// <summary>
    /// Every track edits through one voice part. A track without one has nothing to
    /// draw into, so make it on demand. Clamps trackIndex into range.
    /// </summary>
    public static UVoicePart EnsurePart(UProject project, ref int trackIndex) {
        if (project.tracks.Count == 0) {
            DocManager.Inst.StartUndoGroup();
            DocManager.Inst.ExecuteCmd(new AddTrackCommand(project, NewTrack(project, 0)));
            DocManager.Inst.EndUndoGroup();
            trackIndex = 0;
        }
        trackIndex = Math.Clamp(trackIndex, 0, project.tracks.Count - 1);
        int index = trackIndex;

        var part = project.parts.OfType<UVoicePart>().FirstOrDefault(p => p.trackNo == index);
        if (part != null) return part;

        part = new UVoicePart {
            trackNo = index,
            position = 0,
            Duration = project.resolution * 4 * 8,
            name = project.tracks[index].TrackName,
        };
        DocManager.Inst.StartUndoGroup();
        DocManager.Inst.ExecuteCmd(new AddPartCommand(project, part));
        DocManager.Inst.EndUndoGroup();
        return part;
    }

    /// <returns>The new track's index.</returns>
    public static int AddTrack(UProject project) {
        DocManager.Inst.StartUndoGroup();
        DocManager.Inst.ExecuteCmd(new AddTrackCommand(project, NewTrack(project, project.tracks.Count)));
        DocManager.Inst.EndUndoGroup();
        return project.tracks.Count - 1;
    }

    /// <summary>Removes a track and its parts. False when it is the last track.</summary>
    public static bool RemoveTrack(UProject project, UTrack track) {
        if (project.tracks.Count <= 1) return false;
        DocManager.Inst.StartUndoGroup();
        // Parts hold a track index, so drop this track's parts before the track goes.
        foreach (var part in project.parts.Where(p => p.trackNo == track.TrackNo).ToList()) {
            DocManager.Inst.ExecuteCmd(new RemovePartCommand(project, part));
        }
        DocManager.Inst.ExecuteCmd(new RemoveTrackCommand(project, track));
        DocManager.Inst.EndUndoGroup();
        return true;
    }

    public static void SetSinger(UProject project, UTrack track, USinger? singer) {
        // The default WORLDLINE-R renderer drives worldline's PhraseSynth API, which
        // passes a managed LogCallback into native code. The wasm interpreter cannot
        // marshal that reverse p/invoke and aborts the runtime. CLASSIC reaches the
        // same resampler through plain one-way calls. The headless host does the
        // same so a project renders alike in both.
        track.RendererSettings.renderer = OpenUtau.Core.Render.Renderers.CLASSIC;
        DocManager.Inst.StartUndoGroup();
        DocManager.Inst.ExecuteCmd(new TrackChangeSingerCommand(project, track, singer!));
        // Auto picks the phonemizer per lyric (Hangul, kana, Latin) and routes the bank's own
        // language to the phonemizer its character.yaml names, so no manual choice is needed.
        if (singer != null && track.Phonemizer is not AutoPhonemizer) {
            DocManager.Inst.ExecuteCmd(new TrackChangePhonemizerCommand(project, track, AutoFactory.Create()));
        }
        DocManager.Inst.EndUndoGroup();
    }

    public static void SetPhonemizer(UProject project, UTrack track, PhonemizerFactory factory) {
        DocManager.Inst.StartUndoGroup();
        DocManager.Inst.ExecuteCmd(new TrackChangePhonemizerCommand(project, track, factory.Create()));
        DocManager.Inst.EndUndoGroup();
    }

    /// <summary>Must run inside an undo group; a part shorter than its notes renders truncated.</summary>
    public static void GrowPartToFit(UProject project, UVoicePart part, int endTick) {
        if (endTick <= part.Duration) return;
        int bar = project.resolution * 4 / project.timeSignatures[0].beatUnit * project.timeSignatures[0].beatPerBar;
        int target = (endTick / bar + 1) * bar;
        DocManager.Inst.ExecuteCmd(new ResizeVoicePartCommand(project, part, target - part.Duration, false));
    }
}
