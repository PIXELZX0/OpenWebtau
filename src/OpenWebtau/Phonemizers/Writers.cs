using OpenUtau.Core.Ustx;

namespace OpenWebtau.Phonemizers;

/// <summary>Syllables to Hangul, one syllable block per syllable.</summary>
public static class HangulWriter {
    // Unicode order: ㄱㄲㄴㄷㄸㄹㅁㅂㅃㅅㅆㅇㅈㅉㅊㅋㅌㅍㅎ
    static int Initial(string onset) => onset switch {
        "k" => 15, "g" => 0, "s" or "sh" => 9, "z" or "j" => 12, "t" => 16, "d" => 3,
        "ch" or "ts" => 14, "n" => 2, "h" => 18, "f" or "p" => 17, "b" or "v" => 7,
        "m" => 6, "r" or "l" => 5, _ => 11,
    };

    // ㅏ0 ㅐ1 ㅑ2 ㅒ3 ㅓ4 ㅔ5 ㅕ6 ㅖ7 ㅗ8 ㅘ9 ㅙ10 ㅚ11 ㅛ12 ㅜ13 ㅝ14 ㅞ15 ㅟ16 ㅠ17 ㅡ18 ㅢ19 ㅣ20
    static int Medial(string glide, string vowel) => (glide, vowel) switch {
        ("y", "a") => 2, ("y", "u") => 17, ("y", "o") => 12, ("y", "e") => 7,
        ("y", "eo") => 6, ("y", "ae") => 3, ("y", _) => 20,
        ("w", "a") => 9, ("w", "i") => 16, ("w", "e") => 15, ("w", "eo") => 14,
        ("w", "o") => 14, ("w", "ae") => 10, ("w", _) => 13,
        (_, "a") => 0, (_, "ae") => 1, (_, "i") => 20, (_, "u") => 13, (_, "e") => 5,
        (_, "o") => 8, (_, "eo") => 4, _ => 18,
    };

    // ㄴ=4 ㄹ=8 ㅁ=16 ㅂ=17 ㅅ=19 ㅇ=21 ㄱ=1. A stop batchim is what Korean writes for an unreleased
    // final in loanwords: ㅅ for t (캣), ㅂ for p, ㄱ for k.
    static int Final(string coda) => coda switch {
        "n" => 4, "m" => 16, "ng" => 21, "l" => 8, "k" => 1, "t" or "q" => 19, "p" => 17, _ => 0,
    };

    public static char Write(Syllable s) {
        string on = s.Onset, gl = s.Glide, v = s.Vowel.Length == 0 ? "eu" : s.Vowel;
        if (on == "sh" && gl == "" && v is "a" or "u" or "o" or "e" or "ae") gl = "y";  // しゃ → 샤
        if (on is "sh" or "ch" or "j" && v == "eu" && gl == "") v = "i";                // dish → 디시
        if (on is "ch" or "j" or "ts") gl = gl == "y" ? "" : gl;                        // ㅊ ㅈ are already palatal
        if (on == "ts" && v == "u") v = "eu";                                            // つ → 츠
        if (on == "f" && v == "u" && gl == "") on = "h";                                 // ふ → 후
        return (char)(0xAC00 + (Initial(on) * 21 + Medial(gl, v)) * 28 + Final(s.Coda));
    }

    public static List<Piece> Render(IEnumerable<Syllable> syllables) =>
        syllables.Where(s => !s.Long).Select(s => new Piece(Write(s).ToString())).ToList();
}

/// <summary>Syllables to romaji aliases (ka, shi, kya, n) checked against what the bank has.</summary>
public static class RomajiWriter {
    static string EpenthicVowel(string onset) => onset switch {
        "t" or "d" => "o",
        "ch" or "j" => "i",
        _ => "u",
    };

    /// <summary>
    /// Spellings for a syllable, best first. With <paramref name="korean"/> set, Korean
    /// romanisation of the vowels Japanese lacks (geo, neu, hae) goes first, for banks
    /// that spell Korean that way.
    /// </summary>
    public static List<string> Candidates(Syllable s, bool korean = false, bool hepburn = false) {
        string on = s.Onset;
        string v = s.Vowel switch {
            "ae" => "e", "eo" => "o", "eu" => EpenthicVowel(on), var x => x,
        };
        var list = new List<string>();
        void Add(string a) { if (a.Length > 0 && !list.Contains(a)) list.Add(a); }
        if (v.Length == 0) return list;
        if (korean && s.Vowel is "eo" or "eu" or "ae") Add(on + s.Glide + s.Vowel);
        if (on == "l") on = "r";
        if (on == "v") on = "b";

        if (on == "" && s.Glide == "y") {
            Add(v switch { "i" => "i", "e" => "ye", _ => "y" + v });
        } else if (on == "" && s.Glide == "w") {
            Add(v switch { "u" => "u", "a" => "wa", _ => "w" + v });
            Add(v);
        } else if (s.Glide == "y" && on is not ("sh" or "ch" or "j")) {
            Add(v == "i" ? on + "i" : on + "y" + v);
            Add(on + v);
        } else if (s.Glide == "w") {
            Add(on + "w" + v);
            Add(on + "u" + v);
            Add(on + v);
        } else {
            // Kunrei banks spell it si/ti/tu, Hepburn ones shi/chi/tsu: the bank decides, so
            // both are candidates; Hepburn is what is used when the bank has neither.
            if (hepburn) Add(Normalise(on, v));
            Add(on + v);
            Add(Normalise(on, v));
            if (on == "f" && v != "u") Add("h" + v);
            if (on == "ts" && v != "u") Add("t" + v);
        }
        return list;
    }

    static string Normalise(string on, string v) => (on + v) switch {
        "si" => "shi", "ti" => "chi", "tu" => "tsu", "hu" => "fu", "zi" => "ji", "di" => "ji", "du" => "zu",
        "ji" or "shi" or "chi" or "tsu" or "fu" => on + v,
        var x => x,
    };

    /// <param name="has">Whether the bank has an alias; the first plain candidate is used when it has none.</param>
    public static List<Piece> Render(IEnumerable<Syllable> syllables, Func<string, bool> has) {
        var pieces = new List<Piece>();
        foreach (var s in syllables) {
            if (s.Long) continue;
            var pick = Candidates(s, korean: true).FirstOrDefault(has) ?? Candidates(s, hepburn: true).FirstOrDefault();
            if (pick != null) pieces.Add(new Piece(pick, null, 3));
            foreach (var coda in CodaSyllables(s)) {
                var c = coda.HasVowel ? Candidates(coda) : new List<string> { "n", "N", "ん" };
                var alias = c.FirstOrDefault(has) ?? (coda.HasVowel ? Candidates(coda, hepburn: true)[0] : "n");
                pieces.Add(new Piece(alias, null, s.HasVowel ? 1 : 3));
            }
        }
        return pieces;
    }

    /// <summary>
    /// A CV voice cannot close a syllable, so a final consonant becomes a syllable of its own:
    /// n/m/ng the moraic ん, the stops and l the way a katakana loanword does (cat → キャト).
    /// </summary>
    public static IEnumerable<Syllable> CodaSyllables(Syllable s) {
        switch (s.Coda) {
            case "n" or "m" or "ng": yield return new Syllable("n", "", ""); break;
            case "k": yield return new Syllable("k", "", "eu"); break;
            case "t": yield return new Syllable("t", "", "eu"); break;
            case "p": yield return new Syllable("p", "", "eu"); break;
            case "l": yield return new Syllable("r", "", "eu"); break;
        }
    }
}

/// <summary>Syllables to hiragana or katakana, checked against what the bank has.</summary>
public static class KanaWriter {
    static readonly Dictionary<string, string> Rows = new() {
        [""] = "あいうえお", ["k"] = "かきくけこ", ["g"] = "がぎぐげご", ["s"] = "さしすせそ", ["z"] = "ざじずぜぞ",
        ["t"] = "たちつてと", ["d"] = "だぢづでど", ["n"] = "なにぬねの", ["h"] = "はひふへほ", ["b"] = "ばびぶべぼ",
        ["p"] = "ぱぴぷぺぽ", ["m"] = "まみむめも", ["r"] = "らりるれろ",
    };
    const string SmallVowel = "ぁぃぅぇぉ";

    static string EpenthicVowel(string onset) => onset switch {
        "t" or "d" => "o",
        "ch" or "j" => "i",
        _ => "u",
    };

    /// <summary>Hiragana spellings for a syllable, best first.</summary>
    public static List<string> Candidates(Syllable s) {
        var list = new List<string>();
        void Add(string a) { if (a.Length > 0 && !list.Contains(a)) list.Add(a); }
        string on = s.Onset;
        string v = s.Vowel switch { "ae" => "e", "eo" => "o", "eu" => EpenthicVowel(on), var x => x };
        int vi = "aiueo".IndexOf(v);
        if (vi < 0) return list;
        string small = SmallVowel[vi].ToString();
        string y = v switch { "a" => "ゃ", "u" => "ゅ", "o" => "ょ", _ => "" };
        if (on == "l") on = "r";

        if (on == "") {
            switch (s.Glide) {
                case "y":
                    Add(v switch { "a" => "や", "u" => "ゆ", "o" => "よ", "i" => "い", _ => "いぇ" });
                    if (v == "e") Add("え");
                    break;
                case "w":
                    Add(v switch { "a" => "わ", "u" => "う", "i" => "うぃ", "e" => "うぇ", _ => "うぉ" });
                    Add(Rows[""][vi].ToString());
                    break;
                default:
                    Add(Rows[""][vi].ToString());
                    break;
            }
        } else if (on is "sh" or "ch" or "j") {
            string b = on switch { "sh" => "し", "ch" => "ち", _ => "じ" };
            Add(v switch { "i" => b, "e" => b + "ぇ", _ => b + y });
            Add(b);
        } else if (on == "ts") {
            Add(v == "u" ? "つ" : "つ" + small);
            Add(Rows["t"][vi].ToString());
        } else if (on == "f") {
            Add(v == "u" ? "ふ" : "ふ" + small);
            Add(Rows["h"][vi].ToString());
        } else if (on == "v") {
            Add(v == "u" ? "ゔ" : "ゔ" + small);
            Add(Rows["b"][vi].ToString());
        } else if (Rows.TryGetValue(on, out var row)) {
            if (s.Glide == "y" && v != "i") {
                Add(v == "e" ? row[1] + "ぇ" : row[1] + y);
                Add(row[vi].ToString());
            } else if (s.Glide == "w" && v != "u") {
                Add(row[2] + small);
                Add(row[vi].ToString());
            } else {
                // Sounds Japanese has no kana for (ti, tu, di, du) come as small-kana combinations.
                if (on == "t" && v == "i") { Add("てぃ"); Add("ち"); }
                else if (on == "t" && v == "u") { Add("とぅ"); Add("つ"); }
                else if (on == "d" && v == "i") { Add("でぃ"); Add("じ"); }
                else if (on == "d" && v == "u") { Add("どぅ"); Add("ず"); }
                Add(row[vi].ToString());
            }
        }
        return list;
    }

    public static string ToKatakana(string hiragana) =>
        new string(hiragana.Select(c => c >= 0x3041 && c <= 0x3096 ? (char)(c + 0x60) : c).ToArray());

    public static List<Piece> Render(IEnumerable<Syllable> syllables, bool katakana, Func<string, bool> has) {
        string Script(string k) => katakana ? ToKatakana(k) : k;
        var pieces = new List<Piece>();
        foreach (var s in syllables) {
            if (s.Long) continue;
            var c = Candidates(s).Select(Script).ToList();
            if (c.Count > 0) pieces.Add(new Piece(c.FirstOrDefault(has) ?? c[0], null, 3));
            foreach (var coda in RomajiWriter.CodaSyllables(s)) {
                var cc = coda.HasVowel ? Candidates(coda).Select(Script).ToList() : new List<string> { Script("ん") };
                var alias = cc.FirstOrDefault(has) ?? cc[0];
                pieces.Add(new Piece(alias, null, s.HasVowel ? 1 : 3));
            }
        }
        return pieces;
    }
}

/// <summary>Syllables to ARPAbet, as a phonetic hint for the English (Arpasing) phonemizers.</summary>
public static class ArpaWriter {
    static string Onset(string o) => o switch {
        "j" => "jh", "h" => "hh", "ts" => "t s", "" => "", var x => x,
    };

    static string Vowel(string v) => v switch {
        "a" => "aa", "i" => "iy", "u" => "uw", "e" => "eh", "o" => "ao",
        "ae" => "ae", "eo" => "ah", "eu" => "uh", _ => "",
    };

    public static string Hint(IEnumerable<Syllable> syllables) {
        var phones = new List<string>();
        foreach (var s in syllables) {
            if (s.Long) continue;
            if (s.Onset.Length > 0) phones.Add(s.Onset == "s" && (s.Vowel == "i" || s.Glide == "y") ? "sh" : Onset(s.Onset));
            if (s.Glide == "y" && s.Vowel != "i") phones.Add("y");
            else if (s.Glide == "w" && s.Vowel != "u") phones.Add("w");
            if (s.HasVowel) phones.Add(Vowel(s.Vowel));
            if (s.Coda is "n" or "m" or "ng" or "l" or "k" or "t" or "p") phones.Add(s.Coda);
        }
        return string.Join(' ', phones.Where(p => p.Length > 0));
    }
}
