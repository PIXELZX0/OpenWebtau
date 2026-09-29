using System.Text;
using OpenUtau.Api;
using OpenUtau.Core;
using OpenUtau.Core.G2p;

namespace OpenWebtau.Phonemizers;

/// <summary>Hangul to syllables, after Korean pronunciation rules (연음, 비음화, 격음화 ...).</summary>
public static class HangulReader {
    const int Start = 0xAC00, End = 0xD7A3;

    public static bool IsSyllable(char c) => c >= Start && c <= End;

    /// <summary>The Hangul syllables of a lyric, with anything else (punctuation, spaces) dropped.</summary>
    public static List<char> Chars(string lyric) => lyric.Where(IsSyllable).ToList();

    static readonly string[] Onsets = { "g", "k", "n", "d", "t", "r", "m", "b", "p", "s", "s", "", "j", "ch", "ch", "k", "t", "p", "h" };

    // ㅏ ㅐ ㅑ ㅒ ㅓ ㅔ ㅕ ㅖ ㅗ ㅘ ㅙ ㅚ ㅛ ㅜ ㅝ ㅞ ㅟ ㅠ ㅡ ㅢ ㅣ
    static readonly (string glide, string vowel)[] Vowels = {
        ("", "a"), ("", "ae"), ("y", "a"), ("y", "ae"), ("", "eo"), ("", "e"), ("y", "eo"), ("y", "e"),
        ("", "o"), ("w", "a"), ("w", "ae"), ("w", "e"), ("y", "o"), ("", "u"), ("w", "eo"), ("w", "e"),
        ("w", "i"), ("y", "u"), ("", "eu"), ("", "i"), ("", "i"),
    };

    // - ㄱ ㄲ ㄳ ㄴ ㄵ ㄶ ㄷ ㄹ ㄺ ㄻ ㄼ ㄽ ㄾ ㄿ ㅀ ㅁ ㅂ ㅄ ㅅ ㅆ ㅇ ㅈ ㅊ ㅋ ㅌ ㅍ ㅎ
    static readonly string[] Codas = {
        "", "k", "k", "k", "n", "n", "n", "t", "l", "k", "m", "l", "l", "l", "p", "l",
        "m", "p", "p", "t", "t", "ng", "t", "t", "k", "t", "p", "t",
    };

    /// <param name="prevLyric">Lyric of the note before, if any: it decides liaison into this one.</param>
    /// <param name="nextLyric">Lyric of the note after.</param>
    public static List<Syllable> Read(string lyric, string? prevLyric = null, string? nextLyric = null) {
        var chars = Chars(lyric);
        var result = new List<Syllable>();
        for (int i = 0; i < chars.Count; i++) {
            string? prev = i > 0 ? chars[i - 1].ToString() : LastHangul(prevLyric);
            string? next = i < chars.Count - 1 ? chars[i + 1].ToString() : FirstHangul(nextLyric);
            result.Add(Decompose(Pronounce(prev, chars[i], next)));
        }
        return result;
    }

    static string? LastHangul(string? lyric) {
        var c = lyric == null ? new List<char>() : Chars(lyric);
        return c.Count > 0 ? c[^1].ToString() : null;
    }

    static string? FirstHangul(string? lyric) {
        var c = lyric == null ? new List<char>() : Chars(lyric);
        return c.Count > 0 ? c[0].ToString() : null;
    }

    static char Pronounce(string? prev, char c, string? next) {
        try {
            var spoken = KoreanPhonemizerUtil.Variate(prev, c.ToString(), next);
            if (spoken != null && spoken.Length == 1 && IsSyllable(spoken[0])) return spoken[0];
        } catch (Exception) {
            // Fall back to the written syllable; a wrong liaison beats a lost note.
        }
        return c;
    }

    public static Syllable Decompose(char c) {
        int n = c - Start;
        int t = n % 28;
        int v = n / 28 % 21;
        int l = n / 28 / 21;
        var (glide, vowel) = Vowels[v];
        return new Syllable(Onsets[l], glide, vowel, Codas[t]);
    }
}

/// <summary>Hiragana and katakana to syllables.</summary>
public static class KanaReader {
    public static bool IsKana(char c) =>
        (c >= 0x3041 && c <= 0x3096) || (c >= 0x30A1 && c <= 0x30F6) || c == 0x30FC;

    static char Hira(char c) => c >= 0x30A1 && c <= 0x30F6 ? (char)(c - 0x60) : c;

    const string Small = "ぁぃぅぇぉゃゅょゎ";

    /// <summary>
    /// The kana of a lyric grouped the way they are sung: a small ゃ/ぃ stays with the kana
    /// before it, and っ, ん and ー stand alone. The original characters are kept, so a
    /// katakana lyric stays katakana.
    /// </summary>
    public static List<string> Units(string lyric) {
        var units = new List<string>();
        foreach (var c in lyric) {
            if (!IsKana(c)) continue;
            if (units.Count > 0 && Small.Contains(Hira(c)) && "っんー".IndexOf(Hira(units[^1][^1])) < 0) {
                units[^1] += c;
            } else {
                units.Add(c.ToString());
            }
        }
        return units;
    }

    static readonly Dictionary<char, (string on, string gl, string v)> Table = BuildTable();

    static Dictionary<char, (string, string, string)> BuildTable() {
        var t = new Dictionary<char, (string, string, string)>();
        void Row(string kana, string onset) {
            const string vowels = "aiueo";
            for (int i = 0; i < kana.Length; i++) {
                if (kana[i] != ' ') t[kana[i]] = (onset, "", vowels[i].ToString());
            }
        }
        Row("あいうえお", "");
        Row("かきくけこ", "k"); Row("がぎぐげご", "g");
        Row("さ すせそ", "s"); Row("ざ ずぜぞ", "z");
        Row("た  てと", "t"); Row("だ  でど", "d");
        Row("なにぬねの", "n");
        Row("はひ へほ", "h"); Row("ばびぶべぼ", "b"); Row("ぱぴぷぺぽ", "p");
        Row("まみむめも", "m");
        Row("らりるれろ", "r");
        t['し'] = ("sh", "", "i"); t['じ'] = ("j", "", "i"); t['ぢ'] = ("j", "", "i");
        t['ち'] = ("ch", "", "i"); t['つ'] = ("ts", "", "u"); t['づ'] = ("z", "", "u");
        t['ふ'] = ("f", "", "u"); t['ゔ'] = ("v", "", "u");
        t['や'] = ("", "y", "a"); t['ゆ'] = ("", "y", "u"); t['よ'] = ("", "y", "o");
        t['わ'] = ("", "w", "a"); t['ゎ'] = ("", "w", "a"); t['ゐ'] = ("", "", "i");
        t['ゑ'] = ("", "", "e"); t['を'] = ("", "", "o");
        return t;
    }

    static readonly Dictionary<char, string> SmallVowel = new() {
        ['ぁ'] = "a", ['ぃ'] = "i", ['ぅ'] = "u", ['ぇ'] = "e", ['ぉ'] = "o",
    };
    static readonly Dictionary<char, string> SmallY = new() { ['ゃ'] = "a", ['ゅ'] = "u", ['ょ'] = "o" };

    public static List<Syllable> Read(string lyric) => Read(Units(lyric));

    public static List<Syllable> Read(IEnumerable<string> units) {
        var result = new List<Syllable>();
        foreach (var unit in units) {
            var head = Hira(unit[0]);
            switch (head) {
                case 'っ': Attach(result, "q"); continue;
                case 'ん': Attach(result, "n"); continue;
                case 'ー':
                    if (result.Count > 0 && result[^1].HasVowel) {
                        result.Add(new Syllable("", "", result[^1].Vowel, "", Long: true));
                    }
                    continue;
            }
            if (!Table.TryGetValue(head, out var b)) continue;
            var syl = new Syllable(b.on, b.gl, b.v);
            if (unit.Length > 1) {
                var small = Hira(unit[1]);
                if (SmallY.TryGetValue(small, out var yv)) {
                    // しゃ is sh+a, きゃ is k+y+a
                    syl = new Syllable(syl.Onset, syl.Onset is "sh" or "ch" or "j" ? "" : "y", yv);
                } else if (SmallVowel.TryGetValue(small, out var sv)) {
                    // うぃ = wi, いぇ = ye, ふぁ = fa, てぃ = ti
                    syl = syl.Onset.Length == 0 && syl.Vowel == "u" ? new Syllable("", "w", sv)
                        : syl.Onset.Length == 0 && syl.Vowel == "i" ? new Syllable("", "y", sv)
                        : new Syllable(syl.Onset, "", sv);
                }
            }
            result.Add(syl);
        }
        return result;
    }

    // ん and っ close the syllable before them when there is one to close.
    static void Attach(List<Syllable> list, string coda) {
        if (list.Count > 0 && list[^1].HasVowel && list[^1].Coda.Length == 0 && !list[^1].Long) {
            list[^1] = list[^1] with { Coda = coda };
        } else {
            list.Add(new Syllable("", "", "", coda));
        }
    }
}

/// <summary>Latin letters read as romaji ("sakura", "kya", "matte"), or null if they are not romaji.</summary>
public static class RomajiReader {
    static readonly string[] Onsets = { "sh", "ch", "ts", "ky", "gy", "ny", "hy", "by", "py", "my", "ry", "ly", "dy", "ty", "sy", "zy", "jy", "fy", "vy" };
    const string Consonants = "kgsztdnhbpmrwyfjvlc";

    /// <param name="strict">Only letters Japanese romaji uses, and only k/s/t/p doubled. Off, it also
    /// takes l, c, v and any doubled consonant, so a sound-out of an unknown English word gets somewhere.</param>
    public static List<Syllable>? Parse(string text, bool strict = false) {
        var s = text.ToLowerInvariant().Replace("'", "").Replace("-", "");
        if (s.Length == 0) return null;
        if (strict && s.Any(c => "lcqxv".IndexOf(c) >= 0)) return null;
        var result = new List<Syllable>();
        int i = 0;
        while (i < s.Length) {
            char c = s[i];
            if ("aiueo".IndexOf(c) >= 0) { result.Add(new Syllable("", "", c.ToString())); i++; continue; }
            if (c == 'n' && (i + 1 == s.Length || "aiueoy".IndexOf(s[i + 1]) < 0)) {
                AttachCoda(result, "n");   // the second n of "konnichiwa" starts "ni", so only this one is used
                i++;
                continue;
            }
            // kk, tt, ss ...: the first half closes the previous syllable
            if (Consonants.IndexOf(c) >= 0 && i + 1 < s.Length && s[i + 1] == c && c != 'n'
                && (!strict || "kstp".IndexOf(c) >= 0)) {
                AttachCoda(result, "q");
                i++;
                continue;
            }
            string? onset = Onsets.FirstOrDefault(o => string.CompareOrdinal(s, i, o, 0, o.Length) == 0);
            string on;
            string gl = "";
            if (onset != null) {
                i += onset.Length;
                if (onset.Length == 2 && onset[1] == 'y') {
                    on = onset[0].ToString();
                    gl = "y";
                    if (on == "s") { on = "sh"; gl = ""; }
                    else if (on == "t") { on = "ch"; gl = ""; }
                    else if (on is "z" or "j") { on = "j"; gl = ""; }
                    else if (on == "l") on = "r";
                } else {
                    on = onset;
                }
            } else if (Consonants.IndexOf(c) >= 0) {
                i++;
                on = c switch { 'l' => "r", 'c' => "k", _ => c.ToString() };
                if (on is "y" or "w") { gl = on; on = ""; }
            } else {
                return null;
            }
            if (i >= s.Length || "aiueo".IndexOf(s[i]) < 0) return null;
            result.Add(new Syllable(on, gl, s[i].ToString()));
            i++;
        }
        return result;
    }

    static void AttachCoda(List<Syllable> list, string coda) {
        if (list.Count > 0 && list[^1].HasVowel && list[^1].Coda.Length == 0) list[^1] = list[^1] with { Coda = coda };
        else list.Add(new Syllable("", "", "", coda));
    }
}

/// <summary>
/// English words to syllables through CMUdict/ARPAbet, folded onto what a Korean or
/// Japanese voice can sing: clusters get an epenthetic ㅡ/う (strike → s-t-r-ai-k), and
/// consonants a syllable cannot close become their own syllable.
/// </summary>
public static class EnglishReader {
    static readonly Lazy<IG2p?> G2p = new(() => {
        try { return new ArpabetG2p(); } catch (Exception) { return null; }
    });

    /// <summary>ARPAbet phones of a word, lowercase and without stress digits, or null if unknown.</summary>
    public static string[]? Phones(string word) {
        var w = word.ToLowerInvariant().Trim();
        if (w.Length == 0 || !w.All(c => (c >= 'a' && c <= 'z') || c == '\'' || c == '-')) return null;
        try {
            var p = G2p.Value?.Query(w);
            return p is { Length: > 0 } ? p.Select(x => new string(x.Where(char.IsLetter).ToArray())).ToArray() : null;
        } catch (Exception) {
            // onnxruntime is not available in every host; unknown words fall back to romaji.
            return null;
        }
    }

    public static List<Syllable>? Read(string word) {
        var phones = Phones(word);
        if (phones != null) return FromPhones(phones);
        return RomajiReader.Parse(word);
    }

    static bool IsVowel(string p) => p is "aa" or "ae" or "ah" or "ao" or "aw" or "ay" or "eh" or "er" or "ey" or "ih" or "iy" or "ow" or "oy" or "uh" or "uw";

    static readonly Dictionary<string, string> ConsonantOnset = new() {
        ["b"] = "b", ["ch"] = "ch", ["d"] = "d", ["dh"] = "d", ["f"] = "f", ["g"] = "g", ["hh"] = "h",
        ["jh"] = "j", ["k"] = "k", ["l"] = "l", ["m"] = "m", ["n"] = "n", ["ng"] = "n", ["p"] = "p",
        ["r"] = "r", ["s"] = "s", ["sh"] = "sh", ["t"] = "t", ["th"] = "s", ["v"] = "v", ["z"] = "z", ["zh"] = "j",
    };

    static readonly HashSet<string> CodaOk = new() { "n", "m", "ng", "l", "k", "p", "t" };

    public static List<Syllable> FromPhones(IReadOnlyList<string> phones) {
        var result = new List<Syllable>();
        var pending = new List<string>();   // consonants waiting for the next vowel
        int offGlide = -1;                  // index of a diphthong's second half
        foreach (var p in phones) {
            if (!IsVowel(p)) { pending.Add(p); continue; }
            var (on, gl) = TakeOnset(result, pending);
            bool first = true;
            foreach (var v in VowelSyllables(p)) {
                result.Add(new Syllable(on, gl, v));
                if (!first) offGlide = result.Count - 1;
                first = false;
                on = ""; gl = "";
            }
        }
        // Consonants after the last vowel: the first one may close it, the rest need a vowel of their own.
        pending.RemoveAll(p => p == "r" && result.Count > 0);   // non-rhotic tail: "car" -> カー
        for (int i = 0; i < pending.Count; i++) {
            var c = pending[i];
            if (i == 0 && result.Count > 0 && CodaOk.Contains(c) && result[^1].Coda.Length == 0 && offGlide != result.Count - 1) {
                result[^1] = result[^1] with { Coda = c };
            } else if (ConsonantOnset.TryGetValue(c, out var o)) {
                result.Add(new Syllable(o, "", "eu"));
            }
        }
        return result;
    }

    // Everything but the consonant next to the vowel is pushed out as an epenthetic syllable.
    static (string onset, string glide) TakeOnset(List<Syllable> result, List<string> pending) {
        string glide = "";
        if (pending.Count > 0 && pending[^1] is "w" or "y") {
            glide = pending[^1];
            pending.RemoveAt(pending.Count - 1);
        }
        string onset = "";
        if (pending.Count > 0) {
            var last = pending[^1];
            pending.RemoveAt(pending.Count - 1);
            onset = ConsonantOnset.GetValueOrDefault(last, "");
        }
        foreach (var c in pending) {
            if (ConsonantOnset.TryGetValue(c, out var o)) result.Add(new Syllable(o, "", "eu"));
        }
        pending.Clear();
        return (onset, glide);
    }

    static IEnumerable<string> VowelSyllables(string p) => p switch {
        "aa" => new[] { "a" },
        "ae" => new[] { "ae" },
        "ah" => new[] { "eo" },
        "ao" => new[] { "o" },
        "aw" => new[] { "a", "u" },
        "ay" => new[] { "a", "i" },
        "eh" => new[] { "e" },
        "er" => new[] { "eo" },
        "ey" => new[] { "e", "i" },
        "ih" => new[] { "i" },
        "iy" => new[] { "i" },
        "ow" => new[] { "o" },
        "oy" => new[] { "o", "i" },
        "uh" => new[] { "u" },
        "uw" => new[] { "u" },
        _ => new[] { "eu" },
    };
}
