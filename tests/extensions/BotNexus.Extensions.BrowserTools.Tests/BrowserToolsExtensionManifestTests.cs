using System.Text.Json;
using System.Xml.Linq;
using Shouldly;

namespace BotNexus.Extensions.BrowserTools.Tests;

/// <summary>
/// AC2, AC3 and AC4 of #3029: the zero-dependency rule, the manifest contract, and the
/// artifacts copy target.
/// </summary>
/// <remarks>
/// These read the real files off disk rather than a fixture. The point of AC2 is that nobody can
/// add a package to this extension without noticing; a test against an in-memory copy of the
/// csproj would keep passing while the real file grew a dependency.
/// </remarks>
public sealed class BrowserToolsExtensionManifestTests
{
    private static readonly string RepoRoot = FindRepoRoot();

    private static string BrowserToolsCsproj => Path.Combine(
        RepoRoot, "src", "extensions", "BotNexus.Extensions.BrowserTools",
        "BotNexus.Extensions.BrowserTools.csproj");

    private static string WebToolsCsproj => Path.Combine(
        RepoRoot, "src", "extensions", "BotNexus.Extensions.WebTools",
        "BotNexus.Extensions.WebTools.csproj");

    private static string ManifestPath => Path.Combine(
        RepoRoot, "src", "extensions", "BotNexus.Extensions.BrowserTools",
        "botnexus-extension.json");

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null
               && !Directory.Exists(Path.Combine(dir.FullName, "src", "extensions")))
        {
            dir = dir.Parent;
        }

        dir.ShouldNotBeNull("the test must be able to locate the repository root.");
        return dir.FullName;
    }

    private static IReadOnlyList<string> PackageReferences(string csprojPath)
    {
        File.Exists(csprojPath).ShouldBeTrue($"{csprojPath} must exist.");

        return XDocument.Load(csprojPath)
            .Descendants()
            .Where(e => e.Name.LocalName == "PackageReference")
            .Select(e => e.Attribute("Include")?.Value ?? string.Empty)
            .Where(v => v.Length > 0)
            .OrderBy(v => v, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // ---- AC2: no PackageReference beyond WebTools' set -----------------------------------

    [Fact]
    public void Ac2_BrowserToolsPackageReferences_AreASubsetOfWebToolsPackageReferences()
    {
        var browser = PackageReferences(BrowserToolsCsproj);
        var web = PackageReferences(WebToolsCsproj);

        // The subset relation is the criterion as written. It is expressed against the LIVE
        // WebTools file rather than against a hard-coded empty list so that if WebTools ever
        // legitimately takes a dependency, this fence relaxes with it instead of becoming a
        // stale assertion someone deletes.
        browser.Except(web, StringComparer.OrdinalIgnoreCase).ShouldBeEmpty(
            "BotNexus.Extensions.BrowserTools must declare no PackageReference beyond those "
            + "already in BotNexus.Extensions.WebTools.csproj (#3029 AC2).");
    }

    [Fact]
    public void Ac2_TheReferenceProject_StillDeclaresNoPackages_SoTheSubsetTestIsNotVacuous()
    {
        // Without this, the subset test above would pass trivially the moment WebTools grew a
        // long dependency list - "subset of everything" asserts nothing.
        PackageReferences(WebToolsCsproj).ShouldBeEmpty(
            "WebTools is the zero-dependency reference shape; if this changed, revisit AC2.");
    }

    [Fact]
    public void Ac2_TheTwoPackagesRemovedForThisIssue_AreNotReintroduced()
    {
        // Named explicitly because these two are the concrete regression: the project carried
        // them before #3029 and the refactor to IBrowserFileSystem exists to shed them.
        var browser = PackageReferences(BrowserToolsCsproj);

        browser.ShouldNotContain("TestableIO.System.IO.Abstractions.Wrappers");
        browser.ShouldNotContain("Microsoft.Extensions.Logging.Abstractions");
    }

    // ---- AC3: manifest id and config schema ----------------------------------------------

    [Fact]
    public void Ac3_Manifest_DeclaresTheBrowserExtensionIdAndEntryAssembly()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(ManifestPath));
        var root = doc.RootElement;

        root.GetProperty("id").GetString().ShouldBe("botnexus-browser");
        root.GetProperty("entryAssembly").GetString()
            .ShouldBe("BotNexus.Extensions.BrowserTools.dll");
        root.GetProperty("extensionTypes").EnumerateArray()
            .Select(e => e.GetString()).ShouldContain("tool");
    }

    [Theory]
    [InlineData("browser.binaryPath")]
    [InlineData("browser.pinnedVersion")]
    [InlineData("browser.autoProvision")]
    [InlineData("browser.commandTimeoutSeconds")]
    [InlineData("browser.snapshotMaxChars")]
    public void Ac3_Manifest_DeclaresEachRequiredConfigSchemaEntry(string configId)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(ManifestPath));

        var ids = doc.RootElement.GetProperty("configSchema").EnumerateArray()
            .Select(e => e.GetProperty("id").GetString())
            .ToList();

        ids.ShouldContain(configId);
    }

    [Fact]
    public void Ac3_AutoProvision_DefaultsToFalseInTheManifest()
    {
        // The default is a security control, not a preference (AC7). A manifest that shipped
        // "true" would opt every operator into a network fetch of an executable.
        using var doc = JsonDocument.Parse(File.ReadAllText(ManifestPath));

        var entry = doc.RootElement.GetProperty("configSchema").EnumerateArray()
            .Single(e => e.GetProperty("id").GetString() == "browser.autoProvision");

        entry.GetProperty("default").GetString().ShouldBe("false");
        new BrowserToolsConfig().AutoProvision.ShouldBeFalse(
            "the code default must agree with the manifest default.");
    }

    // ---- AC4: the artifacts copy target ---------------------------------------------------

    [Fact]
    public void Ac4_CsprojDeclaresACopyExtensionToArtifactsTarget_ForTheBrowserExtensionDirectory()
    {
        var doc = XDocument.Load(BrowserToolsCsproj);

        var target = doc.Descendants()
            .SingleOrDefault(e => e.Name.LocalName == "Target"
                && e.Attribute("Name")?.Value == "CopyExtensionToArtifacts");

        target.ShouldNotBeNull("the extension must be discoverable from artifacts/extensions/.");
        target.Attribute("AfterTargets")?.Value.ShouldBe("Build");

        var dir = target.Descendants()
            .Single(e => e.Name.LocalName == "ExtensionArtifactDir").Value;
        dir.Replace('\\', '/').ShouldContain("artifacts/extensions/botnexus-browser/");

        // The manifest itself must be among the copied files, or discovery finds an assembly
        // with nothing describing it.
        target.Descendants()
            .Where(e => e.Name.LocalName == "Copy")
            .Select(e => e.Attribute("SourceFiles")?.Value ?? string.Empty)
            .ShouldContain(v => v.Contains("botnexus-extension.json", StringComparison.Ordinal));
    }
}
