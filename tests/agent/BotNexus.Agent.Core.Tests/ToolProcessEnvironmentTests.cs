using BotNexus.Agent.Core.Tools;
using Shouldly;

namespace BotNexus.Agent.Core.Tests;

/// <summary>
/// Proves the control in <see cref="ToolProcessEnvironment"/>: a tool subprocess is handed an
/// environment built from an allow-list, never the gateway's own.
/// </summary>
/// <remarks>
/// The parent environment is injected rather than read from the real process, so the sentinel
/// secret below is a value that provably CANNOT reach the child - which is how this is proven
/// rather than asserted about. Same approach the browser worker's tests use for
/// GHSA-m4m8-xjp4-5rmm.
/// </remarks>
public sealed class ToolProcessEnvironmentTests
{
    private const string SentinelSecretName = "SABNZBD_API_KEY";
    private const string SentinelSecretValue = "sk-do-not-leak-this";

    private static readonly Dictionary<string, string> Parent = new(StringComparer.Ordinal)
    {
        [SentinelSecretName] = SentinelSecretValue,
        ["ANTHROPIC_API_KEY"] = SentinelSecretValue,
        ["NAS_SHARE_PASSWORD"] = SentinelSecretValue,
        ["DOTNET_ROOT"] = "/home/agent/.dotnet",
        ["PATH"] = "/usr/bin",
        ["HOME"] = "/home/agent",
    };

    private static string? Read(string name) => Parent.GetValueOrDefault(name);

    [Fact]
    public void Build_DoesNotCopyACredentialOutOfTheParentEnvironment()
    {
        var child = ToolProcessEnvironment.Build(Read);

        child.ShouldNotContainKey(SentinelSecretName,
            "the child environment must be built from empty, not inherited.");
        child.ShouldNotContainKey("ANTHROPIC_API_KEY");
        child.ShouldNotContainKey("NAS_SHARE_PASSWORD");
        child.Values.ShouldNotContain(SentinelSecretValue);
    }

    [Fact]
    public void Build_StillAdmitsWhatACommandNeedsToRun()
    {
        var child = ToolProcessEnvironment.Build(Read);

        child["PATH"].ShouldBe("/usr/bin", "without PATH nothing resolves and every command fails.");
        child["HOME"].ShouldBe("/home/agent");
    }

    [Fact]
    public void Build_OmitsAllowedNamesThatTheParentDoesNotSet()
    {
        // An empty-valued entry is not the same as an absent one: exporting TMPDIR="" would make
        // some tools write to the filesystem root rather than fall back to their own default.
        var child = ToolProcessEnvironment.Build(_ => string.Empty);

        child.ShouldBeEmpty();
    }

    [Fact]
    public void AllowedVariables_CarryNoNameThatCouldAuthenticateToAnything()
    {
        // A guard on the LIST itself. Without it the sentinel test above keeps passing while
        // someone adds GITHUB_TOKEN to the allow-list for convenience.
        string[] forbiddenFragments =
            ["KEY", "TOKEN", "SECRET", "PASSWORD", "CREDENTIAL", "AUTH", "COOKIE", "SESSION"];

        foreach (var name in ToolProcessEnvironment.AllowedVariables)
        {
            foreach (var fragment in forbiddenFragments)
            {
                name.Contains(fragment, StringComparison.OrdinalIgnoreCase).ShouldBeFalse(
                    $"'{name}' looks like it could carry a credential and must not be allow-listed.");
            }
        }
    }

    [Fact]
    public void Build_LetsAnOperatorPassThroughANamedVariable()
    {
        var child = ToolProcessEnvironment.Build(Read, passThrough: ["DOTNET_ROOT"]);

        child["DOTNET_ROOT"].ShouldBe("/home/agent/.dotnet",
            "the escape hatch is what makes a strict default survivable for a real toolchain.");
        child.ShouldNotContainKey(SentinelSecretName,
            "opting one variable in must not reopen the rest.");
    }

    [Fact]
    public void Build_IgnoresBlankPassThroughNames()
    {
        var child = ToolProcessEnvironment.Build(Read, passThrough: ["", "   ", null!]);

        child.ShouldNotContainKey(string.Empty);
        child.Keys.ShouldAllBe(k => !string.IsNullOrWhiteSpace(k));
    }

    [Fact]
    public void ApplyTo_ClearsAnInheritedBlockBeforeSeedingIt()
    {
        // THE control, at the shape the spawn sites actually use: .NET hands them a dictionary
        // already populated from the parent process. Seeding without clearing would leave the
        // whole keyring in place and merely add the allow-list on top.
        var inherited = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [SentinelSecretName] = SentinelSecretValue,
            ["ANTHROPIC_API_KEY"] = SentinelSecretValue,
            ["PATH"] = "/inherited/bin",
        };

        ToolProcessEnvironment.ApplyTo(inherited, Read);

        inherited.ShouldNotContainKey(SentinelSecretName);
        inherited.ShouldNotContainKey("ANTHROPIC_API_KEY");
        inherited.Values.ShouldNotContain(SentinelSecretValue);
        inherited["PATH"].ShouldBe("/usr/bin", "the allow-listed value replaces the inherited one.");
    }
}
