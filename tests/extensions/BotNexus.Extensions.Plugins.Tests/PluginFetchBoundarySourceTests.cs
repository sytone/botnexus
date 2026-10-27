using System.Runtime.CompilerServices;

namespace BotNexus.Extensions.Plugins.Tests;

/// <summary>Guards the lifecycle fetch boundary against reintroducing untrusted diagnostics.</summary>
public sealed class PluginFetchBoundarySourceTests
{
    [Fact]
    public void StageAsync_FetchFailure_UsesOnlyFixedSafeDiagnostics()
    {
        var source = ReadManager();
        var start = source.IndexOf("private async Task<StagedPlugin> StageAsync(", StringComparison.Ordinal);
        var end = source.IndexOf("private static List<string> Promote(", start, StringComparison.Ordinal);
        var boundary = source[start..end];
        boundary.ShouldNotContain("{source}");
        boundary.ShouldNotContain("ex.Message");
        boundary.ShouldNotContain("ex.ToString");
        boundary.ShouldNotContain("LogWarning");
        boundary.ShouldContain("GitPluginSourcePolicy.FetchFailureMessage");
        boundary.ShouldContain("GitPluginSourcePolicy.RejectedSourceMessage");
        boundary.ShouldContain("catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)");
        boundary.ShouldContain("throw new OperationCanceledException(cancellationToken)");
    }

    private static string ReadManager([CallerFilePath] string testFile = "")
    {
        var directory = new DirectoryInfo(Path.GetDirectoryName(testFile) ?? throw new InvalidOperationException("Test source directory unavailable."));
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "BotNexus.slnx")) && !Directory.Exists(Path.Combine(directory.FullName, ".git")) && !File.Exists(Path.Combine(directory.FullName, ".git")))
        {
            directory = directory.Parent;
        }
        var root = directory?.FullName ?? throw new InvalidOperationException("Repository root unavailable.");
        return File.ReadAllText(Path.Combine(root, "src", "extensions", "BotNexus.Extensions.Plugins", "Lifecycle", "PluginLifecycleManager.cs"));
    }
}
