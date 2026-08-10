using BotNexus.Agent.Core.Types;
using System.IO.Abstractions.TestingHelpers;

namespace BotNexus.Extensions.ExecTool.Tests;

/// <summary>
/// End-to-end coverage for issue #2892 at the <c>exec</c> spawn seam. The tool merged caller
/// <c>env</c> entries straight into <c>ProcessStartInfo.Environment</c>, so on Windows - where the
/// real environment block is case-insensitive - an override differing only in casing from an
/// inherited variable did not replace it. These tests observe what the CHILD actually sees, so they
/// fail if the site is changed back to a direct <c>startInfo.Environment[key] = value</c> write.
/// </summary>
[Collection(ExecToolBackgroundRegistryCollection.Name)]
public class ExecToolEnvKeyCasingTests : IDisposable
{
    private const string InheritedName = "BN2892_CASING_PROBE";
    private const string OverrideName = "bn2892_casing_probe";

    private readonly ExecTool _tool = new(workingDirectory: null, fileSystem: new MockFileSystem());

    public ExecToolEnvKeyCasingTests() =>
        Environment.SetEnvironmentVariable(InheritedName, "inherited");

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(InheritedName, null);
        ExecTool.ClearBackgroundProcesses();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// AC2/AC3. The child prints every environment entry; the assertion is on the entries the
    /// child genuinely received, not on an internal dictionary.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_DifferentlyCasedEnvOverride_ChildSeesPlatformCorrectEntries()
    {
        var result = await _tool.ExecuteAsync("casing", BuildArgs(), CancellationToken.None);

        var text = Flatten(result.Content);
        var entries = ParseProbeEntries(text);

        if (OperatingSystem.IsWindows())
        {
            // Windows: one logical variable, and it carries the caller's override value.
            entries.Count.ShouldBe(1,
                "On Windows the child environment block is case-insensitive, so a differently-cased " +
                $"override must replace the inherited entry, not coexist with it. Got: {text}");
            entries.Values.Single().ShouldBe("overridden");
        }
        else
        {
            // POSIX: the two spellings are distinct variables and both must survive.
            entries.Count.ShouldBe(2,
                $"POSIX environments are case-sensitive; both spellings must survive. Got: {text}");
            entries[InheritedName].ShouldBe("inherited");
            entries[OverrideName].ShouldBe("overridden");
        }
    }

    /// <summary>
    /// Guards against a "fix" that drops inherited variables wholesale: an unrelated inherited
    /// variable must still reach the child.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_EnvOverride_DoesNotDiscardUnrelatedInheritedVariables()
    {
        const string unrelated = "BN2892_UNRELATED_PROBE";
        Environment.SetEnvironmentVariable(unrelated, "kept");
        try
        {
            var result = await _tool.ExecuteAsync("casing", BuildArgs(), CancellationToken.None);

            Flatten(result.Content).ShouldContain("kept");
        }
        finally
        {
            Environment.SetEnvironmentVariable(unrelated, null);
        }
    }

    /// <summary>
    /// Extracts the probe variables from the child's dumped environment. Names are compared
    /// ordinally so a Windows-side duplicate (<c>BN2892...</c> plus <c>bn2892...</c>) is visible
    /// rather than collapsed by the assertion's own dictionary.
    /// </summary>
    private static string Flatten(IReadOnlyList<AgentToolContent> content) =>
        string.Join("\n", content.Select(c => c.Value));

    private static Dictionary<string, string> ParseProbeEntries(string output)
    {
        var entries = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var line in output.Split('\n'))
        {
            var trimmed = line.Trim();
            var separator = trimmed.IndexOf('=');
            if (separator <= 0)
                continue;

            var name = trimmed[..separator];
            if (!name.StartsWith("BN2892_CASING_PROBE", StringComparison.OrdinalIgnoreCase))
                continue;

            entries[name] = trimmed[(separator + 1)..];
        }

        return entries;
    }

    private static IReadOnlyDictionary<string, object?> BuildArgs()
    {
        string[] command = OperatingSystem.IsWindows()
            ? ["cmd.exe", "/c", "set"]
            : ["/bin/sh", "-c", "env"];

        return new Dictionary<string, object?>
        {
            ["command"] = (IReadOnlyList<string>)command.ToList(),
            ["timeoutMs"] = 60_000,
            ["noOutputTimeoutMs"] = null,
            ["input"] = null,
            ["background"] = false,
            ["env"] = (IReadOnlyDictionary<string, string>)new Dictionary<string, string>
            {
                [OverrideName] = "overridden",
            },
            ["workingDir"] = null,
        };
    }
}
