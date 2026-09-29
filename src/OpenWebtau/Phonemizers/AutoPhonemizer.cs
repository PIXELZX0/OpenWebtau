using System.Text.RegularExpressions;
using OpenUtau.Api;
using OpenUtau.Core;
using OpenUtau.Core.Ustx;
using OpenUtau.Plugin.Builtin;

namespace OpenWebtau.Phonemizers;

/// <summary>
/// Picks a phonemizer per note from the script of its lyric and the language of the
/// track's voicebank, so "안녕" or "ありがとう" or "hello" can be typed straight onto a
/// note without choosing a phonemizer first (the way Synthesizer V picks phonemes from
/// the lyric's language).
///
/// A lyric in the bank's own language goes to the phonemizer the bank names in
/// character.yaml (or a sensible one for its alias style). A lyric in another language
/// goes to a cross-lingual phonemizer when OpenUtau ships one (Korean/English on a
/// Japanese bank), and otherwise passes through as the alias.
/// </summary>
[Phonemizer("Auto (Detect Language)", "AUTO", "OpenWebtau")]
public class AutoPhonemizer : Phonemizer {
    public enum Script { Unknown, Hangul, Kana, Han, Latin }

    USinger? singer;
    Script bankScript;
    string? bankLanguage;
    bool bankIsVcv;

    readonly Dictionary<Type, Phonemizer> delegates = new();
    readonly Dictionary<Phonemizer, Exception> broken = new();
    readonly Dictionary<Note[], Phonemizer> byGroup = new(ReferenceEqualityComparer.Instance);

    public override void SetSinger(USinger singer) {
        if (this.singer == singer && singer != null && singer.Loaded) return;
        this.singer = singer;
        InspectBank();
    }

    public override void SetUp(Note[][] notes, UProject project, UTrack track) {
        base.SetUp(notes, project, track);
        InspectBank();
        byGroup.Clear();
        broken.Clear();

        // Neutral lyrics ("-", "R", punctuation) follow the note before them so a rest
        // or a breath does not flip the phonemizer and cut the phrase in two.
        var scripts = new Script[notes.Length];
        var last = Script.Unknown;
        for (int i = 0; i < notes.Length; i++) {
            var s = Detect(notes[i][0].lyric);
            if (s == Script.Unknown) s = last;
            scripts[i] = s;
            if (s != Script.Unknown) last = s;
        }
        // Leading neutral notes take the first script that shows up.
        var next = Script.Unknown;
        for (int i = notes.Length - 1; i >= 0; i--) {
            if (scripts[i] == Script.Unknown) scripts[i] = next;
            else next = scripts[i];
        }

        var groupsByDelegate = new Dictionary<Phonemizer, List<Note[]>>();
        for (int i = 0; i < notes.Length; i++) {
            var d = GetDelegate(Choose(scripts[i]));
            byGroup[notes[i]] = d;
            if (!groupsByDelegate.TryGetValue(d, out var list)) {
                groupsByDelegate[d] = list = new List<Note[]>();
            }
            list.Add(notes[i]);
        }
        // Each delegate only sees its own groups, so a syllable-based phonemizer never
        // reads a Japanese lyric while it is building Korean syllables.
        foreach (var (d, groups) in groupsByDelegate) {
            try {
                d.SetSinger(singer!);
                d.SetTiming(timeAxis);
                d.SetUp(groups.ToArray(), project, track);
            } catch (Exception e) {
                Serilog.Log.Error(e, "auto phonemizer: {Delegate} failed to set up", d.GetType().Name);
                broken[d] = e;
            }
        }
    }

    public override Result Process(Note[] notes, Note? prev, Note? next, Note? prevNeighbour, Note? nextNeighbour, Note[] prevs) {
        if (!byGroup.TryGetValue(notes, out var d)) {
            d = GetDelegate(Choose(Detect(notes[0].lyric)));
        }
        if (broken.TryGetValue(d, out var e)) throw e;

        // A neighbour in another language means nothing to this phonemizer; handing it
        // over would make a VCV/CVVC one look for a connection that cannot exist.
        bool Same(Note? n) => n != null && ReferenceEquals(ResolveFor(n.Value), d);
        var result = d.Process(
            notes,
            Same(prev) ? prev : null,
            Same(next) ? next : null,
            Same(prevNeighbour) ? prevNeighbour : null,
            Same(nextNeighbour) ? nextNeighbour : null,
            prevs.Length > 0 && Same(prevs[0]) ? prevs : Array.Empty<Note>());

        if (d.LegacyMapping && singer != null) {
            for (int k = 0; k < result.phonemes.Length; k++) {
                if (singer.TryGetMappedOto(result.phonemes[k].phoneme, notes[0].tone, out var oto)) {
                    result.phonemes[k].phoneme = oto.Alias;
                }
            }
        }
        return result;
    }

    public override void CleanUp() {
        foreach (var d in delegates.Values) {
            try { d.CleanUp(); } catch (Exception e) { Serilog.Log.Error(e, "auto phonemizer: cleanup failed"); }
        }
        byGroup.Clear();
    }

    Phonemizer ResolveFor(Note n) {
        // prev/next notes come from the runner as bare Notes; find the delegate by lyric.
        foreach (var (group, d) in byGroup) {
            if (group.Length > 0 && group[0].position == n.position && group[0].lyric == n.lyric) return d;
        }
        return GetDelegate(Choose(Detect(n.lyric)));
    }

    Phonemizer GetDelegate(Type type) {
        if (!delegates.TryGetValue(type, out var d)) {
            var factory = PhonemizerFactory.Get(type);
            d = factory?.Create() ?? (Phonemizer)Activator.CreateInstance(type)!;
            delegates[type] = d;
        }
        return d;
    }

    // ---- bank language ----

    void InspectBank() {
        bankScript = Script.Unknown;
        bankLanguage = null;
        bankIsVcv = false;
        if (singer == null) return;

        var declared = string.IsNullOrEmpty(singer.DefaultPhonemizer)
            ? null : PhonemizerFactory.Get(singer.DefaultPhonemizer);
        bankLanguage = declared?.language?.ToUpperInvariant();

        // Count what the aliases are written in; a bank that names no phonemizer (or a
        // language we have no script for) is judged by its voice files.
        var counts = new int[5];
        int vcv = 0, total = 0;
        foreach (var oto in singer.Otos) {
            var alias = oto.Alias;
            if (string.IsNullOrEmpty(alias)) continue;
            if (++total > 4000) break;
            counts[(int)Detect(alias)]++;
            if (VcvAlias.IsMatch(alias)) vcv++;
        }
        var best = Script.Unknown;
        int bestCount = 0;
        for (int s = 1; s < counts.Length; s++) {
            if (counts[s] > bestCount) { bestCount = counts[s]; best = (Script)s; }
        }
        bankScript = bankLanguage switch {
            "KO" => Script.Hangul,
            "JA" => Script.Kana,
            "ZH" or "YUE" => Script.Han,
            _ => best,
        };
        bankIsVcv = total > 0 && vcv * 4 > total;
    }

    // "a あ", "n ka" style aliases: a phoneme, a space, then a syllable.
    static readonly Regex VcvAlias = new(@"^[\p{L}\-]+ [\p{L}]+$", RegexOptions.Compiled);

    // ---- routing ----

    Type Choose(Script lyric) {
        // Same language as the bank: use what the bank asks for, else the usual one.
        if (lyric == bankScript || lyric == Script.Unknown) {
            var declared = singer == null || string.IsNullOrEmpty(singer.DefaultPhonemizer)
                ? null : PhonemizerFactory.Get(singer.DefaultPhonemizer);
            if (declared != null && declared.type != typeof(AutoPhonemizer)) return declared.type;
            return lyric switch {
                Script.Hangul => typeof(KoreanCVPhonemizer),
                Script.Kana when bankIsVcv => typeof(JapaneseVCVPhonemizer),
                _ => typeof(DefaultPhonemizer),
            };
        }
        // Another language on this bank: only the cross-lingual ones OpenUtau has.
        return (lyric, bankScript) switch {
            (Script.Hangul, Script.Kana) => typeof(KOtoJAPhonemizer),
            (Script.Latin, Script.Kana) => typeof(ENtoJAPhonemizer),
            _ => typeof(DefaultPhonemizer),
        };
    }

    // ---- script detection ----

    /// <summary>Script of a lyric; punctuation, digits and "-"/"+" stay Unknown.</summary>
    public static Script Detect(string? lyric) {
        if (string.IsNullOrEmpty(lyric)) return Script.Unknown;
        bool kana = false, han = false, latin = false;
        foreach (var ch in lyric) {
            if ((ch >= 0xAC00 && ch <= 0xD7A3) || (ch >= 0x1100 && ch <= 0x11FF) || (ch >= 0x3130 && ch <= 0x318F)) {
                return Script.Hangul;
            }
            if ((ch >= 0x3041 && ch <= 0x30FF) || (ch >= 0x31F0 && ch <= 0x31FF) || (ch >= 0xFF66 && ch <= 0xFF9F)) kana = true;
            else if ((ch >= 0x4E00 && ch <= 0x9FFF) || (ch >= 0x3400 && ch <= 0x4DBF)) han = true;
            else if ((ch >= 'a' && ch <= 'z') || (ch >= 'A' && ch <= 'Z') || (ch >= 0xC0 && ch <= 0x24F)) latin = true;
        }
        // Kanji next to kana is Japanese; kana wins.
        if (kana) return Script.Kana;
        if (han) return Script.Han;
        return latin ? Script.Latin : Script.Unknown;
    }
}
