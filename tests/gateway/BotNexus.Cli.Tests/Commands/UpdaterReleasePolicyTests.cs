using System.Diagnostics;
using System.Text.Json;
using BotNexus.Cli.Commands;
using Xunit;

namespace BotNexus.Cli.Tests.Commands;

public sealed class UpdaterReleasePolicyTests
{
    private static string Root()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "updater-compatibility.json"))) return directory.FullName;
            directory = directory.Parent;
        }
        return Environment.GetEnvironmentVariable("BOTNEXUS_COMPATIBILITY_TEST_ROOT")
            ?? throw new InvalidOperationException("Cannot find repository root.");
    }

    [Theory]
    [InlineData("0.50.1", false)]
    [InlineData("0.50.2", true)]
    [InlineData("0.51.0", true)]
    public void ReleasePolicy_EnforcesFirstContractAwareRelease(string version, bool allowed)
    {
        var root = Root();
        var result = Run("pwsh", root, "-NoProfile", "-File", Path.Combine(root, "scripts", "repo", "Test-UpdaterCompatibility.ps1"), "-ReleaseVersion", version);
        (result.Code == 0).ShouldBe(allowed, result.Output);
    }

    [Fact]
    public void ReleasePolicy_SourceAndPackagedSupport_Agree()
    {
        var root = Root();
        var support = UpdaterCompatibility.Running with { PackageVersion = "0.50.2" };
        UpdaterCompatibility.Evaluate(support, File.ReadAllText(Path.Combine(root, UpdaterCompatibility.ContractPath))).ShouldBeNull();
        var workflow = File.ReadAllText(Path.Combine(root, ".github", "workflows", "release-cli.yml"));
        workflow.ShouldContain("Test-UpdaterCompatibility.ps1");
        workflow.IndexOf("Test-UpdaterCompatibility.ps1", StringComparison.Ordinal).ShouldBeLessThan(workflow.IndexOf("- name: Commit release artifacts", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("supportedUpdaterCapabilities", "future-required-v2")]
    [InlineData("requiredTargetCapabilities", "future-required-v2")]
    public void ReleasePolicy_InconsistentCliSupport_RejectsRelease(string property, string capability)
    {
        var root = Root();
        var temp = Path.Combine(Path.GetTempPath(), "botnexus-compat-policy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            var support = new Dictionary<string, object>
            {
                ["supportedContractSchemas"] = new[] { 1 },
                ["supportedUpdaterCapabilities"] = new[] { "planned-shutdown-v1", "readiness-v1", "immutable-target-preflight-v1" },
                ["requiredTargetCapabilities"] = new[] { "planned-shutdown-v1", "readiness-v1" }
            };
            support[property] = new[] { capability };
            var path = Path.Combine(temp, "support.json");
            File.WriteAllText(path, JsonSerializer.Serialize(support));
            var result = Run("pwsh", root, "-NoProfile", "-File", Path.Combine(root, "scripts", "repo", "Test-UpdaterCompatibility.ps1"), "-ReleaseVersion", "0.50.2", "-SupportPath", path);
            result.Code.ShouldNotBe(0, result.Output);
        }
        finally { Directory.Delete(temp, true); }
    }

    [Theory]
    [InlineData(false, "requiredUpdaterCapabilities", "\"planned-shutdown-v1\"")]
    [InlineData(false, "providedTargetCapabilities", "\"planned-shutdown-v1\"")]
    [InlineData(false, "requiredUpdaterCapabilities", "null")]
    [InlineData(false, "requiredUpdaterCapabilities", "[1]")]
    [InlineData(false, "schemaVersion", "null")]
    [InlineData(false, "schemaVersion", "\"1\"")]
    [InlineData(false, "schemaVersion", "true")]
    [InlineData(false, "minimumUpdaterVersion", "null")]
    [InlineData(false, "unknown", "true")]
    [InlineData(false, "supportedContractSchemas", "[1]")]
    [InlineData(false, "duplicate", "true")]
    [InlineData(true, "supportedUpdaterCapabilities", "\"planned-shutdown-v1\"")]
    [InlineData(true, "requiredTargetCapabilities", "\"planned-shutdown-v1\"")]
    [InlineData(true, "supportedContractSchemas", "1")]
    [InlineData(true, "supportedContractSchemas", "[\"1\"]")]
    [InlineData(true, "supportedContractSchemas", "[null]")]
    [InlineData(true, "supportedContractSchemas", "[1,1]")]
    [InlineData(true, "supportedUpdaterCapabilities", "null")]
    [InlineData(true, "unknown", "true")]
    [InlineData(true, "schemaVersion", "1")]
    [InlineData(true, "duplicate", "true")]
    public void ReleasePolicy_MalformedShape_RejectsRelease(bool support, string property, string value)
    {
        var root = Root();
        var original = File.ReadAllText(Path.Combine(root, support ? "src/gateway/BotNexus.Cli/updater-support.json" : UpdaterCompatibility.ContractPath));
        var json = System.Text.Json.Nodes.JsonNode.Parse(original)?.AsObject() ?? throw new InvalidOperationException("Missing fixture object.");
        if (property == "duplicate")
            original = original.TrimEnd()[..^1] + ",\"" + (support ? "supportedContractSchemas\":[1]" : "schemaVersion\":1") + "}";
        else
        {
            json[property] = System.Text.Json.Nodes.JsonNode.Parse(value);
            original = json.ToJsonString();
        }
        var descriptor = UpdaterCompatibility.Running with { PackageVersion = "0.50.2" };
        if (support)
            UpdaterCompatibility.ReadSupport(original, "0.50.2", "fixture").SupportedSchemas.ShouldBeEmpty();
        else
            UpdaterCompatibility.Evaluate(descriptor, original).ShouldNotBeNull();
        var temp = Path.Combine(Path.GetTempPath(), "botnexus-compat-shape-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            var path = Path.Combine(temp, "declaration.json");
            File.WriteAllText(path, original);
            var result = Run("pwsh", root, "-NoProfile", "-File", Path.Combine(root, "scripts", "repo", "Test-UpdaterCompatibility.ps1"), "-ReleaseVersion", "0.50.2", support ? "-SupportPath" : "-ContractPath", path);
            result.Code.ShouldNotBe(0, result.Output);
        }
        finally { Directory.Delete(temp, true); }
    }

    internal static (int Code, string Output) Run(string file, string root, params string[] args)
    {
        var info = file == "git" ? GitSandboxGuard.CreateSandboxedGit(root, string.Empty)
            : new ProcessStartInfo(file) { WorkingDirectory = root, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        if (file == "git")
        {
            info.Arguments = string.Empty;
            info.ArgumentList.Add("-C");
            info.ArgumentList.Add(root);
            info.ArgumentList.Add("-c");
            info.ArgumentList.Add("core.hooksPath=" + Path.Combine(root, "no-hooks"));
            info.Environment["GIT_CONFIG_GLOBAL"] = OperatingSystem.IsWindows() ? "NUL" : "/dev/null";
            info.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        }
        foreach (var arg in args) info.ArgumentList.Add(arg);
        using var process = Process.Start(info) ?? throw new InvalidOperationException("Could not start fixture process.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        return (process.ExitCode, output.GetAwaiter().GetResult() + error.GetAwaiter().GetResult());
    }
}
