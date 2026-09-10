using System.CommandLine;
using BotNexus.Cli;
using BotNexus.Cli.Commands;
using BotNexus.Cli.Services;
using NSubstitute;

namespace BotNexus.Cli.Tests.Commands;

/// <summary>
/// Pins that <c>gateway stop</c> and <c>gateway restart</c> hand the manager a binary path.
///
/// <para>
/// These live at the COMMAND boundary on purpose. <c>GatewayStopDiscoveryTests</c> already covers
/// the manager thoroughly — and could not have caught this defect, because every one of its cases
/// passes the path in by hand while the real call sites passed nothing. The manager was correct
/// throughout; the caller dropped an optional argument, `FindProcessByBinaryPath(null)` returned on
/// its first line, and a live gateway was reported "not running" with a tick and exit code 0.
/// </para>
/// <para>
/// A test that supplies the input the call site forgot cannot see a call site that forgets it.
/// That is the shape these assert against.
/// </para>
/// </summary>
public sealed class GatewayStopWiringTests : IDisposable
{
    private readonly string _home;
    private readonly IGatewayProcessManager _manager = Substitute.For<IGatewayProcessManager>();

    public GatewayStopWiringTests()
    {
        _home = Path.Combine(Path.GetTempPath(), $"bn-stopwiring-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_home);

        _manager
            .StopAsync(Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new GatewayStopResult(true, "Gateway is not running", GatewayStopOutcome.NotRunning));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_home))
                Directory.Delete(_home, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private RootCommand BuildRoot()
    {
        var verboseOption = new Option<bool>("--verbose", "Show additional command output.");
        var targetOption = new Option<string?>("--target", () => null, "BotNexus home directory.");
        var root = new RootCommand("test");
        root.AddGlobalOption(verboseOption);
        root.AddGlobalOption(targetOption);
        root.AddCommand(new GatewayCommand(_manager).Build(verboseOption, targetOption));
        return root;
    }

    private static string? CapturedBinaryPath(IGatewayProcessManager manager)
    {
        var call = manager.ReceivedCalls()
            .Single(c => c.GetMethodInfo().Name == nameof(IGatewayProcessManager.StopAsync));
        return call.GetArguments()[1] as string;
    }

    [Fact]
    public async Task Stop_HandsTheManagerABinaryPath_SoAGatewayWithNoPidFileCanBeFound()
    {
        await BuildRoot().InvokeAsync(["gateway", "stop", "--target", _home]);

        var binaryPath = CapturedBinaryPath(_manager);

        binaryPath.ShouldNotBeNullOrWhiteSpace(
            "without a path the manager's discovery fallback returns on its first line, and a " +
            "gateway started outside the CLI is reported 'not running' while alive");
        Path.GetFileName(binaryPath).ShouldBe("BotNexus.Gateway.Api.dll");
    }

    [Fact]
    public async Task Stop_ResolvesTheBinaryFromExplicitSource_SoAGatewayFromAnotherTreeIsReachable()
    {
        var otherTree = Path.Combine(_home, "some-other-worktree");

        await BuildRoot().InvokeAsync(["gateway", "stop", "--target", _home, "--source", otherTree]);

        // Path identity is how discovery matches, so a gateway serving a different tree is only
        // stoppable when the caller can say which tree.
        CapturedBinaryPath(_manager).ShouldBe(CliPaths.GatewayBinary(otherTree));
    }

    [Fact]
    public void Stop_OffersASourceOption()
    {
        var stop = BuildRoot().Subcommands
            .Single(c => c.Name == "gateway").Subcommands
            .Single(c => c.Name == "stop");

        stop.Options.Select(o => o.Name).ShouldContain("source");
    }

    /// <summary>
    /// Covers <c>restart</c> — and every future call site — without driving a restart.
    ///
    /// <para>
    /// Invoking <c>gateway restart</c> for real would run the start path with <c>skipBuild: false</c>
    /// and shell out to a solution build, which is the wrong tool for asserting an argument. The
    /// defect was never subtle behaviour; it was a dropped optional parameter, and that is exactly
    /// what source can be asked about directly.
    /// </para>
    /// <para>
    /// Restart matters most of the three: one that believes nothing was running goes on to redeploy
    /// extensions a live gateway still holds mapped, which is the
    /// <c>&lt;ext&gt;.pdb ... used by another process</c> failure and a half-updated deployment.
    /// </para>
    /// </summary>
    [Fact]
    public void NoStopCallSiteInGatewayCommand_OmitsTheBinaryPath()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepoRoot(), "src", "gateway", "BotNexus.Cli", "Commands", "GatewayCommand.cs"));

        var callSites = source
            .Split('\n')
            .Select((line, index) => (line: line.Trim(), number: index + 1))
            .Where(x => x.line.Contains("_processManager.StopAsync("))
            .ToList();

        callSites.ShouldNotBeEmpty("the fence is vacuous if it matches nothing");

        var offenders = callSites
            .Where(x => x.line.Contains("cancellationToken: cancellationToken"))
            .Select(x => $"line {x.number}: {x.line}")
            .ToList();

        offenders.ShouldBeEmpty(
            "a StopAsync call that names cancellationToken is skipping gatewayBinaryPath, which " +
            "silently disables discovery for any gateway without a PID file");
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Directory.Packages.props")))
            dir = dir.Parent;

        dir.ShouldNotBeNull("could not locate the repository root from the test output directory");
        return dir!.FullName;
    }
}
