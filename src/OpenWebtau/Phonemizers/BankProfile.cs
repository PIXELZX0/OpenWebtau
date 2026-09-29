using System.Text.RegularExpressions;
using OpenUtau.Api;
using OpenUtau.Core.Ustx;
using OpenUtau.Plugin.Builtin;

namespace OpenWebtau.Phonemizers;

public enum BankKind { Unknown, Hangul, Kana, Romaji, Arpa, Chinese, Other }

/// <summary>
/// What a voicebank sings in. The declared phonemizer in character.yaml is trusted first;
/// a bank that names none is judged by the script of its oto aliases.
/// </summary>
public sealed class BankProfile {
    public BankKind Kind { get; private init; }
    public bool Katakana { get; private init; }
    public bool Vcv { get; private init; }

    /// <summary>The phonemizer character.yaml names, if it names one that is registered.</summary>
    public Type? Declared { get; private init; }

    public static readonly BankProfile Empty = new();

    static readonly Regex VcvAlias = new(@"^[\p{L}\-]+ \p{L}+$", RegexOptions.Compiled);
    static readonly Regex RomajiAlias = new(@"^(?:(?:sh|ch|ts|[kgsztdnhbpmrwyfjv]y?)?[aiueo]|n)$", RegexOptions.Compiled);
    static readonly HashSet<string> ArpaVowels = new() { "aa", "ae", "ah", "ao", "eh", "ih", "iy", "uh", "uw" };

    public static BankProfile Inspect(USinger? singer) {
        if (singer == null) return Empty;
        Type? declared = null;
        string? language = null;
        if (!string.IsNullOrEmpty(singer.DefaultPhonemizer)) {
            var f = PhonemizerFactory.Get(singer.DefaultPhonemizer);
            if (f != null && f.type != typeof(AutoPhonemizer)) {
                declared = f.type;
                language = f.language?.ToUpperInvariant();
            }
        }

        int hangul = 0, hira = 0, kata = 0, latin = 0, han = 0, romaji = 0, arpa = 0, vcv = 0, total = 0;
        try {
            foreach (var oto in singer.Otos) {
                var a = oto.Alias;
                if (string.IsNullOrEmpty(a)) continue;
                if (++total > 4000) break;
                switch (AutoPhonemizer.Detect(a)) {
                    case AutoPhonemizer.Script.Hangul: hangul++; break;
                    case AutoPhonemizer.Script.Han: han++; break;
                    case AutoPhonemizer.Script.Latin:
                        latin++;
                        if (RomajiAlias.IsMatch(a)) romaji++;
                        if (ArpaVowels.Contains(a) || a.Split(' ').Any(ArpaVowels.Contains)) arpa++;
                        break;
                    case AutoPhonemizer.Script.Kana:
                        if (a.Any(c => c >= 0x30A1 && c <= 0x30FA)) kata++; else hira++;
                        break;
                }
                if (VcvAlias.IsMatch(a)) vcv++;
            }
        } catch (Exception) {
            // A bank that has not loaded has no otos yet; the declared phonemizer still says something.
        }

        BankKind kind = language switch {
            "KO" => BankKind.Hangul,
            "JA" => BankKind.Kana,
            "ZH" or "YUE" => BankKind.Chinese,
            "EN" => declared == typeof(ArpasingPhonemizer) || declared == typeof(ArpasingPlusPhonemizer)
                ? BankKind.Arpa : BankKind.Other,
            null => Guess(hangul, hira + kata, han, latin, romaji, arpa),
            _ => BankKind.Other,
        };
        return new BankProfile {
            Kind = kind,
            Declared = declared,
            Katakana = kata > hira,
            Vcv = total > 0 && vcv * 4 > total,
        };
    }

    static BankKind Guess(int hangul, int kana, int han, int latin, int romaji, int arpa) {
        int best = Math.Max(Math.Max(hangul, kana), Math.Max(han, latin));
        if (best == 0) return BankKind.Unknown;
        if (best == hangul) return BankKind.Hangul;
        if (best == kana) return BankKind.Kana;
        if (best == han) return BankKind.Chinese;
        if (arpa > 0 && arpa * 4 >= latin && romaji * 3 < latin) return BankKind.Arpa;
        return romaji * 3 >= latin ? BankKind.Romaji : BankKind.Other;
    }

    /// <summary>The phonemizer that reads lyrics already written the way this bank sings.</summary>
    public Type Native(AutoPhonemizer.Script lyric) {
        if (Declared != null) return Declared;
        return Kind switch {
            BankKind.Hangul => typeof(KoreanCVPhonemizer),
            BankKind.Kana when Vcv => typeof(JapaneseVCVPhonemizer),
            BankKind.Arpa => typeof(ArpasingPhonemizer),
            BankKind.Chinese when lyric == AutoPhonemizer.Script.Han => typeof(ChineseCVVMonophonePhonemizer),
            _ => typeof(OpenUtau.Core.DefaultPhonemizer),
        };
    }
}
