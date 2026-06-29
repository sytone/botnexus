using BotNexus.Agent.Core.Loop;

namespace BotNexus.Agent.Core.Tests.Loop;

/// <summary>
/// Unit tests for the leaked tool-call recovery parser (#1709). Verifies the parser extracts one
/// or more invoke/tool_use blocks from assistant text, builds tool calls with synthetic ids and
/// parsed arguments, returns the text with the XML removed, and leaves malformed XML / real prose
/// untouched without throwing.
/// </summary>
public class LeakedToolCallRecoveryTests
{
    [Fact]
    public void Single_invoke_with_one_arg_is_parsed()
    {
        const string text = "Listing.\n<invoke name=\"shell\"><parameter name=\"command\">gh issue list</parameter></invoke>";
        var result = LeakedToolCallRecovery.Recover(text);
        result.RecoveredCalls.Count.ShouldBe(1);
        result.RecoveredCalls[0].Name.ShouldBe("shell");
        result.RecoveredCalls[0].Arguments["command"].ShouldBe("gh issue list");
        result.CleanedText.ShouldNotContain("<invoke");
        result.CleanedText.ShouldContain("Listing.");
    }

    [Fact]
    public void Multiple_invokes_are_all_parsed()
    {
        const string text = "<invoke name=\"shell\"><parameter name=\"command\">a</parameter></invoke>"
            + "<invoke name=\"shell\"><parameter name=\"command\">b</parameter></invoke>";
        var result = LeakedToolCallRecovery.Recover(text);
        result.RecoveredCalls.Count.ShouldBe(2);
        result.RecoveredCalls[0].Arguments["command"].ShouldBe("a");
        result.RecoveredCalls[1].Arguments["command"].ShouldBe("b");
        result.RecoveredCalls[0].Id.ShouldNotBe(result.RecoveredCalls[1].Id);
    }

    [Fact]
    public void No_arg_invoke_parses_to_empty_arguments()
    {
        const string text = "<invoke name=\"shell\"></invoke>";
        var result = LeakedToolCallRecovery.Recover(text);
        result.RecoveredCalls.Count.ShouldBe(1);
        result.RecoveredCalls[0].Arguments.Count.ShouldBe(0);
    }

    [Fact]
    public void Tool_use_alias_is_recognised()
    {
        const string text = "<tool_use name=\"shell\"><parameter name=\"command\">x</parameter></tool_use>";
        var result = LeakedToolCallRecovery.Recover(text);
        result.RecoveredCalls.Count.ShouldBe(1);
        result.RecoveredCalls[0].Name.ShouldBe("shell");
    }

    [Fact]
    public void Malformed_unclosed_invoke_recovers_nothing()
    {
        const string text = "Working <invoke name=\"shell\"><parameter name=\"command\">oops";
        var result = LeakedToolCallRecovery.Recover(text);
        result.RecoveredCalls.ShouldBeEmpty();
    }

    [Fact]
    public void Real_prose_is_unchanged_and_recovers_nothing()
    {
        const string text = "I will not call any tool. The word invoke appears here as prose.";
        var result = LeakedToolCallRecovery.Recover(text);
        result.RecoveredCalls.ShouldBeEmpty();
        result.CleanedText.ShouldBe(text);
    }

    [Fact]
    public void Numeric_and_bool_args_are_coerced()
    {
        const string text = "<invoke name=\"shell\"><parameter name=\"n\">5</parameter><parameter name=\"f\">true</parameter></invoke>";
        var result = LeakedToolCallRecovery.Recover(text);
        result.RecoveredCalls.Count.ShouldBe(1);
        result.RecoveredCalls[0].Arguments["n"].ShouldBe(5L);
        result.RecoveredCalls[0].Arguments["f"].ShouldBe(true);
    }
}
