using System.Reflection;
using System.Text.RegularExpressions;
using BotNexus.Gateway.Sessions;
using Shouldly;

namespace BotNexus.Architecture.Tests;

/// <summary>
/// Architecture fitness functions enforcing the Phase 3b invariant (#534):
/// the canonical "session history → LLM-visible entries" projection lives in
/// exactly one place — <see cref="SessionContextProjector"/> in the
/// <c>BotNexus.Gateway.Sessions</c> assembly — and is not re-invented inline
/// anywhere else in <c>src/gateway/</c>.
/// </summary>
public sealed class SessionContextProjectorArchitectureTests
{
    /// <summary>
    /// The projector must live in the <c>BotNexus.Gateway.Sessions</c> assembly so
    /// every isolation strategy and the compactor can depend on it without taking
    /// on a wider runtime dependency. A namespace-only check is insufficient because
    /// <c>BotNexus.Gateway.Sessions</c> is also used by types compiled into the
    /// <c>BotNexus.Gateway</c> assembly (e.g. <c>LlmSessionCompactor</c>).
    /// </summary>
    [Fact]
    public void SessionContextProjector_LivesInGatewaySessionsAssembly()
    {
        var assemblyName = typeof(SessionContextProjector).Assembly.GetName().Name;

        assemblyName.ShouldBe("BotNexus.Gateway.Sessions",
            "SessionContextProjector is the single source of truth for the LLM-visible " +
            "projection. It must live in BotNexus.Gateway.Sessions so isolation strategies " +
            "(in BotNexus.Gateway and future projects) and the compactor can share it.");
    }

    /// <summary>
    /// No file in <c>src/gateway/</c> outside the allowlist may contain a compound
    /// boolean expression that references both <c>IsHistory</c> and
    /// <c>IsCrashSentinel</c>. This is the canonical projection-filter pattern, and
    /// any new occurrence is almost certainly an inline re-implementation drifting
    /// from <see cref="SessionContextProjector"/>.
    /// </summary>
    /// <remarks>
    /// The scan is intentionally narrow: it looks for the substrings
    /// <c>IsHistory</c> and <c>IsCrashSentinel</c> appearing in the same file, which
    /// is the strongest grep-style signal of an inline projection that does not
    /// suffer the brittleness of matching a specific predicate formatting.
    /// </remarks>
    [Fact]
    public void NoInlineProjectionFilter_OutsideAllowlist()
    {
        var gatewayRoot = FindGatewaySourceRoot();
        var allowlist = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            // The canonical filter — the one and only place that combines IsHistory and
            // IsCrashSentinel in projection logic.
            "SessionContextProjector.cs",
            // Storage layer: persists both flags on the SessionEntry row. The projection
            // happens elsewhere; here we just round-trip the raw state.
            "SqliteSessionStore.cs",
            // Documentation comment only; the actual projection is delegated to the
            // projector via SessionCompaction.ApplyLegacyHistoryProjection.
            "FileSessionStore.cs",
        };

        var offenders = Directory
            .EnumerateFiles(gatewayRoot, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(path => !allowlist.Contains(Path.GetFileName(path)))
            .Where(path =>
            {
                var text = File.ReadAllText(path);
                return text.Contains("IsHistory", StringComparison.Ordinal)
                    && text.Contains("IsCrashSentinel", StringComparison.Ordinal);
            })
            .Select(path => Path.GetRelativePath(gatewayRoot, path))
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToArray();

        offenders.ShouldBeEmpty(
            "Files outside the SessionContextProjector allowlist contain both IsHistory " +
            "and IsCrashSentinel, indicating an inline re-implementation of the projection " +
            "filter. Route the call through SessionContextProjector instead.\n" +
            "Offenders:\n  " + string.Join("\n  ", offenders));
    }

    private static string FindGatewaySourceRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "BotNexus.slnx")))
        {
            current = current.Parent;
        }

        current.ShouldNotBeNull("Could not locate repo root from " + AppContext.BaseDirectory);
        var gatewayRoot = Path.Combine(current.FullName, "src", "gateway");
        Directory.Exists(gatewayRoot).ShouldBeTrue("Expected src/gateway under " + current.FullName);
        return gatewayRoot;
    }
}
