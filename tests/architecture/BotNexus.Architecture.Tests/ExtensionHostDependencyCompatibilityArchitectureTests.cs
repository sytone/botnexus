using System.Reflection;
using System.Runtime.Loader;
using Mono.Cecil;

namespace BotNexus.Architecture.Tests;

/// <summary>
/// #4778: CopyLocal is necessary, but not sufficient. The real loader returns null for every
/// host-shipped simple name, so the HOST must satisfy references made by the entire private
/// extension closure. Read production DLL metadata, never testhost's merged dependency graph.
/// This fence checks binding identity/version, not API-level binary compatibility.
/// </summary>
public sealed class ExtensionHostDependencyCompatibilityArchitectureTests : ArchitectureTest
{
    [Fact]
    public void Built_deployable_extension_closures_are_compatible_with_the_actual_host()
    {
        var hostPath = ReadArtifactPaths("compatibility-host-artifact.txt").ShouldHaveSingleItem();
        Path.GetFileName(hostPath).ShouldBe("BotNexus.Gateway.Api.dll");
        var hostDirectory = RequireDirectory(hostPath);
        var extensionPaths = ReadArtifactPaths("compatibility-extension-artifacts.txt");
        extensionPaths.ShouldNotBeEmpty();

        // Keep discovery non-vacuous when an extension is added or removed. The recorded set must
        // be EXACTLY the manifest-bearing project set, not a curated ServiceBus-only list.
        var expected = Directory.EnumerateFiles(Repository.Path("src", "extensions"), "*.csproj", SearchOption.AllDirectories)
            .Where(path => File.Exists(Path.Combine(RequireDirectory(path), "botnexus-extension.json")))
            .Select(Path.GetFileNameWithoutExtension).Order(StringComparer.Ordinal).ToArray();
        extensionPaths.Select(Path.GetFileNameWithoutExtension).Order(StringComparer.Ordinal).ToArray().ShouldBe(expected);

        var fence = new ManagedAssemblyCompatibilityFence();
        var violations = new List<string>();
        var sharedReferenceCount = 0;
        foreach (var extensionPath in extensionPaths)
        {
            File.Exists(extensionPath).ShouldBeTrue($"Missing built extension: {extensionPath}");
            var result = fence.Inspect(hostDirectory, RequireDirectory(extensionPath));
            sharedReferenceCount += result.SharedReferenceCount;
            violations.AddRange(result.Violations.Select(message => $"{Path.GetFileName(extensionPath)}: {message}"));
        }

        sharedReferenceCount.ShouldBeGreaterThan(0, "The fence must examine actual host-shared assembly references.");
        violations.ShouldBeEmpty("Host unification cannot satisfy these built extension references (#4778):\n" + string.Join("\n", violations));
    }

    [Fact]
    public void ServiceBus_AzureCore_reference_binds_through_the_real_extension_load_context_to_the_host()
    {
        var hostDirectory = RequireDirectory(ReadArtifactPaths("compatibility-host-artifact.txt").ShouldHaveSingleItem());
        var extensionPath = ReadArtifactPaths("compatibility-extension-artifacts.txt")
            .Single(path => Path.GetFileName(path) == "BotNexus.Extensions.Channels.ServiceBus.dll");
        var extensionDirectory = RequireDirectory(extensionPath);
        var serviceBusPath = Path.Combine(extensionDirectory, "Azure.Messaging.ServiceBus.dll");
        var hostCorePath = Path.Combine(hostDirectory, "Azure.Core.dll");
        File.Exists(Path.Combine(extensionDirectory, "Azure.Core.dll")).ShouldBeTrue("The extension must ship its private Azure.Core copy.");
        File.Exists(Path.Combine(hostDirectory, "Azure.Messaging.ServiceBus.dll")).ShouldBeFalse("ServiceBus must remain extension-private.");

        using var serviceBusMetadata = AssemblyDefinition.ReadAssembly(serviceBusPath);
        var coreReference = serviceBusMetadata.MainModule.AssemblyReferences.Single(reference => reference.Name == "Azure.Core");
        using var hostCoreMetadata = AssemblyDefinition.ReadAssembly(hostCorePath);
        var fence = new ManagedAssemblyCompatibilityFence();
        fence.IsCompatible(coreReference, hostCoreMetadata.Name).ShouldBeTrue(
            $"ServiceBus requests {coreReference.FullName}, but the production host ships {hostCoreMetadata.Name.FullName}.");

        // Even if another architecture test has touched Azure.Core, it cannot silently replace
        // the artifact under test with a newer testhost copy. MVID checks content, not just name.
        var hostCore = AssemblyLoadContext.Default.LoadFromAssemblyPath(hostCorePath);
        hostCore.ManifestModule.ModuleVersionId.ShouldBe(hostCoreMetadata.MainModule.Mvid,
            "Testhost Azure.Core differs from the real host artifact; this test must not mask a host downgrade.");
        var loaderType = typeof(BotNexus.Gateway.GatewayHost).Assembly.GetType(
            "BotNexus.Gateway.Extensions.ExtensionAssemblyLoadContext", throwOnError: true)
            ?? throw new InvalidOperationException("The production extension loader type is missing.");
        var context = Activator.CreateInstance(loaderType, [extensionPath, true]) as AssemblyLoadContext
            ?? throw new InvalidOperationException("Could not construct the production extension load context.");
        try
        {
            var serviceBus = context.LoadFromAssemblyName(AssemblyName.GetAssemblyName(serviceBusPath));
            AssemblyLoadContext.GetLoadContext(serviceBus).ShouldBeSameAs(context,
                "A dependency absent from the host must remain extension-private.");
            serviceBus.GetType("Azure.Messaging.ServiceBus.ServiceBusClient", throwOnError: true).ShouldNotBeNull();
            var boundCore = context.LoadFromAssemblyName(new AssemblyName(coreReference.FullName));
            boundCore.ShouldBeSameAs(hostCore, "A private duplicate Azure.Core is not an acceptable fix.");
            AssemblyLoadContext.GetLoadContext(boundCore).ShouldBeSameAs(AssemblyLoadContext.Default);
            boundCore.GetType("Azure.Core.TokenCredential", throwOnError: true).ShouldBeSameAs(
                hostCore.GetType("Azure.Core.TokenCredential", throwOnError: true));
        }
        finally
        {
            context.Unload();
        }
    }

    [Theory]
    [InlineData("1.44.1.0", "Shared.Dependency", "neutral", "0011223344556677", false)]
    [InlineData("1.46.2.0", "Other.Dependency", "neutral", "0011223344556677", false)]
    [InlineData("1.46.2.0", "Shared.Dependency", "neutral", "8899aabbccddeeff", false)]
    [InlineData("1.46.2.0", "Shared.Dependency", "fr", "0011223344556677", false)]
    [InlineData("1.46.2.0", "Shared.Dependency", "neutral", "0011223344556677", true)]
    [InlineData("1.47.0.0", "Shared.Dependency", "neutral", "0011223344556677", true)]
    public void Compatibility_requires_sufficient_version_and_matching_binding_identity(
        string hostVersion, string hostName, string hostCulture, string hostToken, bool expected)
    {
        var requested = Identity("Shared.Dependency", "1.46.2.0", "neutral", "0011223344556677");
        var supplied = Identity(hostName, hostVersion, hostCulture, hostToken);
        new ManagedAssemblyCompatibilityFence().IsCompatible(requested, supplied).ShouldBe(expected);
    }

    [Theory]
    [InlineData("1.44.1.0", "0011223344556677", 1)]
    [InlineData("1.46.2.0", "8899aabbccddeeff", 1)]
    [InlineData("1.46.2.0", "0011223344556677", 0)]
    public void Metadata_scan_detects_incompatible_host_sharing_in_a_synthetic_private_closure(
        string hostVersion, string hostToken, int expectedViolations)
    {
        var root = Path.Combine(Path.GetTempPath(), "bn4778-" + Guid.NewGuid().ToString("N"));
        var host = Path.Combine(root, "host");
        var extension = Path.Combine(root, "extension");
        Directory.CreateDirectory(host);
        Directory.CreateDirectory(extension);
        try
        {
            // The extension's private copy is compatible. It still cannot save a lower-version
            // or differently signed host copy because the loader unifies by SIMPLE NAME.
            // Assembly definitions store a public key, not a token. Cecil derives the token
            // when reading the emitted DLLs. Arbitrary distinct fixture keys suffice here:
            // this is a metadata identity test, not a cryptographic signature verification.
            var hostIdentity = new AssemblyNameDefinition("Shared.Dependency", Version.Parse(hostVersion))
            {
                PublicKey = Convert.FromHexString(hostToken)
            };
            var privateIdentity = new AssemblyNameDefinition("Shared.Dependency", new Version(1, 46, 2, 0))
            {
                PublicKey = Convert.FromHexString("0011223344556677")
            };
            var requested = new AssemblyNameReference(privateIdentity.Name, privateIdentity.Version)
            {
                PublicKeyToken = privateIdentity.PublicKeyToken
            };
            WriteFixture(Path.Combine(host, "Shared.Dependency.dll"), hostIdentity);
            WriteFixture(Path.Combine(extension, "Shared.Dependency.dll"), privateIdentity);
            WriteFixture(Path.Combine(extension, "Private.Consumer.dll"), Identity("Private.Consumer", "1.0.0.0", "neutral", ""), requested);
            // A higher-version reference absent from the host is genuinely private, not an error.
            WriteFixture(Path.Combine(extension, "Extension.Entry.dll"), Identity("Extension.Entry", "1.0.0.0", "neutral", ""),
                Identity("Private.Dependency", "9.0.0.0", "neutral", ""));
            var result = new ManagedAssemblyCompatibilityFence().Inspect(host, extension);
            result.SharedReferenceCount.ShouldBe(1);
            result.Violations.Count.ShouldBe(expectedViolations);
            if (expectedViolations > 0)
            {
                var violation = result.Violations.ShouldHaveSingleItem();
                violation.ShouldContain("Private.Consumer.dll");
                violation.ShouldContain(requested.FullName);
                violation.ShouldContain("host supplies");
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private string[] ReadArtifactPaths(string manifest)
    {
        var path = Path.Combine(AppContext.BaseDirectory, manifest);
        File.Exists(path).ShouldBeTrue($"Build Architecture.Tests to record production artifact paths: missing {path}");
        var paths = File.ReadAllLines(path).Where(line => !string.IsNullOrWhiteSpace(line)).Select(Path.GetFullPath).ToArray();
        foreach (var artifact in paths)
        {
            Path.GetRelativePath(Repository.SourceRoot, artifact).StartsWith("..", StringComparison.Ordinal).ShouldBeFalse(
                $"Artifact must come from this checkout's production source tree: {artifact}");
            File.Exists(artifact).ShouldBeTrue($"Missing production artifact: {artifact}");
        }
        return paths;
    }

    private static string RequireDirectory(string path) => Path.GetDirectoryName(path)
        ?? throw new InvalidOperationException($"No parent directory for {path}.");

    private static AssemblyNameDefinition Identity(string name, string version, string culture, string token) => new(name, Version.Parse(version))
    {
        Culture = culture == "neutral" ? "" : culture,
        PublicKeyToken = Convert.FromHexString(token)
    };

    private static void WriteFixture(string path, AssemblyNameDefinition identity, AssemblyNameReference? reference = null)
    {
        using var assembly = AssemblyDefinition.CreateAssembly(identity, identity.Name, ModuleKind.Dll);
        if (reference is not null)
            assembly.MainModule.AssemblyReferences.Add(reference);
        assembly.Write(path);
    }
}

/// <summary>Reads only managed metadata; never resolves dependencies into the test process.</summary>
internal sealed class ManagedAssemblyCompatibilityFence
{
    public bool IsCompatible(AssemblyNameReference requested, AssemblyNameReference supplied) =>
        string.Equals(requested.Name, supplied.Name, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(requested.Culture ?? "", supplied.Culture ?? "", StringComparison.OrdinalIgnoreCase) &&
        requested.PublicKeyToken.AsSpan().SequenceEqual(supplied.PublicKeyToken) &&
        supplied.Version >= requested.Version;

    public CompatibilityInspection Inspect(string hostDirectory, string extensionDirectory)
    {
        var host = new Dictionary<string, AssemblyNameDefinition>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in Directory.EnumerateFiles(hostDirectory, "*.dll"))
        {
            using var assembly = ReadManagedAssembly(path);
            if (assembly is not null)
                host.Add(assembly.Name.Name, assembly.Name);
        }

        var violations = new List<string>();
        var sharedReferenceCount = 0;
        foreach (var path in Directory.EnumerateFiles(extensionDirectory, "*.dll"))
        {
            using var assembly = ReadManagedAssembly(path);
            if (assembly is null)
                continue;
            // These copies never execute in the extension context: the loader substitutes the
            // host assembly itself. Inspect references from genuinely extension-private DLLs,
            // including third-party dependencies, not dead references in replaced copies.
            if (host.ContainsKey(assembly.Name.Name))
                continue;
            foreach (var reference in assembly.MainModule.AssemblyReferences)
            {
                if (!host.TryGetValue(reference.Name, out var supplied))
                    continue;
                sharedReferenceCount++;
                if (!IsCompatible(reference, supplied))
                    violations.Add($"{Path.GetFileName(path)} requests {reference.FullName}; host supplies {supplied.FullName}");
            }
        }
        return new CompatibilityInspection(sharedReferenceCount, violations);
    }

    private static AssemblyDefinition? ReadManagedAssembly(string path)
    {
        try
        {
            return AssemblyDefinition.ReadAssembly(path);
        }
        catch (BadImageFormatException)
        {
            // Native libraries are outside the managed-binding contract.
            return null;
        }
    }
}

internal sealed record CompatibilityInspection(int SharedReferenceCount, IReadOnlyList<string> Violations);
