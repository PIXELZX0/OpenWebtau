using OpenUtau.Api;
using OpenUtau.Core;
using OpenUtau.Core.Ustx;
using OpenUtau.Plugin.Builtin;
using WanaKanaNet;

namespace OpenWebtau.Phonemizers;

/// <summary>
/// Turns lyrics in any supported language into what the track's voicebank can sing, the way
/// Synthesizer V picks phonemes from the language of the lyric.
///
/// Every note's lyric is read into language-neutral syllables (Hangul with Korean
/// pronunciation rules, kana, English through CMUdict) and written out again in the
/// bank's own alias system, then handed to the phonemizer that already knows that bank
/// (Korean CV, Japanese VCV, Arpasing, ...). Lyrics already in the bank's language skip
/// the conversion and go straight to that phonemizer. A lyric of several syllables on one
/// note ("안녕", "hello") is split and shares the note's length.
///
/// Kanji cannot be read without a dictionary, so Han lyrics only work on Chinese banks.
/// </summary>
[Phonemizer("Auto (Detect Language)", "AUTO", "OpenWebtau")]
public class AutoPhonemizer : Phonemizer {
    public enum Script { Unknown, Hangul, Kana, Han, Latin }

    /// <summary>A note group handed to one delegate phonemizer.</summary>
    sealed class Step {
        public Phonemizer Delegate = null!;
        public Note[] Notes = null!;
        public int Index;
        /// <summary>Script of the lyric this step came from, before any conversion.</summary>
        public Script Origin;
    }

    USinger? singer;
    BankProfile bank = BankProfile.Empty;

    readonly Dictionary<Type, Phonemizer> delegates = new();
    readonly Dictionary<Phonemizer, Exception> broken = new();
    readonly Dictionary<Note[], List<Step>> plans = new(ReferenceEqualityComparer.Instance);
    readonly List<Step> steps = new();

    int inspectedOtos = -1;

    public override void SetSinger(USinger singer) {
        // The runner calls this before every phrase; scanning the oto list is only worth
        // repeating for a different singer or after the bank was edited.
        int count = singer?.Otos.Count ?? 0;
        if (ReferenceEquals(singer, this.singer) && count == inspectedOtos) return;
        this.singer = singer;
        inspectedOtos = count;
        bank = BankProfile.Inspect(singer);
    }

    public override void SetUp(Note[][] notes, UProject project, UTrack track) {
        base.SetUp(notes, project, track);
        plans.Clear();
        steps.Clear();
        broken.Clear();

        var scripts = ResolveScripts(notes);
        for (int i = 0; i < notes.Length; i++) {
            var prev = i > 0 ? notes[i - 1][0].lyric : null;
            var next = i < notes.Length - 1 ? notes[i + 1][0].lyric : null;
            var list = Plan(notes[i], scripts[i], prev, next);
            foreach (var s in list) { s.Index = steps.Count; s.Origin = scripts[i]; steps.Add(s); }
            plans[notes[i]] = list;
        }

        // Each delegate only sees its own steps, so a syllable-based phonemizer never reads a
        // lyric in a language it was not built for.
        foreach (var group in steps.GroupBy(s => s.Delegate)) {
            var d = group.Key;
            try {
                d.SetSinger(singer!);
                d.SetTiming(timeAxis);
                d.SetUp(group.Select(s => s.Notes).ToArray(), project, track);
            } catch (Exception e) {
                Serilog.Log.Error(e, "auto phonemizer: {Delegate} failed to set up", d.GetType().Name);
                broken[d] = e;
            }
        }
    }

    public override Result Process(Note[] notes, Note? prev, Note? next, Note? prevNeighbour, Note? nextNeighbour, Note[] prevs) {
        if (!plans.TryGetValue(notes, out var list)) {
            // Not a group SetUp saw; treat it like a plain lyric.
            var d = GetDelegate(bank.Native(Detect(notes[0].lyric)));
            if (broken.TryGetValue(d, out var e0)) throw e0;
            return d.Process(notes, prev, next, prevNeighbour, nextNeighbour, prevs);
        }

        // The runner shortens a group's last note when the next phoneme comes early; converted
        // groups are copies, so carry that over to the last piece.
        var tail = list[^1];
        if (!ReferenceEquals(tail.Notes, notes)) {
            var end = notes[^1].position + notes[^1].duration;
            tail.Notes[^1].duration = Math.Max(1, end - tail.Notes[^1].position);
        }

        var phonemes = new List<Phoneme>();
        foreach (var step in list) {
            if (broken.TryGetValue(step.Delegate, out var e)) throw e;
            var before = Neighbour(step, -1);
            var after = Neighbour(step, +1);
            var r = step.Delegate.Process(
                step.Notes,
                before?.Notes[0],
                after?.Notes[0],
                Touching(before, step) ? before?.Notes[0] : null,
                Touching(step, after) ? after?.Notes[0] : null,
                Touching(before, step) ? before!.Notes : Array.Empty<Note>());
            int shift = step.Notes[0].position - notes[0].position;
            foreach (var p0 in r.phonemes) {
                var p = p0;
                p.position += shift;
                if (step.Delegate.LegacyMapping && singer != null
                    && singer.TryGetMappedOto(p.phoneme, step.Notes[0].tone, out var oto)) {
                    p.phoneme = oto.Alias;
                }
                phonemes.Add(p);
            }
        }
        return new Result { phonemes = phonemes.ToArray() };
    }

    public override void CleanUp() {
        foreach (var d in delegates.Values) {
            try { d.CleanUp(); } catch (Exception e) { Serilog.Log.Error(e, "auto phonemizer: cleanup failed"); }
        }
        plans.Clear();
        steps.Clear();
    }

    // A neighbour in another language means nothing to this phonemizer: a VCV or CVVC one would
    // look for a connection that cannot exist, and the Korean one would apply liaison between
    // words that are not Korean to each other. Only the pieces of one lyric, or neighbours
    // written in the same script, are connected.
    Step? Neighbour(Step s, int dir) {
        int i = s.Index + dir;
        return i >= 0 && i < steps.Count && ReferenceEquals(steps[i].Delegate, s.Delegate) && steps[i].Origin == s.Origin
            ? steps[i] : null;
    }

    static bool Touching(Step? a, Step? b) {
        if (a == null || b == null) return false;
        var last = a.Notes[^1];
        return last.position + last.duration >= b.Notes[0].position;
    }

    Phonemizer GetDelegate(Type type) {
        if (!delegates.TryGetValue(type, out var d)) {
            var factory = PhonemizerFactory.Get(type);
            d = factory?.Create() ?? (Phonemizer)Activator.CreateInstance(type)!;
            delegates[type] = d;
        }
        return d;
    }

    // ---- planning ----

    // Neutral lyrics ("-", "R", punctuation) follow the note before them so a rest or a
    // breath does not switch phonemizer and cut a phrase in two.
    static Script[] ResolveScripts(Note[][] notes) {
        var scripts = new Script[notes.Length];
        var last = Script.Unknown;
        for (int i = 0; i < notes.Length; i++) {
            var s = Detect(notes[i][0].lyric);
            scripts[i] = s == Script.Unknown ? last : s;
            if (s != Script.Unknown) last = s;
        }
        var next = Script.Unknown;
        for (int i = notes.Length - 1; i >= 0; i--) {
            if (scripts[i] == Script.Unknown) scripts[i] = next; else next = scripts[i];
        }
        return scripts;
    }

    List<Step> Plan(Note[] group, Script script, string? prevLyric, string? nextLyric) {
        var lead = group[0];
        var native = bank.Native(script);
        bool typed = Detect(lead.lyric) != Script.Unknown;

        // A phonetic hint means the user already said how it is pronounced.
        if (!typed || !string.IsNullOrEmpty(lead.phoneticHint) || bank.Kind == BankKind.Unknown) {
            return Whole(group, native);
        }

        switch (script) {
            case Script.Hangul: {
                if (bank.Kind == BankKind.Hangul) {
                    var chars = HangulReader.Chars(lead.lyric);
                    return chars.Count > 1 ? Split(group, chars.Select(c => new Piece(c.ToString())).ToList(), native) : Whole(group, native);
                }
                if (bank.Kind == BankKind.Kana) return Whole(group, typeof(KOtoJAPhonemizer));
                var syl = HangulReader.Read(lead.lyric, prevLyric, nextLyric);
                return Convert(group, syl, native);
            }
            case Script.Kana: {
                if (bank.Kind == BankKind.Kana) {
                    var units = KanaReader.Units(lead.lyric);
                    return units.Count > 1 ? Split(group, units.Select(u => new Piece(u)).ToList(), native) : Whole(group, native);
                }
                return Convert(group, KanaReader.Read(lead.lyric), native);
            }
            case Script.Latin: {
                var word = new string(lead.lyric.Where(c => char.IsLetter(c) || c == '\'').ToArray());
                switch (bank.Kind) {
                    case BankKind.Hangul:
                        if (KoreanPhonemizerUtil.IsKoreanRomaji(lead.lyric)) return Whole(group, native);
                        return Convert(group, EnglishReader.Read(word), native);
                    case BankKind.Kana:
                        if (RomajiReader.Parse(word, strict: true) != null) {
                            var kana = bank.Katakana ? WanaKana.ToKatakana(word) : WanaKana.ToHiragana(word);
                            var units = KanaReader.Units(kana);
                            return units.Count > 1 ? Split(group, units.Select(u => new Piece(u)).ToList(), native) : Whole(group, native, kana);
                        }
                        return Convert(group, EnglishReader.Read(word), native);
                    default:
                        return Whole(group, native);
                }
            }
            default:
                return Whole(group, native);
        }
    }

    /// <summary>Writes syllables in the bank's alias system and hands them to its phonemizer.</summary>
    List<Step> Convert(Note[] group, List<Syllable>? syllables, Type native) {
        if (syllables == null || syllables.Count == 0) return Whole(group, native);
        List<Piece> pieces;
        switch (bank.Kind) {
            case BankKind.Hangul:
                pieces = HangulWriter.Render(syllables);
                break;
            case BankKind.Romaji:
                pieces = RomajiWriter.Render(syllables, Has);
                break;
            case BankKind.Kana:
                pieces = KanaWriter.Render(syllables, bank.Katakana, Has);
                break;
            case BankKind.Arpa:
                // English phonemizers take a whole word plus its phonemes on one note.
                var hint = ArpaWriter.Hint(syllables);
                return hint.Length == 0 ? Whole(group, native)
                    : Split(group, new List<Piece> { new Piece(group[0].lyric, hint) }, native);
            default:
                return Whole(group, native);
        }
        return pieces.Count == 0 ? Whole(group, native) : Split(group, pieces, native);

        bool Has(string alias) => singer != null
            && (singer.TryGetOto(alias, out _) || singer.TryGetMappedOto(alias, group[0].tone, out _));
    }

    List<Step> Whole(Note[] group, Type type, string? lyric = null) {
        var notes = group;
        if (lyric != null) notes = Reword(group, lyric, null, group[0].position, group[0].duration);
        return new List<Step> { new Step { Delegate = GetDelegate(type), Notes = notes } };
    }

    /// <summary>
    /// One step per piece. The lead note's length is shared out by weight; extender notes stay
    /// with the last piece, which is the one that gets held.
    /// </summary>
    List<Step> Split(Note[] group, List<Piece> pieces, Type type) {
        var d = GetDelegate(type);
        var lead = group[0];
        if (pieces.Count == 1) {
            var p = pieces[0];
            if (p.Lyric == lead.lyric && p.Hint == null) return Whole(group, type);
            return new List<Step> { new Step { Delegate = d, Notes = Reword(group, p.Lyric, p.Hint, lead.position, lead.duration) } };
        }

        int total = pieces.Sum(p => p.Weight);
        var result = new List<Step>();
        int cursor = lead.position;
        int used = 0;
        for (int i = 0; i < pieces.Count; i++) {
            used += pieces[i].Weight;
            bool last = i == pieces.Count - 1;
            int end = last ? lead.position + lead.duration : lead.position + (int)((long)lead.duration * used / total);
            int dur = Math.Max(1, end - cursor);
            Note[] notes;
            if (last) {
                notes = Reword(group, pieces[i].Lyric, pieces[i].Hint, cursor, dur);
            } else {
                notes = new[] { Reword(new[] { lead }, pieces[i].Lyric, pieces[i].Hint, cursor, dur)[0] };
            }
            result.Add(new Step { Delegate = d, Notes = notes });
            cursor = end;
        }
        return result;
    }

    /// <summary>Copy of a group with the lead note's text and timing replaced.</summary>
    static Note[] Reword(Note[] group, string lyric, string? hint, int position, int duration) {
        var copy = (Note[])group.Clone();
        var lead = group[0];
        copy[0] = new Note {
            lyric = lyric,
            phoneticHint = hint ?? lead.phoneticHint,
            tone = lead.tone,
            position = position,
            duration = duration,
            phonemeAttributes = lead.phonemeAttributes,
        };
        return copy;
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
