using BotNexus.Gateway.Prompts;

namespace BotNexus.Gateway.Prompts.Tests;

public sealed class TodoPromptFormatterTests
{
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
        // heading + advisory line + 1 real item = 3 lines
        lines.Count.ShouldBe(3);
        string.Join('\n', lines).ShouldContain("keep me");
    }

    [Fact]
    public void BuildSection_IncludesAdvisoryLineAboutToolResults()
    {
        var lines = TodoPromptFormatter.BuildSection("""{ "items": [ { "text": "x" } ] }""");
        string.Join('\n', lines).ShouldContain("only a tool result this turn may flip an item to [x]");
    }
}
