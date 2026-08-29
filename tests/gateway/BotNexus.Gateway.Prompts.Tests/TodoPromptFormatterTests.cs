using BotNexus.Gateway.Prompts;

namespace BotNexus.Gateway.Prompts.Tests;

public sealed class TodoPromptFormatterTests
{
    private const string SampleJson = """
        { "items": [ { "text": "do the thing", "status": "pending" } ] }
        """;

    /// <summary>
    /// #3661: the injected section must carry no per-turn item budget. "Advance ONE item per turn"
    /// was read as a stop condition and truncated multi-step turns; the anti-fabrication rule it was
    /// bundled with (#1463) must survive intact.
    /// </summary>
    [Fact]
    public void BuildSection_HasNoPerTurnBudget_ButKeepsToolResultGatedDone()
    {
        var joined = string.Join('\n', TodoPromptFormatter.BuildSection(SampleJson));

        joined.ShouldNotContain("ONE item per turn", Case.Insensitive);
        joined.ShouldNotContain("Advance ONE", Case.Insensitive);
        joined.ShouldNotContain("one item per turn", Case.Insensitive);

        joined.ShouldContain("only a tool result this turn may flip an item to [x] done", Case.Insensitive);
        joined.ShouldContain("narration cannot", Case.Insensitive);
    }

    /// <summary>
    /// #3661: the section must positively instruct same-turn continuation and dynamic revision, not
    /// merely omit the stop wording.
    /// </summary>
    [Fact]
    public void BuildSection_InstructsSameTurnContinuationAndListRevision()
    {
        var joined = string.Join('\n', TodoPromptFormatter.BuildSection(SampleJson));

        joined.ShouldContain("no per-turn item budget", Case.Insensitive);
        joined.ShouldContain("continue to the next one in the same turn", Case.Insensitive);
        joined.ShouldContain("add, split, reprioritize or cancel items", Case.Insensitive);
    }
    [Fact]
    public void BuildSection_NullOrBlank_ReturnsEmpty()
    {
        TodoPromptFormatter.BuildSection(null).ShouldBeEmpty();
        TodoPromptFormatter.BuildSection("").ShouldBeEmpty();
        TodoPromptFormatter.BuildSection("   ").ShouldBeEmpty();
    }

    [Fact]
    public void BuildSection_NoItems_ReturnsEmpty()
    {
        TodoPromptFormatter.BuildSection("""{ "items": [] }""").ShouldBeEmpty();
    }

    [Fact]
    public void BuildSection_MalformedJson_ReturnsEmpty()
    {
        TodoPromptFormatter.BuildSection("{ broken").ShouldBeEmpty();
        TodoPromptFormatter.BuildSection("[]").ShouldBeEmpty(); // not the expected object shape
        TodoPromptFormatter.BuildSection("""{ "items": "nope" }""").ShouldBeEmpty();
    }

    [Fact]
    public void BuildSection_RendersHeadingAndEachItemWithStatusBox()
    {
        var json = """
            { "items": [
              { "text": "design the thing", "status": "done" },
              { "text": "build the thing", "status": "in_progress" },
              { "text": "ship the thing", "status": "pending" },
              { "text": "abandoned idea", "status": "cancelled" }
            ] }
            """;

        var lines = TodoPromptFormatter.BuildSection(json);

        lines[0].ShouldBe(TodoPromptFormatter.SectionHeading);
        var joined = string.Join('\n', lines);
        joined.ShouldContain("[x] design the thing");
        joined.ShouldContain("[~] build the thing");
        joined.ShouldContain("[ ] ship the thing");
        joined.ShouldContain("[-] abandoned idea");
    }

    [Fact]
    public void BuildSection_MissingOrUnknownStatus_DefaultsToPendingBox()
    {
        var json = """
            { "items": [
              { "text": "no status field" },
              { "text": "weird status", "status": "frobnicated" }
            ] }
            """;

        var joined = string.Join('\n', TodoPromptFormatter.BuildSection(json));
        joined.ShouldContain("[ ] no status field");
        joined.ShouldContain("[ ] weird status");
    }

    [Fact]
    public void BuildSection_SkipsItemsWithBlankOrMissingText()
    {
        var json = """
            { "items": [
              { "text": "keep me" },
              { "text": "   " },
              { "status": "done" }
            ] }
            """;

        var lines = TodoPromptFormatter.BuildSection(json);
        // heading + 3 advisory lines + 1 real item = 5 lines (#3661 replaced the single advisory line)
        lines.Count.ShouldBe(5);
        string.Join('\n', lines).ShouldContain("keep me");
    }

    [Fact]
    public void BuildSection_IncludesAdvisoryLineAboutToolResults()
    {
        var lines = TodoPromptFormatter.BuildSection("""{ "items": [ { "text": "x" } ] }""");
        string.Join('\n', lines).ShouldContain("only a tool result this turn may flip an item to [x]");
    }
}
