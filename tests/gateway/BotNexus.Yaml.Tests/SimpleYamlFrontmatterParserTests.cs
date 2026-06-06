using Shouldly;
using BotNexus.Yaml;

namespace BotNexus.Yaml.Tests;

/// <summary>
/// Unit tests for <see cref="SimpleYamlFrontmatterParser"/>.
/// Covers all supported value forms per the issue #877 acceptance criteria.
/// </summary>
public sealed class SimpleYamlFrontmatterParserTests
{
    private static SimpleYamlFrontmatterParser Parser => SimpleYamlFrontmatterParser.Instance;

    // ── Plain scalars ─────────────────────────────────────────────────────────

    [Fact]
    public void Parse_PlainScalar_ExtractsKeyValue()
    {
        var frontmatter = "name: email-triage\ndescription: Triage incoming emails";
        var result = Parser.Parse(frontmatter);
        result["name"].ShouldBe("email-triage");
        result["description"].ShouldBe("Triage incoming emails");
    }

    [Fact]
    public void Parse_EmptyFrontmatter_ReturnsEmptyDictionary()
    {
        Parser.Parse("").ShouldBeEmpty();
        Parser.Parse("   ").ShouldBeEmpty();
    }

    // ── Quoted strings ────────────────────────────────────────────────────────

    [Fact]
    public void Parse_DoubleQuotedValue_UnquotesCorrectly()
    {
        var result = Parser.Parse("name: \"my skill\"");
        result["name"].ShouldBe("my skill");
    }

    [Fact]
    public void Parse_SingleQuotedValue_UnquotesCorrectly()
    {
        var result = Parser.Parse("name: 'my skill'");
        result["name"].ShouldBe("my skill");
    }

    [Fact]
    public void Parse_MixedQuoteStyles_HandledIndependently()
    {
        var frontmatter = "a: \"double\"\nb: 'single'\nc: plain";
        var result = Parser.Parse(frontmatter);
        result["a"].ShouldBe("double");
        result["b"].ShouldBe("single");
        result["c"].ShouldBe("plain");
    }

    // ── Block scalars ─────────────────────────────────────────────────────────

    [Fact]
    public void Parse_LiteralBlockScalar_PreservesNewlines()
    {
        var frontmatter = "description: |\n  Line one.\n  Line two.\n  Line three.";
        var result = Parser.Parse(frontmatter);
        result["description"].ShouldBe("Line one.\nLine two.\nLine three.");
    }

    [Fact]
    public void Parse_FoldedBlockScalar_JoinsWithSpaces()
    {
        var frontmatter = "description: >\n  Line one.\n  Line two.\n  Line three.";
        var result = Parser.Parse(frontmatter);
        result["description"].ShouldBe("Line one. Line two. Line three.");
    }

    [Fact]
    public void Parse_BlockScalar_FollowedByAnotherKey_BothExtracted()
    {
        var frontmatter = "description: |\n  Multi-line content.\n  Second line.\nlicense: MIT";
        var result = Parser.Parse(frontmatter);
        result["description"].ShouldBe("Multi-line content.\nSecond line.");
        result["license"].ShouldBe("MIT");
    }

    [Fact]
    public void Parse_BlockScalarAtEndOfFrontmatter_Flushed()
    {
        var frontmatter = "description: |\n  Only this.";
        var result = Parser.Parse(frontmatter);
        result["description"].ShouldBe("Only this.");
    }

    // ── Comments ──────────────────────────────────────────────────────────────

    [Fact]
    public void Parse_CommentLines_AreIgnored()
    {
        var frontmatter = "# This is a comment\nname: my-skill\n# Another comment\nversion: 1";
        var result = Parser.Parse(frontmatter);
        result.ContainsKey("#").ShouldBeFalse();
        result["name"].ShouldBe("my-skill");
        result["version"].ShouldBe("1");
    }

    // ── Nested metadata (ParseNested) ─────────────────────────────────────────

    [Fact]
    public void ParseNested_ExtractsChildKeyValuePairs()
    {
        var frontmatter = "name: test\nmetadata:\n  author: jon\n  category: productivity\nother: val";
        var result = Parser.ParseNested(frontmatter, "metadata");
        result["author"].ShouldBe("jon");
        result["category"].ShouldBe("productivity");
        result.Count.ShouldBe(2);
    }

    [Fact]
    public void ParseNested_MissingParentKey_ReturnsEmptyDictionary()
    {
        var frontmatter = "name: test\nauthor: jon";
        Parser.ParseNested(frontmatter, "metadata").ShouldBeEmpty();
    }

    [Fact]
    public void ParseNested_QuotedChildValues_UnquotedCorrectly()
    {
        var frontmatter = "metadata:\n  author: \"jon bullen\"\n  tag: 'skills'";
        var result = Parser.ParseNested(frontmatter, "metadata");
        result["author"].ShouldBe("jon bullen");
        result["tag"].ShouldBe("skills");
    }

    // ── Case-insensitivity ────────────────────────────────────────────────────

    [Fact]
    public void Parse_LookupIsCaseInsensitive()
    {
        var result = Parser.Parse("Name: my-skill\nDESCRIPTION: My tool");
        result["name"].ShouldBe("my-skill");
        result["description"].ShouldBe("My tool");
    }

    // ── Hyphenated keys ───────────────────────────────────────────────────────

    [Fact]
    public void Parse_HyphenatedKeys_ExtractedCorrectly()
    {
        var result = Parser.Parse("allowed-tools: bash python\ndisable-model-invocation: true");
        result["allowed-tools"].ShouldBe("bash python");
        result["disable-model-invocation"].ShouldBe("true");
    }

    // ── Indented keys not at top level ────────────────────────────────────────

    [Fact]
    public void Parse_SkipsIndentedLines_NotTopLevelKeys()
    {
        var frontmatter = "name: test\nmetadata:\n  author: jon\nname2: other";
        var result = Parser.Parse(frontmatter);
        // 'author' is nested, should not appear at top level
        result.ContainsKey("author").ShouldBeFalse();
        result["name2"].ShouldBe("other");
    }

    // ── Null / argument guards ────────────────────────────────────────────────

    [Fact]
    public void Parse_NullFrontmatter_ThrowsArgumentNullException() =>
        Should.Throw<ArgumentNullException>(() => Parser.Parse(null!));

    [Fact]
    public void ParseNested_NullFrontmatter_ThrowsArgumentNullException() =>
        Should.Throw<ArgumentNullException>(() => Parser.ParseNested(null!, "metadata"));

    [Fact]
    public void ParseNested_NullParentKey_ThrowsArgumentException() =>
        Should.Throw<ArgumentException>(() => Parser.ParseNested("name: x", null!));

    // ── SkillParser integration ───────────────────────────────────────────────

    [Fact]
    public void SimpleYamlFrontmatterParser_Instance_IsNotNull()
    {
        SimpleYamlFrontmatterParser.Instance.ShouldNotBeNull();
        SimpleYamlFrontmatterParser.Instance.ShouldBeOfType<SimpleYamlFrontmatterParser>();
    }

    [Fact]
    public void Parser_ImplementsIYamlFrontmatterParser()
    {
        (SimpleYamlFrontmatterParser.Instance is IYamlFrontmatterParser).ShouldBeTrue();
    }
}
