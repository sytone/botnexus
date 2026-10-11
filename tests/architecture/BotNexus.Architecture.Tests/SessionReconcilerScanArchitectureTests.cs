using System.Text.RegularExpressions;
using Shouldly;

namespace BotNexus.Architecture.Tests;

/// <summary>
/// Fences issue #4657: whole-store maintenance must never hydrate every persisted transcript.
/// </summary>
public sealed class SessionReconcilerScanArchitectureTests : ArchitectureTest
{
    [Fact]
    public void SessionConsistencyChecker_DoesNotHydrateIndividualSessions()
    {
        var path = Repository.Path(
            "src", "gateway", "BotNexus.Gateway", "Sessions", "SessionConsistencyChecker.cs");
        var source = File.ReadAllText(path);

        source.Contains("_sessions.GetAsync(", StringComparison.Ordinal).ShouldBeFalse(
            "#4657: the consistency pass must not hydrate one full transcript per conversation");
        source.Contains("_sessions.ListByConversationAsync(", StringComparison.Ordinal).ShouldBeFalse(
            "#4657: the consistency pass must reuse its transcript-free projection for cron-poison repair");
    }

    [Theory]
    [InlineData("src/gateway/BotNexus.Gateway/Sessions/SessionConsistencyChecker.cs")]
    [InlineData("src/gateway/BotNexus.Memory/MemorySessionReconciler.cs")]
    [InlineData("src/gateway/BotNexus.Gateway.Api/Triggers/CronSessionStartupReconciler.cs")]
    public void WholeStoreMaintenance_DoesNotCall_SessionStoreListAsync(string relativePath)
    {
        var path = Repository.Path(relativePath.Split('/'));
        File.Exists(path).ShouldBeTrue($"non-vacuity: expected maintenance source {path}");

        var source = File.ReadAllText(path);
        source.Contains("ListSummariesAsync", StringComparison.Ordinal).ShouldBeTrue(
            "whole-store maintenance must use the transcript-free session projection");

        var calls = FullSessionListPattern.Matches(source);
        calls.Count.ShouldBe(0,
            $"#4657: {Path.GetFileName(path)} must not call ISessionStore.ListAsync because it hydrates every transcript. " +
            "Use ListSummariesAsync. Offending text: " + string.Join(", ", calls.Select(match => match.Value)));
    }

    private static readonly Regex FullSessionListPattern = new(
        @"\b_?sessions\s*\.\s*ListAsync\s*\(|\bsessionStore\s*\.\s*ListAsync\s*\(",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
}
