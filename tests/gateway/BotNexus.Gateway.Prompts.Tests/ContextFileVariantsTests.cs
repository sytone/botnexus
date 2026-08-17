using BotNexus.Gateway.Prompts;

namespace BotNexus.Gateway.Prompts.Tests;

/// <summary>
/// Covers the model-specific instruction-file suffix grammar and its specificity ladder (#2435).
/// </summary>
/// <remarks>
/// The sad-path block is the load-bearing half. A suffix the grammar rejects must resolve to the
/// BASE file: throwing would take prompt assembly down over a stray filename, and silently loading
/// the malformed file would hand the model instructions written for a different one.
/// </remarks>
public sealed class ContextFileVariantsTests
{
    [Theory]
    [InlineData("AGENTS.gpt.md", "AGENTS.md", "gpt")]
    [InlineData("AGENTS.gpt-5.md", "AGENTS.md", "gpt-5")]
    [InlineData("AGENTS.gpt-5-6.md", "AGENTS.md", "gpt-5-6")]
    [InlineData("SOUL.claude-opus.md", "SOUL.md", "claude-opus")]
    [InlineData("WORLD.claude-opus-4-8.md", "WORLD.md", "claude-opus-4-8")]
    public void TryParse_AcceptsGrammaticalSuffixes(string fileName, string expectedBase, string expectedSuffix)
    {
        Assert.True(ContextFileVariants.TryParse(fileName, out var suffix));
        Assert.NotNull(suffix);
        Assert.Equal(expectedBase, suffix!.BaseFileName);
        Assert.Equal(expectedSuffix, suffix.Suffix);
    }

    [Theory]
    [InlineData("AGENTS.md")]                 // no suffix at all
    [InlineData("AGENTS.GPT.md")]             // uppercase violates the lowercase rule
    [InlineData("AGENTS.gpt--5.md")]          // doubled separator
    [InlineData("AGENTS.-gpt.md")]            // leading separator
    [InlineData("AGENTS.gpt-.md")]            // trailing separator
    [InlineData("AGENTS.gpt.5.md")]           // '.' used as anything but a segment delimiter
    [InlineData("AGENTS.gpt_5.md")]           // '_' is not the separator
    [InlineData("AGENTS.5-gpt.md")]           // version before family: no such rung
    [InlineData("AGENTS.5.md")]               // version with no family token
    [InlineData("AGENTS.gpt-5-6-7.md")]       // three version components
    [InlineData(".gpt.md")]                   // empty stem
    public void TryParse_RejectsUngrammaticalSuffixes(string fileName)
    {
        Assert.False(ContextFileVariants.TryParse(fileName, out var suffix));
        Assert.Null(suffix);
    }

    [Theory]
    [InlineData("AGENTS.GPT.md")]
    [InlineData("AGENTS.gpt--5.md")]
    [InlineData("AGENTS.mistral.md")]
    public void Resolve_FallsBackToBaseFile_ForMalformedOrUnknownSuffix(string variant)
    {
        var resolved = ContextFileVariants.Resolve([variant, "AGENTS.md"], "AGENTS.md", "gpt-5.6");

        Assert.Equal("AGENTS.md", resolved);
    }

    [Fact]
    public void GetBaseFileName_ReturnsNameUnchanged_WhenSuffixIsMalformed()
    {
        // A rejected suffix is an ORDINARY file, not a broken variant: its own name is its base.
        Assert.Equal("AGENTS.GPT.md", ContextFileVariants.GetBaseFileName("AGENTS.GPT.md"));
        Assert.Equal("AGENTS.md", ContextFileVariants.GetBaseFileName("AGENTS.gpt-5.md"));
    }

    [Fact]
    public void Resolve_PicksMostSpecificVariant_ForGpt()
    {
        string[] candidates =
            ["AGENTS.md", "AGENTS.gpt.md", "AGENTS.gpt-5.md", "AGENTS.gpt-5-6.md", "AGENTS.claude-opus.md"];

        Assert.Equal("AGENTS.gpt-5-6.md", ContextFileVariants.Resolve(candidates, "AGENTS.md", "gpt-5.6"));
    }

    [Fact]
    public void Resolve_StopsAtTheRungThatMatches_WhenMinorDiffers()
    {
        // gpt-5.2 must NOT take the gpt-5-6 rung; family+major is the most specific true statement.
        string[] candidates = ["AGENTS.md", "AGENTS.gpt.md", "AGENTS.gpt-5.md", "AGENTS.gpt-5-6.md"];

        Assert.Equal("AGENTS.gpt-5.md", ContextFileVariants.Resolve(candidates, "AGENTS.md", "gpt-5.2"));
    }

    [Fact]
    public void Resolve_StopsAtFamily_WhenNoVersionRungMatches()
    {
        string[] candidates = ["AGENTS.md", "AGENTS.gpt.md", "AGENTS.gpt-4.md"];

        Assert.Equal("AGENTS.gpt.md", ContextFileVariants.Resolve(candidates, "AGENTS.md", "gpt-5.6"));
    }

    [Fact]
    public void Resolve_PrefersFamilyPlusModel_OverFamilyAlone()
    {
        string[] candidates = ["AGENTS.md", "AGENTS.claude.md", "AGENTS.claude-opus.md"];

        Assert.Equal("AGENTS.claude-opus.md", ContextFileVariants.Resolve(candidates, "AGENTS.md", "claude-opus-4-8"));
    }

    [Fact]
    public void Resolve_MatchesFamilyPlusModelPlusVersion()
    {
        string[] candidates = ["AGENTS.md", "AGENTS.claude-opus.md", "AGENTS.claude-opus-4-8.md"];

        Assert.Equal("AGENTS.claude-opus-4-8.md", ContextFileVariants.Resolve(candidates, "AGENTS.md", "claude-opus-4-8"));
    }

    [Fact]
    public void Resolve_IgnoresVariantsOfADifferentBaseFile()
    {
        // A SOUL variant must not be offered up as the AGENTS file just because it matches the
        // model. Base-name equality is checked before specificity, never after.
        string[] candidates = ["AGENTS.md", "SOUL.gpt.md", "SOUL.md"];

        Assert.Equal("AGENTS.md", ContextFileVariants.Resolve(candidates, "AGENTS.md", "gpt-5.6"));
        // Control: the same candidate set DOES resolve the SOUL variant for the SOUL base, so the
        // assertion above is about base-name scoping rather than the variant simply never matching.
        Assert.Equal("SOUL.gpt.md", ContextFileVariants.Resolve(candidates, "SOUL.md", "gpt-5.6"));
    }

    [Fact]
    public void Resolve_DoesNotMatchAcrossFamilies()
    {
        // The Claude conversation must not read the GPT file just because it is the only variant.
        Assert.Equal("AGENTS.md", ContextFileVariants.Resolve(["AGENTS.md", "AGENTS.gpt.md"], "AGENTS.md", "claude-opus-5"));
    }

    [Fact]
    public void Resolve_MatchesFamilyDetectedFromProvider_WhenModelIdIsAVanityName()
    {
        // #3104's vanity-id case: the id carries no family substring, the provider serves one family.
        Assert.Equal(
            "AGENTS.copilot.md",
            ContextFileVariants.Resolve(["AGENTS.md", "AGENTS.copilot.md"], "AGENTS.md", "house-blend-v2", "github-copilot"));
    }

    [Fact]
    public void Resolve_FallsBackToBase_WhenNoModelIsKnown()
    {
        Assert.Equal("AGENTS.md", ContextFileVariants.Resolve(["AGENTS.md", "AGENTS.gpt.md"], "AGENTS.md", null));
    }

    [Fact]
    public void Resolve_DoesNotMatchFamilyTokenInsideAnUnrelatedWord()
    {
        // "opus" inside "octopus" is not an opus -- the boundary rule ModelFamilyVersion enforces.
        Assert.Equal("AGENTS.md", ContextFileVariants.Resolve(["AGENTS.md", "AGENTS.opus.md"], "AGENTS.md", "octopus-3"));
    }

    [Fact]
    public void Resolve_TreatsADateStampAsNoVersion()
    {
        // claude-opus-4-20250514: the 20250514 is a date stamp, so the model is 4.0 and must not
        // satisfy a -4-8 variant. Guarding the ModelFamilyVersion date-stamp cap through this seam.
        Assert.Equal(
            "AGENTS.claude-opus-4.md",
            ContextFileVariants.Resolve(
                ["AGENTS.md", "AGENTS.claude-opus-4.md", "AGENTS.claude-opus-4-8.md"],
                "AGENTS.md",
                "claude-opus-4-20250514"));
    }

    [Fact]
    public void Resolve_IsDeterministic_ForEquallySpecificVariants()
    {
        // Two same-score suffixes must not resolve by directory-enumeration order.
        string[] forward = ["AGENTS.md", "AGENTS.claude.md", "AGENTS.opus.md"];
        string[] reversed = ["AGENTS.opus.md", "AGENTS.claude.md", "AGENTS.md"];

        var first = ContextFileVariants.Resolve(forward, "AGENTS.md", "claude-opus-5");
        var second = ContextFileVariants.Resolve(reversed, "AGENTS.md", "claude-opus-5");

        Assert.Equal(first, second);
    }

    [Fact]
    public void Grammar_IsIdenticalToThePromptVariantAttributeGrammar()
    {
        // The whole point of #2435's strictness: a family spelled one way in a [PromptVariant]
        // attribute and another way on disk must not resolve differently. This asserts the two
        // grammars are the SAME pattern rather than merely similar ones.
        var registryGrammar = typeof(PromptVariantRegistry)
            .GetField("TokenGrammar", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
            ?.GetValue(null) as System.Text.RegularExpressions.Regex;

        Assert.NotNull(registryGrammar);
        Assert.Equal(registryGrammar!.ToString(), ContextFileVariants.GrammarPattern);
    }

    [Theory]
    [InlineData("agents.gpt.md", "agents.md")]
    [InlineData("agents.claude-opus-4-8.md", "agents.md")]
    [InlineData("world.gpt.md", "world.md")]
    [InlineData("memory.claude.md", "memory.md")]
    public void SortForPrompt_PlacesVariantsAtTheirBaseFilesPosition(string variantName, string baseName)
    {
        // AC2. Before #2435 a variant missed the basename lookup, fell through to int.MaxValue and
        // sorted AFTER memory.md -- so picking a variant silently reordered the instruction stack.
        // A trailing unrecognised file is present so "sorted to the end" remains a DISTINGUISHABLE
        // outcome: without it, last position and correct position could coincide for memory.md.
        var files = new List<ContextFile>
        {
            new("memory.md", "m"),
            new(variantName, "v"),
            new("world.md", "w"),
            new("agents.md", "a"),
            new("soul.md", "s"),
            new("zz-unranked.md", "z")
        };

        var sorted = ContextFileOrdering.SortForPrompt(files).Select(file => file.Path).ToList();

        // The variant sits immediately beside its base file -- the two share an order value and are
        // then separated only by the ordinal basename tie-break.
        Assert.Equal(1, Math.Abs(sorted.IndexOf(variantName) - sorted.IndexOf(baseName)));
        Assert.NotEqual(sorted.Count - 1, sorted.IndexOf(variantName));
        Assert.Equal(sorted.Count - 1, sorted.IndexOf("zz-unranked.md"));
    }

    [Fact]
    public void SortForPrompt_LeavesAnUngrammaticalVariantAtTheEnd()
    {
        // AGENTS.GPT.md is not a variant, so it must NOT inherit agents.md's position -- it is an
        // unrecognised file and sorts with the other unrecognised files.
        var files = new List<ContextFile>
        {
            new("agents.gpt.md", "v"),
            new("agents.GPT.md", "x"),
            new("memory.md", "m"),
            new("agents.md", "a")
        };

        var sorted = ContextFileOrdering.SortForPrompt(files).Select(file => file.Path).ToList();

        Assert.True(sorted.IndexOf("agents.gpt.md") < sorted.IndexOf("memory.md"));
        Assert.True(sorted.IndexOf("agents.GPT.md") > sorted.IndexOf("memory.md"));
    }
}
