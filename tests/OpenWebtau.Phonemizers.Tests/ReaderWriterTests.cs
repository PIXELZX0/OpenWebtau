using OpenWebtau.Phonemizers;
using Xunit;

namespace OpenWebtau.Phonemizers.Tests;

public class ScriptTests {
    [Theory]
    [InlineData("안녕", AutoPhonemizer.Script.Hangul)]
    [InlineData("ありがとう", AutoPhonemizer.Script.Kana)]
    [InlineData("カタカナ", AutoPhonemizer.Script.Kana)]
    [InlineData("你好", AutoPhonemizer.Script.Han)]
    [InlineData("hello", AutoPhonemizer.Script.Latin)]
    [InlineData("日本語のうた", AutoPhonemizer.Script.Kana)]   // kanji beside kana is Japanese
    [InlineData("-", AutoPhonemizer.Script.Unknown)]
    [InlineData("R", AutoPhonemizer.Script.Latin)]
    [InlineData("", AutoPhonemizer.Script.Unknown)]
    public void Detect(string lyric, AutoPhonemizer.Script expected) =>
        Assert.Equal(expected, AutoPhonemizer.Detect(lyric));
}

public class HangulToOthersTests {
    static string Romaji(string lyric, string? prev = null, string? next = null) =>
        string.Join(" ", RomajiWriter.Render(HangulReader.Read(lyric, prev, next), _ => false).Select(p => p.Lyric));

    [Theory]
    [InlineData("가", "ga")]
    [InlineData("카", "ka")]
    [InlineData("사", "sa")]
    [InlineData("시", "shi")]
    [InlineData("차", "cha")]
    [InlineData("냐", "nya")]
    [InlineData("와", "wa")]
    [InlineData("안", "a n")]
    [InlineData("안녕", "a n nyo n")]   // no ㅕ in Japanese, so ㅓ falls to o
    public void ToRomaji(string hangul, string expected) => Assert.Equal(expected, Romaji(hangul));

    [Fact]
    public void LiaisonIsAppliedBeforeConverting() {
        // 꽃이 is pronounced 꼬치: the note 이 after 꽃 is sung "chi", not "i".
        Assert.Equal("chi", Romaji("이", prev: "꽃"));
        Assert.Equal("i", Romaji("이"));
    }

    [Fact]
    public void KoreanSpellingsAreUsedWhenTheBankHasThem() {
        var bank = new HashSet<string> { "geo", "neu" };
        var pieces = RomajiWriter.Render(HangulReader.Read("거느"), bank.Contains);
        Assert.Equal(new[] { "geo", "neu" }, pieces.Select(p => p.Lyric));
    }

    [Fact]
    public void KunreiAndHepburnBanksBothGetTheirOwnSpelling() {
        var syl = HangulReader.Read("시");
        Assert.Equal("si", RomajiWriter.Render(syl, a => a == "si")[0].Lyric);
        Assert.Equal("shi", RomajiWriter.Render(syl, a => a == "shi")[0].Lyric);
        Assert.Equal("shi", RomajiWriter.Render(syl, _ => false)[0].Lyric);   // neither: Hepburn
    }

    [Theory]
    [InlineData("카", "k aa")]
    [InlineData("시", "sh iy")]
    [InlineData("헐", "hh ah l")]
    [InlineData("차", "ch aa")]
    public void HangulToArpabet(string hangul, string hint) => Assert.Equal(hint, ArpaWriter.Hint(HangulReader.Read(hangul)));
}

public class KanaToOthersTests {
    static string Hangul(string kana) => new(HangulWriter.Render(KanaReader.Read(kana)).SelectMany(p => p.Lyric).ToArray());

    [Theory]
    [InlineData("あ", "아")]
    [InlineData("か", "카")]
    [InlineData("が", "가")]
    [InlineData("さ", "사")]
    [InlineData("し", "시")]
    [InlineData("しゃ", "샤")]
    [InlineData("しゅ", "슈")]
    [InlineData("ち", "치")]
    [InlineData("ちゃ", "차")]
    [InlineData("つ", "츠")]
    [InlineData("じ", "지")]
    [InlineData("た", "타")]
    [InlineData("だ", "다")]
    [InlineData("ふ", "후")]
    [InlineData("ふぁ", "파")]
    [InlineData("ぱ", "파")]
    [InlineData("ば", "바")]
    [InlineData("ま", "마")]
    [InlineData("ら", "라")]
    [InlineData("きゃ", "캬")]
    [InlineData("にょ", "뇨")]
    [InlineData("わ", "와")]
    [InlineData("を", "오")]
    [InlineData("うぃ", "위")]
    [InlineData("ん", "은")]
    [InlineData("ア", "아")]
    [InlineData("キョ", "쿄")]
    [InlineData("ありがとう", "아리가토우")]
    [InlineData("きって", "킷테")]
    public void KanaToHangul(string kana, string expected) => Assert.Equal(expected, Hangul(kana));

    [Fact]
    public void LongVowelMarkIsSkippedForHangul() =>
        Assert.Equal("카", Hangul("かー"));

    [Theory]
    [InlineData("か", "ka")]
    [InlineData("し", "shi")]
    [InlineData("ふ", "fu")]
    [InlineData("きゃ", "kya")]
    [InlineData("ちゃ", "cha")]
    [InlineData("ん", "n")]
    public void KanaToRomaji(string kana, string expected) =>
        Assert.Equal(expected, string.Join(" ", RomajiWriter.Render(KanaReader.Read(kana), _ => false).Select(p => p.Lyric)));

    [Fact]
    public void KanaUnitsKeepSmallKanaWithTheirBase() =>
        Assert.Equal(new[] { "きゃ", "っ", "と", "ふぁ", "ー" }, KanaReader.Units("きゃっとふぁー"));

    [Fact]
    public void SokuonAndMoraicNClosePreviousSyllable() {
        var s = KanaReader.Read("きって");
        Assert.Equal(2, s.Count);
        Assert.Equal("q", s[0].Coda);
        var n = KanaReader.Read("かん");
        Assert.Single(n);
        Assert.Equal("n", n[0].Coda);
    }
}

public class EnglishTests {
    [Fact]
    public void DictionaryIsAvailableWithoutOnnx() {
        // CMUdict words must resolve even when the neural fallback cannot load (the browser case).
        var phones = EnglishReader.Phones("hello");
        Assert.NotNull(phones);
        Assert.Contains("l", phones!);
    }

    [Theory]
    [InlineData("hello", "허로")]
    [InlineData("star", "스타")]
    [InlineData("love", "러브")]
    [InlineData("cat", "캣")]
    [InlineData("strike", "스트라이크")]
    public void EnglishToHangul(string word, string expected) {
        var syl = EnglishReader.Read(word)!;
        Assert.Equal(expected, new string(HangulWriter.Render(syl).SelectMany(p => p.Lyric).ToArray()));
    }

    [Fact]
    public void ClustersGetEpentheticVowels() {
        // "strike" cannot be sung as one Korean or Japanese syllable
        var syl = EnglishReader.FromPhones(new[] { "s", "t", "r", "ay", "k" });
        Assert.Equal("스트라이크", new string(HangulWriter.Render(syl).SelectMany(p => p.Lyric).ToArray()));
        Assert.Equal("すとらいく", string.Join("", KanaWriter.Render(syl, false, _ => true).Select(p => p.Lyric)));
    }

    [Fact]
    public void WordFinalConsonantsBecomeBatchimOrOwnSyllable() {
        var hangul = new string(HangulWriter.Render(EnglishReader.FromPhones(new[] { "k", "ae", "t" })).SelectMany(p => p.Lyric).ToArray());
        Assert.Equal("캣", hangul);
        var dish = new string(HangulWriter.Render(EnglishReader.FromPhones(new[] { "d", "ih", "sh" })).SelectMany(p => p.Lyric).ToArray());
        Assert.Equal("디시", dish);
    }

    [Fact]
    public void EnglishToKana() {
        var syl = EnglishReader.FromPhones(new[] { "k", "ae", "t" });
        var kana = string.Join("", KanaWriter.Render(syl, false, _ => true).Select(p => p.Lyric));
        Assert.Equal("けと", kana);   // ae has no kana of its own: e; the stop becomes と
        var kata = string.Join("", KanaWriter.Render(EnglishReader.FromPhones(new[] { "s", "t", "aa", "r" }), true, _ => true).Select(p => p.Lyric));
        Assert.Equal("スタ", kata);
    }

    [Fact]
    public void KanaWriterFallsBackToWhatTheBankHas() {
        var syl = EnglishReader.FromPhones(new[] { "t", "iy" });
        Assert.Equal("てぃ", KanaWriter.Render(syl, false, _ => true)[0].Lyric);
        Assert.Equal("ち", KanaWriter.Render(syl, false, a => a == "ち")[0].Lyric);
    }

    [Fact]
    public void ArpabetHint() {
        var syl = HangulReader.Read("차");
        Assert.Equal("ch aa", ArpaWriter.Hint(syl));
    }
}

public class RomajiTests {
    [Theory]
    [InlineData("sakura", 3)]
    [InlineData("kya", 1)]
    [InlineData("matte", 2)]
    [InlineData("konnichiwa", 4)]   // ko(n) ni chi wa
    public void Parses(string text, int syllables) => Assert.Equal(syllables, RomajiReader.Parse(text)!.Count);

    [Fact]
    public void KonnichiwaKeepsItsNi() {
        var s = RomajiReader.Parse("konnichiwa")!;
        Assert.Equal("n", s[0].Coda);
        Assert.Equal(("n", "i"), (s[1].Onset, s[1].Vowel));
        Assert.Equal("ch", s[2].Onset);
    }

    [Fact]
    public void EnglishIsNotStrictRomaji() {
        Assert.Null(RomajiReader.Parse("hello", strict: true));
        Assert.Null(RomajiReader.Parse("street", strict: true));
        Assert.NotNull(RomajiReader.Parse("kimi", strict: true));
    }
}
