using System.Net;
using System.Security.Cryptography;
using System.Text;
using Shouldly;

namespace BotNexus.Extensions.BrowserTools.Tests;

/// <summary>
/// AC5-AC8 of #3029: the fixed resolution order, the non-throwing not-found message, the
/// no-network guarantee when auto-provision is off, and sha256 verification on download.
/// </summary>
/// <remarks>
/// Every test drives the resolver through injected seams - fake filesystem, fake PATH, fake home
/// directory, fake HTTP handler - so nothing here touches the real disk or the real network. The
/// no-network claim in particular is asserted by a handler that THROWS if it is ever invoked,
/// rather than by inspecting a flag the code under test could have forgotten to set.
/// </remarks>
public sealed class AgentBrowserBinaryResolverTests
{
    private const string Rid = "win-x64";
    private const string Version = "0.1.0";

    private static readonly string Home = Path.Combine(Path.GetTempPath(), "botnexus-fake-home");
    private static readonly string PathDir = Path.Combine(Path.GetTempPath(), "botnexus-fake-bin");

    /// <summary>
    /// An <see cref="HttpMessageHandler"/> that fails the test if it is used at all.
    /// </summary>
    private sealed class ExplodingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new InvalidOperationException(
                $"A network call was made to '{request.RequestUri}' when none was permitted.");
    }

    /// <summary>Returns a fixed payload and records how many times it was asked.</summary>
    private sealed class StubHandler(byte[] payload, HttpStatusCode status = HttpStatusCode.OK)
        : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new ByteArrayContent(payload),
            });
        }
    }

    private static AgentBrowserBinaryResolver Create(
        BrowserToolsConfig config,
        IBrowserFileSystem fs,
        HttpMessageHandler? handler = null,
        AgentBrowserReleaseCatalog? catalog = null,
        string? path = null)
        => new(
            config,
            fs,
            catalog,
            handler is null ? null : () => new HttpClient(handler, disposeHandler: false),
            () => path,
            () => Home,
            Rid,
            isWindows: true);

    private static string ManagedPath() =>
        Path.Combine(Home, ".botnexus", "tools", "agent-browser", Version, "agent-browser.exe");

    // ---- AC5(1): the configured path wins -------------------------------------------------

    [Fact]
    public async Task Ac5_ConfiguredBinaryPath_IsUsedAheadOfEveryOtherSource()
    {
        // Every other source is ALSO available here. If the resolver returned one of them the
        // test still gets a resolved path, so asserting only IsResolved would not pin the order;
        // the Source assertion is what makes "first" mean first.
        var configured = Path.Combine(Path.GetTempPath(), "custom", "agent-browser.exe");
        var fs = new FakeBrowserFileSystem()
            .AddFile(configured)
            .AddFile(ManagedPath())
            .AddFile(Path.Combine(PathDir, "agent-browser.exe"));

        var result = await Create(
            new BrowserToolsConfig { BinaryPath = configured, PinnedVersion = Version },
            fs, new ExplodingHandler(), path: PathDir).ResolveAsync();

        result.IsResolved.ShouldBeTrue(result.Message);
        result.Source.ShouldBe(AgentBrowserSource.ConfiguredPath);
        result.BinaryPath.ShouldBe(configured);
    }

    [Fact]
    public async Task Ac5_ConfiguredBinaryPathThatDoesNotExist_FailsLoudlyInsteadOfFallingBack()
    {
        // Silently falling through to PATH would run a DIFFERENT binary from the one the
        // operator named while reporting success - the worst of both outcomes.
        var fs = new FakeBrowserFileSystem().AddFile(Path.Combine(PathDir, "agent-browser.exe"));

        var result = await Create(
            new BrowserToolsConfig { BinaryPath = "/nope/agent-browser", PinnedVersion = Version },
            fs, new ExplodingHandler(), path: PathDir).ResolveAsync();

        result.IsResolved.ShouldBeFalse();
        result.Message!.ShouldContain("/nope/agent-browser");
    }

    // ---- AC5(2): the managed directory ----------------------------------------------------

    [Fact]
    public async Task Ac5_ManagedDirectory_IsUsedWhenNoPathIsConfigured_AndBeatsPath()
    {
        var fs = new FakeBrowserFileSystem()
            .AddFile(ManagedPath())
            .AddFile(Path.Combine(PathDir, "agent-browser.exe"));

        var result = await Create(
            new BrowserToolsConfig { PinnedVersion = Version },
            fs, new ExplodingHandler(), path: PathDir).ResolveAsync();

        result.Source.ShouldBe(AgentBrowserSource.ManagedDirectory);
        result.BinaryPath.ShouldBe(ManagedPath());
    }

    [Fact]
    public async Task Ac5_ManagedDirectory_IsVersionScoped_SoADifferentPinDoesNotReuseTheOldBinary()
    {
        // The pinned version is part of the contract, not a label. A resolver that ignored it
        // would hand a newly-pinned build the previous binary and its previous JSON shape.
        var fs = new FakeBrowserFileSystem().AddFile(ManagedPath());

        var result = await Create(
            new BrowserToolsConfig { PinnedVersion = "9.9.9" },
            fs, new ExplodingHandler()).ResolveAsync();

        result.IsResolved.ShouldBeFalse(
            "the 0.1.0 binary must not satisfy a 9.9.9 pin.");
    }

    // ---- AC5(3): PATH ---------------------------------------------------------------------

    [Fact]
    public async Task Ac5_PathEntry_IsUsedWhenNeitherConfigNorManagedDirectoryHasABinary()
    {
        var onPath = Path.Combine(PathDir, "agent-browser.exe");
        var fs = new FakeBrowserFileSystem().AddFile(onPath);

        var result = await Create(
            new BrowserToolsConfig { PinnedVersion = Version },
            fs, new ExplodingHandler(),
            path: string.Join(Path.PathSeparator, "/empty/one", PathDir)).ResolveAsync();

        result.Source.ShouldBe(AgentBrowserSource.Path);
        result.BinaryPath.ShouldBe(onPath);
    }

    // ---- AC6: not found is a message, never an exception -----------------------------------

    [Fact]
    public async Task Ac6_WhenNothingIsFound_TheResultNamesEveryInstallOptionAndDoesNotThrow()
    {
        var fs = new FakeBrowserFileSystem();

        var result = await Create(
            new BrowserToolsConfig { PinnedVersion = Version },
            fs, new ExplodingHandler(), path: PathDir).ResolveAsync();

        result.IsResolved.ShouldBeFalse();
        result.Source.ShouldBe(AgentBrowserSource.NotFound);
        result.BinaryPath.ShouldBeNull();

        // The concrete commands are the actionable part; "not found" alone tells the operator
        // only what they already know.
        result.Message!.ShouldContain("npm i -g agent-browser");
        result.Message!.ShouldContain("brew install");
        result.Message!.ShouldContain("cargo install");
        result.Message!.ShouldContain("browser.binaryPath");
    }

    // ---- AC7: autoProvision false means no network and no write ---------------------------

    [Fact]
    public async Task Ac7_AutoProvisionDisabledWithNoBinary_MakesNoNetworkCallAndWritesNothing()
    {
        var fs = new FakeBrowserFileSystem();
        var handler = new ExplodingHandler();
        var catalog = new AgentBrowserReleaseCatalog(
            [new AgentBrowserReleaseAsset(Version, Rid, "https://example.invalid/ab.zip", "00")]);

        // The asset IS pinned and IS reachable in the catalogue: the only thing stopping the
        // download is the flag. That is precisely the condition worth testing.
        var result = await Create(
            new BrowserToolsConfig { PinnedVersion = Version, AutoProvision = false },
            fs, handler, catalog, path: PathDir).ResolveAsync();

        result.IsResolved.ShouldBeFalse();
        result.Message!.ShouldContain("autoProvision");
        fs.AllFiles.ShouldBeEmpty("no file may be written when provisioning is disabled.");
        fs.CreatedDirectories.ShouldBeEmpty("not even the managed directory may be created.");
    }

    [Fact]
    public void Ac7_AutoProvision_DefaultsToFalse()
        => new BrowserToolsConfig().AutoProvision.ShouldBeFalse();

    // ---- AC8: sha256 verification ---------------------------------------------------------

    [Fact]
    public async Task Ac8_ProvisionedBinaryMatchingThePinnedDigest_IsKeptAndReturned()
    {
        var payload = Encoding.UTF8.GetBytes("a genuine agent-browser build");
        var digest = Convert.ToHexStringLower(SHA256.HashData(payload));
        var handler = new StubHandler(payload);
        var fs = new FakeBrowserFileSystem();
        var catalog = new AgentBrowserReleaseCatalog(
            [new AgentBrowserReleaseAsset(Version, Rid, "https://example.invalid/ab", digest)]);

        var result = await Create(
            new BrowserToolsConfig { PinnedVersion = Version, AutoProvision = true },
            fs, handler, catalog, path: PathDir).ResolveAsync();

        result.IsResolved.ShouldBeTrue(result.Message);
        result.Source.ShouldBe(AgentBrowserSource.Provisioned);
        result.BinaryPath.ShouldBe(ManagedPath());
        handler.Calls.ShouldBe(1);
        fs.GetFileBytes(ManagedPath()).ShouldBe(payload);
        fs.DeletedFiles.ShouldBeEmpty();
    }

    [Fact]
    public async Task Ac8_ProvisionedBinaryFailingTheDigestCheck_IsDeletedAndTheResolutionFails()
    {
        // The corrupted payload stands in for a tampered or truncated asset. Both halves matter:
        // failing while leaving the file on disk would let the NEXT resolve find it at the
        // managed-directory step and execute it with no verification at all.
        var corrupted = Encoding.UTF8.GetBytes("tampered payload");
        var expected = Convert.ToHexStringLower(SHA256.HashData("the real thing"u8.ToArray()));
        var handler = new StubHandler(corrupted);
        var fs = new FakeBrowserFileSystem();
        var catalog = new AgentBrowserReleaseCatalog(
            [new AgentBrowserReleaseAsset(Version, Rid, "https://example.invalid/ab", expected)]);

        var result = await Create(
            new BrowserToolsConfig { PinnedVersion = Version, AutoProvision = true },
            fs, handler, catalog, path: PathDir).ResolveAsync();

        result.IsResolved.ShouldBeFalse();
        result.Message!.ShouldContain("sha256");
        fs.DeletedFiles.ShouldContain(ManagedPath());
        fs.AllFiles.ShouldNotContain(ManagedPath(),
            "a binary that failed verification must not survive on disk.");
    }

    [Fact]
    public async Task Ac8_AutoProvisionForAVersionWithNoPinnedDigest_FailsWithoutDownloading()
    {
        // An unpinned version has no expected digest, so a download could not be verified at
        // all. Fetch-then-trust is the exact failure this criterion exists to prevent.
        var fs = new FakeBrowserFileSystem();
        var handler = new ExplodingHandler();

        var result = await Create(
            new BrowserToolsConfig { PinnedVersion = "7.7.7", AutoProvision = true },
            fs, handler, new AgentBrowserReleaseCatalog([]), path: PathDir).ResolveAsync();

        result.IsResolved.ShouldBeFalse();
        result.Message!.ShouldContain("7.7.7");
        fs.AllFiles.ShouldBeEmpty();
    }

    [Fact]
    public void Ac8_DefaultCatalog_IsEmpty_SoNoUnreviewedAssetCanBeFetched()
        => AgentBrowserReleaseCatalog.Default.Find(Version, Rid).ShouldBeNull();
}
