using System.CommandLine;
using BotNexus.Cli;

namespace BotNexus.Cli.Tests;

/// <summary>
/// Guards the "did you mean" guidance for the noun-first/verb-first inversion
/// (issue #2197): a suggestion identical to the token is never shown, and a
/// verb-first mistake yields fully-qualified parent-scoped suggestions.
/// </summary>
public sealed class CommandSuggestionResolverTests
{
    [Fact]
    public void BuildQualifiedSuggestions_NeverEchoesTheUnmatchedToken()
    {
        var root = CliApp.CreateRootCommandForTesting();

        var suggestions = CommandSuggestionResolver.BuildQualifiedSuggestions(root, "list");

        // The core defect: the bare token must never appear as its own suggestion.
        suggestions.ShouldNotContain("list");
        suggestions.ShouldNotBeEmpty();
    }

    [Fact]
    public void BuildQualifiedSuggestions_ReturnsFullyQualifiedParentScopedForms()
    {
        var root = CliApp.CreateRootCommandForTesting();

        var suggestions = CommandSuggestionResolver.BuildQualifiedSuggestions(root, "list");

        // Every suggestion must be a qualified path (parent + verb), e.g. "agent list".
        suggestions.ShouldAllBe(s => s.Contains(' '));
        suggestions.ShouldContain("agent list");
    }

    [Fact]
    public void BuildQualifiedSuggestions_ReturnsEmptyForUnknownToken()
    {
        var root = CliApp.CreateRootCommandForTesting();

        var suggestions = CommandSuggestionResolver.BuildQualifiedSuggestions(root, "definitely-not-a-command");

        suggestions.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void BuildQualifiedSuggestions_ReturnsEmptyForBlankToken(string token)
    {
        var root = CliApp.CreateRootCommandForTesting();

        var suggestions = CommandSuggestionResolver.BuildQualifiedSuggestions(root, token);

        suggestions.ShouldBeEmpty();
    }

    [Fact]
    public void FormatMessage_QuotesTokenAndEverySuggestion()
    {
        var message = CommandSuggestionResolver.FormatMessage("list", new[] { "agent list", "cron list" });

        message.ShouldContain("'list'");
        message.ShouldContain("'agent list'");
        message.ShouldContain("'cron list'");
        // The message must not be self-referential: the suggestion list differs from the token.
        message.ShouldContain("Did you mean");
    }

    [Fact]
    public void TryBuildUnmatchedCommandGuidance_ProducesGuidanceForVerbFirstInversion()
    {
        var root = CliApp.CreateRootCommandForTesting();

        var handled = CliApp.TryBuildUnmatchedCommandGuidance(root, new[] { "list", "agents" }, out var guidance);

        handled.ShouldBeTrue();
        guidance.ShouldContain("agent list");
        // Never echo the bare token as the whole suggestion.
        guidance.ShouldNotBe("'list' is not a botnexus command. Did you mean: 'list'?");
    }

    [Fact]
    public void TryBuildUnmatchedCommandGuidance_IgnoresValidRootCommand()
    {
        var root = CliApp.CreateRootCommandForTesting();

        var handled = CliApp.TryBuildUnmatchedCommandGuidance(root, new[] { "agent", "list" }, out var guidance);

        handled.ShouldBeFalse();
        guidance.ShouldBeEmpty();
    }

    [Fact]
    public void TryBuildUnmatchedCommandGuidance_IgnoresValidRootAlias()
    {
        var root = CliApp.CreateRootCommandForTesting();

        // "agents" is an alias of the "agent" root command, so it is valid and untouched.
        var handled = CliApp.TryBuildUnmatchedCommandGuidance(root, new[] { "agents", "list" }, out _);

        handled.ShouldBeFalse();
    }

    [Fact]
    public void TryBuildUnmatchedCommandGuidance_IgnoresOptionOnlyInvocation()
    {
        var root = CliApp.CreateRootCommandForTesting();

        var handled = CliApp.TryBuildUnmatchedCommandGuidance(root, new[] { "--help" }, out _);

        handled.ShouldBeFalse();
    }

    [Fact]
    public void TryBuildUnmatchedCommandGuidance_IgnoresUnknownTokenWithNoSubcommandMatch()
    {
        var root = CliApp.CreateRootCommandForTesting();

        var handled = CliApp.TryBuildUnmatchedCommandGuidance(root, new[] { "frobnicate" }, out _);

        handled.ShouldBeFalse();
    }
}
