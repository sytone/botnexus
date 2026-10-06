using System.Text.RegularExpressions;
using Shouldly;

namespace BotNexus.Architecture.Tests;

/// <summary>Fences issue #4668: startup recovery and cleanup use bounded projections.</summary>
public sealed class StartupSessionScanArchitectureTests : ArchitectureTest
{
    [Theory]
    [InlineData("src/gateway/BotNexus.Gateway/Sessions/InterruptedTurnNotificationService.cs", "ListUnresolvedCrashSentinelsAsync")]
    [InlineData("src/gateway/BotNexus.Gateway/SessionCleanupService.cs", "ListCleanupPlanAsync")]
    public void StartupMaintenance_DoesNotCall_FullSessionList(string relativePath, string expectedProjection)
    {
        var path = Repository.Path(relativePath.Split('/'));
        File.Exists(path).ShouldBeTrue($"non-vacuity: expected startup maintenance source {path}");

        var source = File.ReadAllText(path);
        source.Contains(expectedProjection, StringComparison.Ordinal).ShouldBeTrue(
            $"#4668: {Path.GetFileName(path)} must use the bounded {expectedProjection} projection");

        FullSessionListPattern.Matches(source).Count.ShouldBe(0,
            $"#4668: {Path.GetFileName(path)} must not call ISessionStore.ListAsync because it hydrates the transcript corpus");
    }

    private static readonly Regex FullSessionListPattern = new(
        @"\b_?(?:sessions|sessionStore)\s*\.\s*ListAsync\s*\(",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
}
