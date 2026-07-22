using System.Text.RegularExpressions;
using Shouldly;

namespace BotNexus.Architecture.Tests;

/// <summary>
/// Architecture fitness function guarding the extension managed-dependency-closure contract (#2184,
/// companion to the ServiceBus adapter fix #2001).
///
/// <para>
/// BotNexus extensions are loaded into an isolated <see cref="System.Runtime.Loader.AssemblyLoadContext"/>
/// at runtime and their build output is copied verbatim to <c>{home}/extensions/{id}</c> by
/// <c>ServeCommand.DeployExtensions</c>. A plain SDK library project does <b>not</b> copy its
/// transitive managed NuGet dependencies into <c>bin</c> - only project-reference DLLs and native
/// <c>runtimes/**</c> assets land there. So any extension that references a third-party managed
/// NuGet package which the host gateway has not already loaded ships an incomplete closure: the ALC
/// cannot resolve the assembly and the process fails with <see cref="System.IO.FileNotFoundException"/>.
/// For the audio-transcription extension this surfaced as a gateway crash during
/// <c>Host.DisposeAsync</c> (the throw happens during JIT assembly resolution, before the
/// <c>DisposeAsync</c> try/catch frame is active, so the host guard cannot suppress it).
/// </para>
///
/// <para>
/// The lesson from #2001 (ServiceBus) and #2184 (Whisper.net) is that this is a <b>category</b> of
/// mistake, not a single project: every new extension that adds a <c>PackageReference</c> must opt
/// into copying its managed dependency closure. A per-project test catches one project; it does not
/// stop the next extension from silently shipping an incomplete closure. This fence is a static,
/// zero-runtime XML scan: every <b>deployable</b> extension project (one carrying a
/// <c>botnexus-extension.json</c> manifest) that declares at least one <c>PackageReference</c> MUST
/// set <c>&lt;CopyLocalLockFileAssemblies&gt;true&lt;/CopyLocalLockFileAssemblies&gt;</c> (or the
/// equivalent <c>&lt;EnableDynamicLoading&gt;true&lt;/EnableDynamicLoading&gt;</c>, which also emits
/// the closure). A newly added extension that forgets the property fails CI here - naming the
/// offending project - instead of crashing the gateway in production.
/// </para>
/// </summary>
public sealed class ExtensionManagedDependencyClosureArchitectureTests
{
    private static string RepoRoot => FindRepoRoot();
    private static string ExtensionsRoot => Path.Combine(RepoRoot, "src", "extensions");

    [Fact]
    public void ExtensionsDirectory_Exists()
    {
        Directory.Exists(ExtensionsRoot).ShouldBeTrue(
            $"Extensions directory not found at {ExtensionsRoot}");
    }

    [Fact]
    public void DeployableExtensionsWithPackageReferences_EmitManagedDependencyClosure()
    {
        var offenders = new List<string>();

        foreach (var csproj in Directory.GetFiles(ExtensionsRoot, "*.csproj", SearchOption.AllDirectories))
        {
            var projectDir = Path.GetDirectoryName(csproj)!;

            // Only deployable extensions matter: DeployExtensions keys off the manifest, and only
            // deployed output is loaded into an isolated ALC where an incomplete closure bites.
            var manifestPath = Path.Combine(projectDir, "botnexus-extension.json");
            if (!File.Exists(manifestPath))
            {
                continue;
            }

            var xml = File.ReadAllText(csproj);

            // No third-party managed NuGet packages -> nothing to copy, fence does not apply.
            if (!HasPackageReference(xml))
            {
                continue;
            }

            if (!EmitsManagedClosure(xml))
            {
                offenders.Add(Path.GetFileNameWithoutExtension(csproj));
            }
        }

        offenders.ShouldBeEmpty(
            "The following deployable extension project(s) declare a <PackageReference> but do NOT " +
            "emit their managed dependency closure to bin output (missing " +
            "<CopyLocalLockFileAssemblies>true</CopyLocalLockFileAssemblies> or " +
            "<EnableDynamicLoading>true</EnableDynamicLoading>). Their third-party managed " +
            "assemblies will be stripped from the extension deploy directory and the isolated " +
            "AssemblyLoadContext will fail with FileNotFoundException at runtime, crashing the " +
            "gateway. Add the property to each project. See issues #2184 and #2001.\nOffenders: " +
            string.Join(", ", offenders));
    }

    [Fact]
    public void Fence_IsNotVacuous_DetectsPackageReferenceWithoutClosure()
    {
        const string broken = """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PackageReference Include="Whisper.net" />
              </ItemGroup>
            </Project>
            """;

        HasPackageReference(broken).ShouldBeTrue(
            "Vacuity guard: a project with a <PackageReference> must be detected as having one.");
        EmitsManagedClosure(broken).ShouldBeFalse(
            "Vacuity guard: a project without CopyLocalLockFileAssemblies/EnableDynamicLoading must " +
            "be detected as NOT emitting its closure. If this fails, the fence passes vacuously.");
    }

    [Fact]
    public void Fence_PositivePin_AcceptsPackageReferenceWithClosure()
    {
        const string fixedCopyLocal = """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <CopyLocalLockFileAssemblies>true</CopyLocalLockFileAssemblies>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Whisper.net" />
              </ItemGroup>
            </Project>
            """;
        const string fixedDynamicLoading = """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <EnableDynamicLoading>true</EnableDynamicLoading>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Whisper.net" />
              </ItemGroup>
            </Project>
            """;

        EmitsManagedClosure(fixedCopyLocal).ShouldBeTrue(
            "Positive pin: CopyLocalLockFileAssemblies=true must be accepted.");
        EmitsManagedClosure(fixedDynamicLoading).ShouldBeTrue(
            "Positive pin: EnableDynamicLoading=true must be accepted.");
    }

    // ---- helpers ----

    private static bool HasPackageReference(string csprojXml) =>
        Regex.IsMatch(csprojXml, @"<PackageReference\b", RegexOptions.IgnoreCase);

    private static bool EmitsManagedClosure(string csprojXml) =>
        Regex.IsMatch(csprojXml, @"<CopyLocalLockFileAssemblies>\s*true\s*</CopyLocalLockFileAssemblies>", RegexOptions.IgnoreCase) ||
        Regex.IsMatch(csprojXml, @"<EnableDynamicLoading>\s*true\s*</EnableDynamicLoading>", RegexOptions.IgnoreCase);

    private static string FindRepoRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "BotNexus.slnx")))
        {
            current = current.Parent;
        }

        current.ShouldNotBeNull("Could not locate repo root (BotNexus.slnx) from " + AppContext.BaseDirectory);
        return current!.FullName;
    }
}
