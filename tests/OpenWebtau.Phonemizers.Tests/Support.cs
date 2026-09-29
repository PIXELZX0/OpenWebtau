using OpenUtau.Api;
using OpenUtau.Classic;
using OpenUtau.Core;
using OpenUtau.Core.Format;
using OpenUtau.Core.Ustx;

namespace OpenWebtau.Phonemizers.Tests;

/// <summary>A voicebank that is only a list of aliases, so tests need no wav files.</summary>
public sealed class FakeSinger : USinger {
    readonly Dictionary<string, UOto> byAlias = new();
    readonly List<UOto> list = new();
    readonly string? declared;
    readonly List<USubbank> subbanks;

    public FakeSinger(IEnumerable<string> aliases, string? defaultPhonemizer = null) {
        declared = defaultPhonemizer;
        found = true;
        loaded = true;
        var sub = new USubbank(new Subbank { Color = "", Prefix = "", Suffix = "", ToneRanges = Array.Empty<string>() });
        subbanks = new List<USubbank> { sub };
        var set = new UOtoSet(new OtoSet { Name = "fake" }, "");
        foreach (var a in aliases) {
            var oto = new UOto(new Oto { Alias = a, Wav = "" }, set, new[] { sub });
            byAlias[a] = oto;
            list.Add(oto);
        }
    }

    public override string Id => "fake";
    public override string Location => "";
    public override USingerType SingerType => USingerType.Classic;
    public override string DefaultPhonemizer => declared!;
    public override IList<UOto> Otos => list;
    public override IList<USubbank> Subbanks => subbanks;
    public override bool TryGetOto(string phoneme, out UOto oto) => byAlias.TryGetValue(phoneme, out oto!);
}

public static class Banks {
    public const string KoreanCV = "OpenUtau.Plugin.Builtin.KoreanCVPhonemizer";
    public const string JapaneseVCV = "OpenUtau.Plugin.Builtin.JapaneseVCVPhonemizer";

    // Every syllable block a small Korean CV bank would cover, plus what a CV bank names its parts.
    public static FakeSinger Hangul(string? declared = KoreanCV) {
        var aliases = new List<string>();
        for (char c = '가'; c <= '힣'; c++) aliases.Add(c.ToString());
        return new FakeSinger(aliases, declared);
    }

    public static readonly string[] Hiragana = (
        "あ い う え お か き く け こ が ぎ ぐ げ ご さ し す せ そ ざ じ ず ぜ ぞ た ち つ て と だ で ど " +
        "な に ぬ ね の は ひ ふ へ ほ ば び ぶ べ ぼ ぱ ぴ ぷ ぺ ぽ ま み む め も や ゆ よ ら り る れ ろ わ を ん " +
        "きゃ きゅ きょ しゃ しゅ しょ ちゃ ちゅ ちょ にゃ にゅ にょ ひゃ ひゅ ひょ みゃ みゅ みょ りゃ りゅ りょ").Split(' ');

    public static FakeSinger Kana(string? declared = null) => new(Hiragana, declared);

    public static FakeSinger Romaji() => new((
        "a i u e o ka ki ku ke ko ga gi gu ge go sa shi su se so za ji zu ze zo ta chi tsu te to da de do " +
        "na ni nu ne no ha hi fu he ho ba bi bu be bo pa pi pu pe po ma mi mu me mo ya yu yo ra ri ru re ro wa n " +
        "kya kyu kyo sha shu sho cha chu cho nya nyu nyo").Split(' '));

    public static FakeSinger Arpa() => new(
        "aa ae ah ao eh ih iy uh uw k g s sh z t d n m hh f b p l r y w".Split(' ').Concat(new[] { "- aa" }),
        "OpenUtau.Plugin.Builtin.ArpasingPhonemizer");
}

public static class Runner {
    public record Note(string Lyric, int Position = 0, int Duration = 480, string? Hint = null);

    /// <summary>Runs a phonemizer the way PhonemizerRunner does: SetUp once, Process from the last group back.</summary>
    public static List<string[]> Run(Phonemizer p, USinger singer, params Note[] notes) {
        var groups = new List<Phonemizer.Note[]>();
        foreach (var n in notes) {
            var note = new Phonemizer.Note {
                lyric = n.Lyric, phoneticHint = n.Hint, tone = 60, position = n.Position, duration = n.Duration,
                phonemeAttributes = Array.Empty<Phonemizer.PhonemeAttributes>(),
            };
            if (n.Lyric.StartsWith("+") && groups.Count > 0) {
                groups[^1] = groups[^1].Append(note).ToArray();
            } else {
                groups.Add(new[] { note });
            }
        }
        var project = new UProject();
        Ustx.AddDefaultExpressions(project);
        var track = project.tracks[0];
        project.expressions.TryGetValue(Ustx.CLR, out var descriptor);
        track.VoiceColorExp = descriptor.Clone();
        track.VoiceColorExp.options = new[] { "" };
        track.VoiceColorExp.max = 0;
        var timeAxis = new TimeAxis();
        timeAxis.BuildSegments(project);

        p.Testing = true;
        p.SetSinger(singer);
        p.SetTiming(timeAxis);
        p.SetUp(groups.ToArray(), project, track);

        var results = new string[groups.Count][];
        for (int i = groups.Count - 1; i >= 0; i--) {
            var r = p.Process(
                groups[i],
                i > 0 ? groups[i - 1][0] : null,
                i < groups.Count - 1 ? groups[i + 1][0] : null,
                null, null, Array.Empty<Phonemizer.Note>());
            results[i] = r.phonemes.Select(x => $"{x.phoneme}@{x.position}").ToArray();
        }
        p.CleanUp();
        return results.ToList();
    }

    public static string Flat(List<string[]> r) => string.Join(" | ", r.Select(x => string.Join(",", x)));

    public static AutoPhonemizer Auto() => new() { Testing = true };
}
