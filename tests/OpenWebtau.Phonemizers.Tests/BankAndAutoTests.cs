using OpenUtau.Api;
using OpenUtau.Plugin.Builtin;
using OpenWebtau.Phonemizers;
using Xunit;
using static OpenWebtau.Phonemizers.Tests.Runner;

namespace OpenWebtau.Phonemizers.Tests;

public class BankProfileTests {
    static FakeSinger Bank(string aliases, string? declared = null) => new(aliases.Split(' '), declared);

    [Fact]
    public void DeclaredLanguageWins() {
        Assert.Equal(BankKind.Hangul, BankProfile.Inspect(Bank("a i u", Banks.KoreanCV)).Kind);
        Assert.Equal(BankKind.Kana, BankProfile.Inspect(Bank("a i u", "OpenUtau.Plugin.Builtin.JapaneseCVVCPhonemizer")).Kind);
        Assert.Equal(BankKind.Arpa, BankProfile.Inspect(Banks.Arpa()).Kind);
    }

    [Fact]
    public void UndeclaredBanksAreJudgedByTheirAliases() {
        Assert.Equal(BankKind.Hangul, BankProfile.Inspect(Bank("가 나 다 라")).Kind);
        Assert.Equal(BankKind.Kana, BankProfile.Inspect(Bank("あ い う え お")).Kind);
        Assert.Equal(BankKind.Romaji, BankProfile.Inspect(Banks.Romaji()).Kind);
        Assert.Equal(BankKind.Chinese, BankProfile.Inspect(Bank("你 好")).Kind);
        Assert.Equal(BankKind.Unknown, BankProfile.Inspect(Bank("")).Kind);
    }

    [Fact]
    public void ArpaBanksAreToldApartFromRomajiBanks() {
        var arpa = BankProfile.Inspect(Bank("aa ae ah ao eh ih iy uh uw k g s t d n m l r"));
        Assert.Equal(BankKind.Arpa, arpa.Kind);
    }

    [Fact]
    public void KatakanaAndVcvAreDetected() {
        Assert.True(BankProfile.Inspect(Bank("ア イ ウ エ オ カ キ")).Katakana);
        Assert.False(BankProfile.Inspect(Bank("あ い う")).Katakana);
        Assert.True(BankProfile.Inspect(new FakeSinger(new[] { "a か", "a き", "a く", "i か", "i き", "i く" })).Vcv);
        Assert.False(BankProfile.Inspect(Bank("あ い う")).Vcv);
    }
}

public class AutoPhonemizerTests {
    static List<string[]> Run(FakeSinger s, params Note[] notes) => Runner.Run(Auto(), s, notes);

    static string[] Aliases(string[] result) => result.Select(r => r[..r.LastIndexOf('@')]).ToArray();
    static int[] Positions(string[] result) => result.Select(r => int.Parse(r[(r.LastIndexOf('@') + 1)..])).ToArray();

    // Aliases the Korean CV phonemizer's default template asks for.
    static FakeSinger KoreanBank() => new(
        "a ga na nyeo heo ro ri to u a n|eo ng".Split(' '), Banks.KoreanCV);

    [Fact]
    public void KanaOnAKoreanBankIsSungAsKorean() {
        var r = Run(KoreanBank(), new Note("ありがとう"));
        Assert.Equal(new[] { "a", "ri", "ga", "to", "u" }, Aliases(r[0]));
    }

    [Fact]
    public void ALyricOfSeveralSyllablesSharesTheNoteLength() {
        var r = Run(KoreanBank(), new Note("ありがとう", 0, 480));
        Assert.Equal(new[] { 0, 96, 192, 288, 384 }, Positions(r[0]));
    }

    [Fact]
    public void EnglishOnAKoreanBank() {
        var r = Run(KoreanBank(), new Note("hello", 0, 480));
        Assert.Equal(new[] { "heo", "ro" }, Aliases(r[0]));
        Assert.Equal(new[] { 0, 240 }, Positions(r[0]));
    }

    [Fact]
    public void KoreanOnAKoreanBankMatchesThePlainKoreanPhonemizer() {
        var bank = KoreanBank();
        var auto = Run(bank, new Note("나", 0, 480), new Note("가", 480, 480));
        var plain = Runner.Run(new KoreanCVPhonemizer { Testing = true }, bank, new Note("나", 0, 480), new Note("가", 480, 480));
        Assert.Equal(Flat(plain), Flat(auto));
    }

    [Fact]
    public void AMultiSyllableKoreanLyricIsSplitOnAKoreanBank() {
        var r = Run(KoreanBank(), new Note("안녕", 0, 480));
        Assert.True(r[0].Length >= 2);
        Assert.Equal(Positions(r[0]).OrderBy(x => x), Positions(r[0]));   // in order
        Assert.All(Positions(r[0]), p => Assert.InRange(p, 0, 479));
    }

    [Fact]
    public void KoreanOnAJapaneseBankUsesKoToJa() {
        var r = Run(Banks.Kana(), new Note("안", 0, 480));
        Assert.Equal(new[] { "あ", "ん" }, Aliases(r[0]));
    }

    [Fact]
    public void RomajiAndEnglishOnAJapaneseBank() {
        Assert.Equal(new[] { "か" }, Aliases(Run(Banks.Kana(), new Note("ka"))[0]));
        Assert.Equal(new[] { "ほ", "ろ" }, Aliases(Run(Banks.Kana(), new Note("hello", 0, 480))[0]));
    }

    [Fact]
    public void KatakanaBanksGetKatakana() {
        var kata = new FakeSinger("ア イ ウ エ オ カ キ ク ケ コ ホ ロ ヘ".Split(' '));
        Assert.Equal(new[] { "ホ", "ロ" }, Aliases(Run(kata, new Note("hello", 0, 480))[0]));
        Assert.Equal(new[] { "カ" }, Aliases(Run(kata, new Note("ka"))[0]));
    }

    [Fact]
    public void RomajiBankGetsRomaji() {
        Assert.Equal(new[] { "ka" }, Aliases(Run(Banks.Romaji(), new Note("카"))[0]));
        Assert.Equal(new[] { "sha" }, Aliases(Run(Banks.Romaji(), new Note("しゃ"))[0]));
        Assert.Equal(new[] { "a", "n", "nyo", "n" }, Aliases(Run(Banks.Romaji(), new Note("안녕", 0, 480))[0]));
    }

    [Fact]
    public void EnglishBanksTakeArpabetHints() {
        var r = Run(Banks.Arpa(), new Note("카"));
        Assert.Contains("k", Aliases(r[0]).Select(a => a.Replace("- ", "")));
        Assert.Contains("aa", Aliases(r[0]).Select(a => a.Replace("- ", "").Replace(" -", "")));
    }

    [Fact]
    public void AMixedTrackConvertsEachNoteOnItsOwn() {
        var r = Run(KoreanBank(),
            new Note("あ", 0, 480), new Note("나", 480, 480), new Note("hello", 960, 480), new Note("-", 1440, 240));
        Assert.Equal(new[] { "a" }, Aliases(r[0]));
        Assert.Equal(new[] { "heo", "ro" }, Aliases(r[2]));
        Assert.Equal(4, r.Count);
    }

    [Fact]
    public void APhoneticHintIsLeftAlone() {
        // The user already said how it is pronounced, so nothing is converted.
        var r = Run(Banks.Kana(), new Note("안", 0, 480, "a"));
        Assert.DoesNotContain("ん", Aliases(r[0]));
    }

    [Fact]
    public void ExtenderNotesStayWithTheLastSyllable() {
        var r = Run(KoreanBank(), new Note("ありがとう", 0, 480), new Note("+", 480, 480));
        Assert.Single(r);
        Assert.Equal(new[] { "a", "ri", "ga", "to", "u" }, Aliases(r[0]));
    }

    [Fact]
    public void ABankWithNothingInItFallsBackToTheLyric() {
        var r = Run(new FakeSinger(Array.Empty<string>()), new Note("あ"));
        Assert.Equal(new[] { "あ" }, Aliases(r[0]));
    }

    [Fact]
    public void KanjiOnANonChineseBankIsLeftAsTyped() {
        var r = Run(Banks.Kana(), new Note("愛"));
        Assert.Equal(new[] { "愛" }, Aliases(r[0]));
    }

    [Fact]
    public void ReusingThePhonemizerAcrossRunsGivesTheSameResult() {
        var auto = Auto();
        var bank = KoreanBank();
        var a = Flat(Runner.Run(auto, bank, new Note("あ")));
        var b = Flat(Runner.Run(auto, bank, new Note("あ")));
        Assert.Equal(a, b);
    }
}
