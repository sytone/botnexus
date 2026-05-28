using System.Net.Sockets;
using System.Text.RegularExpressions;
using Shouldly;
using Xunit;

namespace BotNexus.Integration.Cli.Tests;

/// <summary>
/// Drives the installed CLI through the GitHub Copilot provider setup flow far enough
/// to verify the device-code prompt is emitted, then aborts the process. We do not
/// complete OAuth — the test only validates that running
/// <c>botnexus provider setup --provider github-copilot --target &lt;tmp&gt;</c>
/// reaches the auth handoff and surfaces the verification URL + user code to stdout.
///
/// Hits the real <c>https://github.com/login/device/code</c> endpoint (no auth required;
/// codes expire on their own). Skipped automatically when the test host has no network
/// access to github.com.
///
/// The non-interactive <c>--provider</c> flag is the seam that makes this test possible
/// — it lets us bypass the interactive provider-selection Spectre.Console prompt that
/// would otherwise require a TTY.
/// </summary>
[Collection(LocalCliCollection.Name)]
public sealed class LocalCliCopilotSetupTests : IAsyncLifetime
{
    private static readonly TimeSpan SetupTimeout = TimeSpan.FromSeconds(45);
    private static readonly Regex UserCodePattern = new("[A-Z0-9]{4}-[A-Z0-9]{4}", RegexOptions.Compiled);

    private readonly LocalCliInstallFixture _fixture;
    private string _home = string.Empty;

    public LocalCliCopilotSetupTests(LocalCliInstallFixture fixture) => _fixture = fixture;

    public Task InitializeAsync()
    {
        _home = Path.Combine(Path.GetTempPath(), "botnexus-local-cli-home", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_home);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        try
        {
            if (Directory.Exists(_home))
                Directory.Delete(_home, recursive: true);
        }
        catch
        {
            // best-effort cleanup
        }
        return Task.CompletedTask;
    }

    [SkippableFact]
    public async Task ProviderSetup_GithubCopilot_EmitsDeviceCodePrompt_ThenAborts()
    {
        AssertFixture();
        Skip.IfNot(await CanReachGitHubAsync(), "GitHub device-code endpoint unreachable from this host.");

        var watch = await ProcessRunner.RunUntilOutputMatchAsync(
            _fixture.CliExecutablePath,
            $"provider setup --provider github-copilot --target \"{_home}\"",
            matchPredicate: output =>
                output.Contains("github.com/login/device", StringComparison.OrdinalIgnoreCase)
                && UserCodePattern.IsMatch(output),
            timeout: SetupTimeout,
            environment: new Dictionary<string, string?> { ["BOTNEXUS_HOME"] = null });

        watch.Matched.ShouldBeTrue(
            $"Did not see GitHub device-code prompt within {SetupTimeout}.\n" +
            $"Output:\n{watch.Output}");

        // Sanity: extract the code and verify the verification URI is well-formed.
        var match = UserCodePattern.Match(watch.Output);
        match.Success.ShouldBeTrue();
        watch.Output.ShouldContain("github.com/login/device");

        // We aborted before OAuth could complete, so no auth.json should have been written.
        var authPath = Path.Combine(_home, "auth.json");
        File.Exists(authPath).ShouldBeFalse(
            "auth.json must not be written when the user has not completed device authorization.");
    }

    [Fact]
    public async Task ProviderSetup_UnknownProvider_FailsWithGuidance()
    {
        AssertFixture();

        var result = await ProcessRunner.RunAsync(
            _fixture.CliExecutablePath,
            $"provider setup --provider does-not-exist --target \"{_home}\"",
            environment: new Dictionary<string, string?> { ["BOTNEXUS_HOME"] = null },
            timeout: TimeSpan.FromSeconds(30));

        result.ExitCode.ShouldNotBe(0,
            $"Expected non-zero exit code for unknown provider.\nStdOut:\n{result.StdOut}\nStdErr:\n{result.StdErr}");
        result.Combined.ShouldContain("Unknown provider",
            customMessage: $"Expected guidance message.\nStdOut:\n{result.StdOut}\nStdErr:\n{result.StdErr}");
        result.Combined.ShouldContain("provider add",
            customMessage: "Error message should point the user at `provider add` for non-known providers.");
    }

    private static async Task<bool> CanReachGitHubAsync()
    {
        try
        {
            using var client = new TcpClient();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await client.ConnectAsync("github.com", 443, cts.Token);
            return client.Connected;
        }
        catch
        {
            return false;
        }
    }

    private void AssertFixture()
    {
        _fixture.Succeeded.ShouldBeTrue(
            $"Local pack/install fixture did not succeed.\n" +
            $"PackExitCode={_fixture.PackExitCode}\nInstallExitCode={_fixture.InstallExitCode}\n" +
            $"PackOutput:\n{_fixture.PackOutput}\n\nInstallOutput:\n{_fixture.InstallOutput}\n\nError:\n{_fixture.Error}");
    }
}
