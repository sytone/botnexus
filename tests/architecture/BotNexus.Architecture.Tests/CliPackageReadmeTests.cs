using System.Xml.Linq;
using Shouldly;

namespace BotNexus.Architecture.Tests;

/// <summary>
/// Architecture fitness function: the CLI NuGet package must include a readme that
/// documents the .NET 10 SDK requirement so users see it before install.
/// </summary>
public sealed class CliPackageReadmeTests
{
    private static string RepoRoot => Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", "..", ".."));

    private static string CliCsproj => Path.Combine(
        RepoRoot, "src", "gateway", "BotNexus.Cli", "BotNexus.Cli.csproj");

    [Fact]
    public void Cli_Csproj_Has_PackageReadmeFile_Property()
    {
        File.Exists(CliCsproj).ShouldBeTrue($"CLI csproj not found at {CliCsproj}");

        var doc = XDocument.Load(CliCsproj);
        var ns = doc.Root!.GetDefaultNamespace();

        var readmeElement = doc.Descendants(ns + "PackageReadmeFile").FirstOrDefault();
        readmeElement.ShouldNotBeNull(
            "BotNexus.Cli.csproj must have a <PackageReadmeFile> element so the NuGet package " +
            "includes a readme documenting the .NET 10 SDK requirement.");
    }

    [Fact]
    public void Cli_PackageReadmeFile_Exists_On_Disk()
    {
        var doc = XDocument.Load(CliCsproj);
        var ns = doc.Root!.GetDefaultNamespace();
        var readmeFileName = doc.Descendants(ns + "PackageReadmeFile").FirstOrDefault()?.Value;

        readmeFileName.ShouldNotBeNullOrWhiteSpace();

        var cliDir = Path.GetDirectoryName(CliCsproj)!;
        var readmePath = Path.Combine(cliDir, readmeFileName);

        File.Exists(readmePath).ShouldBeTrue(
            $"PackageReadmeFile '{readmeFileName}' referenced in csproj does not exist at {readmePath}");
    }

    [Fact]
    public void Cli_PackageReadme_Documents_DotNet10_Requirement()
    {
        var doc = XDocument.Load(CliCsproj);
        var ns = doc.Root!.GetDefaultNamespace();
        var readmeFileName = doc.Descendants(ns + "PackageReadmeFile").FirstOrDefault()?.Value;

        readmeFileName.ShouldNotBeNullOrWhiteSpace();

        var cliDir = Path.GetDirectoryName(CliCsproj)!;
        var readmePath = Path.Combine(cliDir, readmeFileName);
        var content = File.ReadAllText(readmePath);

        content.ShouldContain(".NET", Case.Insensitive,
            "Package readme must mention .NET SDK requirement");
        content.ShouldContain("10", Case.Sensitive,
            "Package readme must mention version 10 (the minimum required SDK)");
    }

    [Fact]
    public void Cli_Description_Mentions_DotNet10()
    {
        var doc = XDocument.Load(CliCsproj);
        var ns = doc.Root!.GetDefaultNamespace();
        var description = doc.Descendants(ns + "Description").FirstOrDefault()?.Value;

        description.ShouldNotBeNullOrWhiteSpace();
        description.ShouldContain(".NET 10", Case.Insensitive,
            "CLI package <Description> should mention .NET 10 so NuGet gallery users see " +
            "the requirement before installing.");
    }
}
