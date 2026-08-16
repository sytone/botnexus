using System.IO.Compression;

namespace BotNexus.Integration.Cli.Tests;

/// <summary>
/// Non-vacuity coverage for the #3237 pack/install layout guard (AC5).
///
/// The guard's whole value is that it FIRES when a startup-critical assembly is absent. A guard
/// that only ever runs against a healthy layout is indistinguishable from no guard at all, and
/// the real layout is healthy on almost every run — the defect it protects against is
/// intermittent. So these tests synthesize the layout directly, including the deliberately
/// broken case where <c>BotNexus.Agent.Providers.Core.dll</c> has been removed, and assert that
/// the step fails by name rather than deferring to a later assembly-load failure.
///
/// These cases invoke no processes and touch no packed CLI, so they are cheap and deterministic.
/// </summary>
public sealed class CliInstallLayoutTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "botnexus-layout-tests", Guid.NewGuid().ToString("N"));

    public CliInstallLayoutTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // best-effort cleanup
        }
    }

    /// <summary>
    /// Writes a tool-path layout mirroring the shape `dotnet tool install --tool-path` produces:
    /// a launcher at the root and the payload assemblies nested under .store/.
    /// </summary>
    private string CreateLayout(IEnumerable<string> assemblyFileNames)
    {
        var toolPath = Path.Combine(_root, "tool");
        var payload = Path.Combine(toolPath, ".store", "botnexus.cli", "99.99.99-local-deadbeef", "tools", "net10.0", "any");
        Directory.CreateDirectory(payload);
        File.WriteAllText(Path.Combine(toolPath, "botnexus"), "launcher");
        foreach (var name in assemblyFileNames)
            File.WriteAllText(Path.Combine(payload, name), "assembly");
        return toolPath;
    }

    [Fact]
    public void FindMissingAssemblies_CompleteLayout_ReportsNothingMissing()
    {
        var toolPath = CreateLayout(CliInstallLayout.RequiredAssemblies);

        var files = CliInstallLayout.EnumerateFiles(toolPath);
        CliInstallLayout.FindMissingAssemblies(files).ShouldBeEmpty();
    }

    /// <summary>
    /// The #3237 scenario exactly: everything the CLI needs is installed EXCEPT the provider
    /// assembly. Today that layout yields a binary that starts and dies later; the guard must
    /// name the assembly here instead.
    /// </summary>
    [Fact]
    public void FindMissingAssemblies_ProviderCoreAbsent_NamesTheProviderAssembly()
    {
        var toolPath = CreateLayout(
            CliInstallLayout.RequiredAssemblies.Where(a => a != "BotNexus.Agent.Providers.Core.dll"));

        var files = CliInstallLayout.EnumerateFiles(toolPath);
        var missing = CliInstallLayout.FindMissingAssemblies(files);

        missing.ShouldBe(["BotNexus.Agent.Providers.Core.dll"]);

        var message = CliInstallLayout.FormatMissingAssemblyFailure(toolPath, missing, files);
        message.ShouldContain("BotNexus.Agent.Providers.Core.dll");
        message.ShouldContain(toolPath);
        // The listing must be present, otherwise a single failing run is not diagnostic.
        message.ShouldContain("BotNexus.Cli.dll");
        message.ShouldContain("Install layout:");
    }

    [Fact]
    public void FindMissingAssemblies_EmptyLayout_ReportsEveryRequiredAssembly()
    {
        var toolPath = CreateLayout([]);

        var missing = CliInstallLayout.FindMissingAssemblies(CliInstallLayout.EnumerateFiles(toolPath));

        missing.ShouldBe(CliInstallLayout.RequiredAssemblies, ignoreOrder: true);
    }

    [Fact]
    public void Describe_MissingDirectory_SaysSoRatherThanThrowing()
    {
        var absent = Path.Combine(_root, "no-such-dir");

        var described = CliInstallLayout.Describe(absent);

        described.ShouldContain("DOES NOT EXIST");
        described.ShouldContain(absent);
    }

    [Fact]
    public void Describe_TruncatesLongListingAndSaysHowMany()
    {
        var files = Enumerable.Range(0, 40).Select(i => $"f{i:D3}.dll").ToList();

        var described = CliInstallLayout.Describe(_root, files, maxEntries: 5);

        described.ShouldContain("... 35 further entries omitted (40 total)");
    }

    /// <summary>
    /// AC2: on a non-zero CLI exit the message must carry stdout, stderr, the resolved install
    /// directory and its listing together — the combination the #3237 evidence lacked.
    /// </summary>
    [Fact]
    public void FormatCliFailure_CarriesExitCodeStreamsAndLayout()
    {
        var toolPath = CreateLayout(CliInstallLayout.RequiredAssemblies);
        var files = CliInstallLayout.EnumerateFiles(toolPath);

        var message = CliInstallLayout.FormatCliFailure(
            "init --target /tmp/home", 1, "some stdout", "Could not load file or assembly 'X'", toolPath, files);

        message.ShouldContain("init --target /tmp/home");
        message.ShouldContain("ExitCode: 1");
        message.ShouldContain("some stdout");
        message.ShouldContain("Could not load file or assembly 'X'");
        message.ShouldContain(toolPath);
        message.ShouldContain("BotNexus.Agent.Providers.Core.dll");
    }

    [Fact]
    public void FormatCliFailure_ProcessStillRunning_RendersExitCodePlaceholder()
    {
        var message = CliInstallLayout.FormatCliFailure(
            "provider setup", exitCode: null, "out", "err", _root, []);

        message.ShouldContain("ExitCode: <still running>");
    }

    /// <summary>
    /// When the assembly is absent from the install layout AND from the package payload, the
    /// pack step is implicated; when it is present in the package but absent on disk, the install
    /// step is. Distinguishing those is the point of reading the nupkg.
    /// </summary>
    [Fact]
    public void FormatMissingAssemblyFailure_AttributesToPackOrInstall()
    {
        var toolPath = CreateLayout([]);
        string[] missing = ["BotNexus.Agent.Providers.Core.dll"];

        var installDropped = CliInstallLayout.FormatMissingAssemblyFailure(
            toolPath, missing, [], ["BotNexus.Cli.dll", "BotNexus.Agent.Providers.Core.dll"]);
        installDropped.ShouldContain("the install step dropped it");

        var packOmitted = CliInstallLayout.FormatMissingAssemblyFailure(
            toolPath, missing, [], ["BotNexus.Cli.dll"]);
        packOmitted.ShouldContain("the pack step omitted it");
    }

    [Fact]
    public void ReadPackagedToolAssemblies_ReturnsToolsDllNames()
    {
        var packDir = Path.Combine(_root, "pack");
        Directory.CreateDirectory(packDir);
        var nupkg = Path.Combine(packDir, "BotNexus.Cli.99.99.99-local-deadbeef.nupkg");
        using (var archive = ZipFile.Open(nupkg, ZipArchiveMode.Create))
        {
            archive.CreateEntry("tools/net10.0/any/BotNexus.Cli.dll");
            archive.CreateEntry("tools/net10.0/any/BotNexus.Agent.Providers.Core.dll");
            archive.CreateEntry("tools/net10.0/any/botnexus.runtimeconfig.json");
            archive.CreateEntry("lib/net10.0/NotATool.dll");
        }

        var assemblies = CliInstallLayout.ReadPackagedToolAssemblies(packDir);

        assemblies.ShouldBe(["BotNexus.Agent.Providers.Core.dll", "BotNexus.Cli.dll"]);
    }

    [Fact]
    public void ReadPackagedToolAssemblies_NoPackage_ReturnsEmptyRatherThanThrowing()
    {
        CliInstallLayout.ReadPackagedToolAssemblies(Path.Combine(_root, "nowhere")).ShouldBeEmpty();
    }
}
