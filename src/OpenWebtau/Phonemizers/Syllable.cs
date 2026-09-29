namespace OpenWebtau.Phonemizers;

/// <summary>
/// One sung syllable, written in a language-neutral inventory so any reader (Hangul,
/// kana, English) can hand it to any writer (Hangul, romaji, ARPAbet).
///
/// Onset:  "" k g s sh z j t ch ts d n h f b p m r l v
/// Glide:  "" y w
/// Vowel:  a i u e o, plus ae (ㅐ, English æ), eo (ㅓ, English ʌ) and eu (ㅡ). It is also what a
///         consonant borrows when the source language has a cluster the target cannot sing
///         ("strike" becomes s-eu t-eu r-eu ...), which is why writers treat eu specially.
///         "" means there is no vowel, as for ん or っ standing alone.
/// Coda:   "" n m ng l k t p, or q for the sokuon ッ.
/// </summary>
public sealed record Syllable(string Onset, string Glide, string Vowel, string Coda = "", bool Long = false) {
    public bool HasVowel => Vowel.Length > 0;
}

/// <summary>What a writer produces for one note-sized chunk of a lyric.</summary>
/// <param name="Lyric">The text the target phonemizer should see.</param>
/// <param name="Hint">Phonetic hint for phonemizers that take one (ARPAbet for the English ones).</param>
/// <param name="Weight">Share of the note's length this piece gets when a lyric is split.</param>
public sealed record Piece(string Lyric, string? Hint = null, int Weight = 1);
