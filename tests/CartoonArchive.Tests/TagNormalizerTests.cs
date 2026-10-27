using CartoonArchive.Tagging;

namespace CartoonArchive.Tests;

/// <summary>
/// Tag uniqueness is enforced in C#, not the database - SQLite's COLLATE NOCASE is
/// ASCII-only, so it would happily store "Firavun" and "firavun" as two rows. That makes
/// Normalize the single source of truth for tag identity.
/// </summary>
public class TagNormalizerTests
{
    [Theory]
    [InlineData("firavun", "firavun")]
    [InlineData("Firavun", "firavun")]
    [InlineData("FIRAVUN", "firavun")]
    [InlineData("  firavun  ", "firavun")]
    [InlineData("iki   kelime", "iki kelime")]
    [InlineData("tab\tseparated", "tab separated")]
    public void Lowercases_trims_and_collapses_whitespace(string input, string expected)
    {
        Assert.Equal(expected, TagNormalizer.Normalize(input));
    }

    /// <summary>
    /// The Turkish dotless i is the case this function exists for. U+0131 has no
    /// decomposition under FormD, so stripping combining marks alone would leave it
    /// untouched and 'kibris' spelled with dotless i would never match the ASCII spelling.
    /// </summary>
    [Theory]
    [InlineData("kıbrıs", "kibris")]
    [InlineData("KIBRIS", "kibris")]
    [InlineData("Kıbrıs", "kibris")]
    public void Folds_dotless_i(string input, string expected)
    {
        Assert.Equal(expected, TagNormalizer.Normalize(input));
    }

    /// <summary>
    /// Regression: the dotted capital I is the mirror image of the dotless-i problem and
    /// was broken until this test was written. U+0130 is untouched by ToLowerInvariant,
    /// then FormD splits it into ASCII 'I' + combining dot; stripping the mark left a
    /// capital letter, so "İzmir" normalized to "Izmir" and got its own tag row separate
    /// from "izmir" and "IZMIR".
    /// </summary>
    [Theory]
    [InlineData("İzmir")]
    [InlineData("izmir")]
    [InlineData("IZMIR")]
    [InlineData("İZMİR")]
    public void Every_spelling_of_a_dotted_i_name_normalizes_alike(string input)
    {
        Assert.Equal("izmir", TagNormalizer.Normalize(input));
    }

    [Fact]
    public void Result_is_always_lowercase()
    {
        foreach (var input in new[] { "İ", "I", "ı", "İstanbul", "ĞÜÇÖİŞ" })
        {
            var normalized = TagNormalizer.Normalize(input);
            Assert.NotNull(normalized);
            Assert.Equal(normalized, normalized!.ToLowerInvariant());
        }
    }

    [Theory]
    [InlineData("şeker", "seker")] // s-cedilla
    [InlineData("ğüçöİ", "gucoi")] // g-breve, u/c/o diacritics, dotted capital I
    [InlineData("Ömer", "omer")]
    [InlineData("café", "cafe")]
    [InlineData("Ñoño", "nono")]
    public void Folds_diacritics(string input, string expected)
    {
        Assert.Equal(expected, TagNormalizer.Normalize(input));
    }

    /// <summary>
    /// All four of these reach the same null, and every caller treats null as "skip this
    /// name" rather than as an error.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\r\n")]
    public void Returns_null_for_empty_input(string? input)
    {
        Assert.Null(TagNormalizer.Normalize(input));
    }

    [Fact]
    public void Returns_null_when_raw_input_is_too_long()
    {
        Assert.Null(TagNormalizer.Normalize(new string('a', 257)));
    }

    [Fact]
    public void Accepts_input_at_the_normalized_length_limit()
    {
        var atLimit = new string('a', 64);
        Assert.Equal(atLimit, TagNormalizer.Normalize(atLimit));
    }

    [Fact]
    public void Returns_null_just_past_the_normalized_length_limit()
    {
        Assert.Null(TagNormalizer.Normalize(new string('a', 65)));
    }

    /// <summary>
    /// The length check runs against the normalized form, so a name that is only too long
    /// because of combining marks still fits. Guards the fold-then-measure ordering.
    /// </summary>
    [Fact]
    public void Measures_length_after_folding()
    {
        // Explicit combining acute (U+0301) rather than a precomposed character, so the
        // raw length is unambiguously two chars per letter: 128 raw, 64 once folded.
        var withMarks = string.Concat(Enumerable.Repeat("é", 64));
        Assert.Equal(128, withMarks.Length);
        Assert.Equal(new string('e', 64), TagNormalizer.Normalize(withMarks));
    }

    [Fact]
    public void Normalization_is_idempotent()
    {
        var once = TagNormalizer.Normalize("  Kıbrıs   ŞEKER  ");
        Assert.Equal(once, TagNormalizer.Normalize(once));
    }
}
