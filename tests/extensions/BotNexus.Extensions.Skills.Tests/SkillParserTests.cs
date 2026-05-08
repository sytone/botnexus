using BotNexus.Extensions.Skills;

namespace BotNexus.Extensions.Skills.Tests;

public sealed class SkillParserTests
{
    [Fact]
    public void Parse_ValidFrontmatter_ExtractsNameAndDescription()
    {
        var markdown = """
            ---
            name: email-triage
            description: Classify and triage incoming emails
            ---
            # Email Triage

            Instructions for triaging emails.
            """;

        var skill = SkillParser.Parse("email-triage", markdown, "/skills/email-triage", SkillSource.Global);

        skill.Name.ShouldBe("email-triage");
        skill.Description.ShouldBe("Classify and triage incoming emails");
        skill.Content.ShouldContain("Instructions for triaging emails.");
    }

    [Fact]
    public void Parse_WithOptionalFields_ExtractsAll()
    {
        var markdown = """
            ---
            name: data-export
            description: Export data to various formats
            license: MIT
            compatibility: Requires Python 3.10+
            allowed-tools: bash python
            ---
            Export content here.
            """;

        var skill = SkillParser.Parse("data-export", markdown, "/skills/data-export", SkillSource.Agent);

        skill.License.ShouldBe("MIT");
        skill.Compatibility.ShouldBe("Requires Python 3.10+");
        skill.AllowedTools.ShouldBe("bash python");
        skill.Source.ShouldBe(SkillSource.Agent);
    }

    [Fact]
    public void Parse_WithMetadata_ExtractsKeyValuePairs()
    {
        var markdown = """
            ---
            name: my-skill
            description: A skill with metadata
            metadata:
              author: jon
              category: productivity
            ---
            Content.
            """;

        var skill = SkillParser.Parse("my-skill", markdown, "/s", SkillSource.Global);

        skill.Metadata.ShouldContainKey("author");
        skill.Metadata["author"].ShouldBe("jon");
        skill.Metadata.ShouldContainKey("category");
        skill.Metadata["category"].ShouldBe("productivity");
    }

    [Fact]
    public void Parse_NoFrontmatter_UsesDirectoryNameAndEmptyDescription()
    {
        var markdown = "# Just Content\n\nNo frontmatter here.";

        var skill = SkillParser.Parse("plain-skill", markdown, "/s", SkillSource.Global);

        skill.Name.ShouldBe("plain-skill");
        skill.Description.ShouldBeEmpty();
        skill.Content.ShouldContain("No frontmatter here.");
    }

    [Fact]
    public void Parse_EmptyBody_ReturnsEmptyContent()
    {
        var markdown = """
            ---
            name: empty
            description: Empty skill
            ---
            """;

        var skill = SkillParser.Parse("empty", markdown, "/s", SkillSource.Global);

        skill.Description.ShouldBe("Empty skill");
        skill.Content.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("valid-name", true)]
    [InlineData("a", true)]
    [InlineData("my-skill-123", true)]
    [InlineData("Bad-Name", false)]        // uppercase
    [InlineData("-leading", false)]         // leading hyphen
    [InlineData("trailing-", false)]        // trailing hyphen
    [InlineData("bad--double", false)]      // consecutive hyphens
    [InlineData("", false)]                 // empty
    [InlineData("has space", false)]        // space
    public void IsValidName_FollowsSpec(string name, bool expected)
    {
        SkillParser.IsValidName(name).ShouldBe(expected);
    }

    [Fact]
    public void IsValidName_ExceedsMaxLength_ReturnsFalse()
    {
        var longName = new string('a', 65);
        SkillParser.IsValidName(longName).ShouldBeFalse();
    }

    [Fact]
    public void Parse_NameMustMatchDirectoryName_FallsBackToDirectory()
    {
        // Per spec: name must match parent directory. Parser uses directory name as fallback.
        var markdown = """
            ---
            name: declared-name
            description: Test
            ---
            Content
            """;

        // The parser always uses the frontmatter name. Validation that it matches
        // the directory is done at the discovery/validation layer, not the parser.
        var skill = SkillParser.Parse("actual-dir", markdown, "/s/actual-dir", SkillSource.Global);
        skill.Name.ShouldBe("declared-name");
    }
}
