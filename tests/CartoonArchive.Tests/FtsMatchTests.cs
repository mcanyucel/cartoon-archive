using CartoonArchive.Api;

namespace CartoonArchive.Tests;

/// <summary>
/// BuildFtsMatch is the boundary between user input and an FTS5 MATCH expression. The
/// strategy is to wrap every token in double quotes so FTS5 reads it as a literal phrase,
/// which neutralizes operators, and to append * for Turkish suffix matching.
/// </summary>
public class FtsMatchTests
{
    [Fact]
    public void Quotes_each_token_and_appends_prefix_operator()
    {
        Assert.Equal("\"firavun\" * \"robin\" *", ImageEndpoints.BuildFtsMatch("firavun robin"));
    }

    [Fact]
    public void Collapses_runs_of_whitespace()
    {
        Assert.Equal(
            "\"a\" * \"b\" *",
            ImageEndpoints.BuildFtsMatch("  a \t\t b \r\n ")
        );
    }

    /// <summary>
    /// FTS5 operators only mean something outside a quoted phrase. Each of these must come
    /// back wrapped in quotes rather than reaching the parser as syntax.
    /// </summary>
    [Theory]
    [InlineData("AND")]
    [InlineData("OR")]
    [InlineData("NOT")]
    [InlineData("NEAR")]
    [InlineData("column:value")]
    [InlineData("(grouped)")]
    [InlineData("^anchor")]
    [InlineData("trailing*")]
    [InlineData("a-b")]
    public void Neutralizes_fts_syntax_by_quoting(string token)
    {
        Assert.Equal($"\"{token}\" *", ImageEndpoints.BuildFtsMatch(token));
    }

    /// <summary>
    /// A double quote is the one character that can't be escaped inside an FTS5 phrase, so
    /// it is stripped. If it survived, it would close the phrase early and hand the rest of
    /// the input to the parser as syntax.
    /// </summary>
    [Theory]
    [InlineData("a\"b", "\"ab\" *")]
    [InlineData("\"", "")]
    [InlineData("\"\"\"", "")]
    [InlineData("a\" OR \"b", "\"a\" * \"OR\" * \"b\" *")]
    public void Strips_double_quotes(string input, string expected)
    {
        Assert.Equal(expected, ImageEndpoints.BuildFtsMatch(input));
    }

    /// <summary>
    /// An empty result is meaningful: HandleSearch checks for it and short-circuits to an
    /// empty result set rather than running a MATCH against an empty expression.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\r\n")]
    [InlineData("\"\"")]
    public void Returns_empty_when_nothing_survives(string input)
    {
        Assert.Equal("", ImageEndpoints.BuildFtsMatch(input));
    }

    [Fact]
    public void Preserves_non_ascii_tokens()
    {
        // Turkish input has to pass through intact for the FTS5 unicode61 tokenizer and
        // its remove_diacritics setting to do their job.
        Assert.Equal("\"şşşğü\" *", ImageEndpoints.BuildFtsMatch("şşşğü"));
    }
}
