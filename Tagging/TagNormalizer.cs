using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace CartoonArchive.Tagging;

public static class TagNormalizer
{
    private const int MaxRawLength = 256;
    private const int MaxNormalizedLength = 64;

    private static readonly Regex WhitespaceRegex = new(@"\s+", RegexOptions.Compiled);

    /// <summary>
    /// Returns the canonical form of a tag name (lowercased, diacritic-folded, whitespace-collapsed), or
    /// <c>null</c> if the input is empty, too long, or normalizes to empty. SQLite's COLLATE NOCASE is ASCII-only,
    /// so tag uniqueness has to be enforced here before insert.
    /// </summary>
    public static string? Normalize(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return null;
        if (input.Length > MaxRawLength)
            return null;

        var collapsed = WhitespaceRegex.Replace(input.Trim(), " ");
        var lowered = collapsed.ToLowerInvariant();

        // 'ı' (U+0131) does not decompose to 'i' + combining mark under FormD,
        // so map it explicitly before decomposition.
        var preFolded = lowered.Replace("ı", "i");

        var decomposed = preFolded.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                sb.Append(c);
        }

        var result = sb.ToString();
        if (result.Length == 0 || result.Length > MaxNormalizedLength)
            return null;
        return result;
    }
}
