using System.Text.Json;
using BotNexus.Cli.Commands;
using BotNexus.Gateway.Configuration;
using System.IO.Abstractions;

namespace BotNexus.Cli.Tests;

public sealed class ExtensionDeploymentReconcilerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"botnexus-extension-deploy-{Guid.NewGuid():N}");

    [Fact]
    public async Task DeployExtensionsSilent_UsesEnabledRegistryStagingAndPreservesItAcrossUpdates()
    {
        var repoRoot = Directory.CreateDirectory(Path.Combine(_root, "repo")).FullName;
        var home = Directory.CreateDirectory(Path.Combine(_root, "home")).FullName;
        var staged = Path.Combine(home, "extension-repositories", "community", "staged");
        Directory.CreateDirectory(staged);
        WriteManifest(staged, "community-tools", "community.dll");
        File.WriteAllText(Path.Combine(staged, "community.dll"), "community-v1");
        var registry = new ExtensionRepositoryRegistryService(Path.Combine(home, "config.json"), new FileSystem());
        await registry.AddAsync("community", "https://example.test/community.git", "main");

        ServeCommand.DeployExtensionsSilent(repoRoot, home, verbose: false).DeployedCount.ShouldBe(1);
        ServeCommand.DeployExtensionsSilent(repoRoot, home, verbose: false).DeployedCount.ShouldBe(1);

        File.ReadAllText(Path.Combine(home, "extensions", "community-tools", "community.dll"))
            .ShouldBe("community-v1");
    }

    [Fact]
    public void Reconcile_RegisteredOutputReplacesLiveDirectoryAndSurvivesLaterPrune()
    {
        var liveRoot = Directory.CreateDirectory(Path.Combine(_root, "live")).FullName;
        var builtIn = CreateOutput("built-in", "built-in.dll", "built-in-v1");
        var registered = CreateOutput("community-tools", "community.dll", "community-v1");
        Directory.CreateDirectory(Path.Combine(liveRoot, "stale"));

        var first = ExtensionDeploymentReconciler.Reconcile(
            liveRoot,
            [new ExtensionDeploymentSource("in-tree:built-in", builtIn, true, false),
             new ExtensionDeploymentSource("repository:community", registered, true, true)]);

        first.Failures.ShouldBeEmpty();
        File.ReadAllText(Path.Combine(liveRoot, "community-tools", "community.dll")).ShouldBe("community-v1");
        Directory.Exists(Path.Combine(liveRoot, "stale")).ShouldBeFalse();

        var second = ExtensionDeploymentReconciler.Reconcile(
            liveRoot,
            [new ExtensionDeploymentSource("in-tree:built-in", builtIn, true, false),
             new ExtensionDeploymentSource("repository:community", registered, true, true)]);

        second.Failures.ShouldBeEmpty();
        Directory.Exists(Path.Combine(liveRoot, "community-tools")).ShouldBeTrue();
    }

    [Fact]
    public void Reconcile_MalformedRegisteredManifestPreservesPriorDeploymentAndReportsFailure()
    {
        var liveRoot = Directory.CreateDirectory(Path.Combine(_root, "live")).FullName;
        var registered = CreateOutput("community-tools", "community.dll", "last-known-good");
        var source = new ExtensionDeploymentSource("repository:community", registered, true, true);
        ExtensionDeploymentReconciler.Reconcile(liveRoot, [source]).Failures.ShouldBeEmpty();
        File.WriteAllText(Path.Combine(registered, "botnexus-extension.json"), "{not-json");

        var failed = ExtensionDeploymentReconciler.Reconcile(liveRoot, [source]);

        failed.Failures.Single().Source.ShouldBe("repository:community");
        File.ReadAllText(Path.Combine(liveRoot, "community-tools", "community.dll"))
            .ShouldBe("last-known-good");

        WriteManifest(registered, "community-tools", "community.dll");
        File.WriteAllText(Path.Combine(registered, "community.dll"), "recovered");
        ExtensionDeploymentReconciler.Reconcile(liveRoot, [source]).Failures.ShouldBeEmpty();
        File.ReadAllText(Path.Combine(liveRoot, "community-tools", "community.dll")).ShouldBe("recovered");
    }

    [Fact]
    public void Reconcile_CollisionNamesBothSourcesBeforeMutatingLiveDeployment()
    {
        var liveRoot = Directory.CreateDirectory(Path.Combine(_root, "live")).FullName;
        var prior = Path.Combine(liveRoot, "shared-id");
        Directory.CreateDirectory(prior);
        File.WriteAllText(Path.Combine(prior, "sentinel.txt"), "unchanged");
        var builtIn = CreateOutput("shared-id", "built-in.dll", "built-in");
        var registered = CreateOutput("shared-id", "community.dll", "community");

        var exception = Should.Throw<InvalidOperationException>(() => ExtensionDeploymentReconciler.Reconcile(
            liveRoot,
            [new ExtensionDeploymentSource("in-tree:shared", builtIn, true, false),
             new ExtensionDeploymentSource("repository:community", registered, true, true)]));

        exception.Message.ShouldContain("in-tree:shared");
        exception.Message.ShouldContain("repository:community");
        File.ReadAllText(Path.Combine(prior, "sentinel.txt")).ShouldBe("unchanged");
        File.Exists(Path.Combine(prior, "built-in.dll")).ShouldBeFalse();
        File.Exists(Path.Combine(prior, "community.dll")).ShouldBeFalse();
    }

    [Fact]
    public void Reconcile_FailedRegisteredSourceCollisionWithValidSourceNamesBothBeforeMutation()
    {
        var liveRoot = Directory.CreateDirectory(Path.Combine(_root, "live")).FullName;
        var registered = CreateOutput("shared-id", "community.dll", "last-known-good");
        var registeredSource = new ExtensionDeploymentSource("repository:community", registered, true, true);
        ExtensionDeploymentReconciler.Reconcile(liveRoot, [registeredSource]).Failures.ShouldBeEmpty();
        File.WriteAllText(Path.Combine(registered, "botnexus-extension.json"), "{not-json");
        var builtIn = CreateOutput("shared-id", "built-in.dll", "built-in");

        var exception = Should.Throw<InvalidOperationException>(() => ExtensionDeploymentReconciler.Reconcile(
            liveRoot,
            [new ExtensionDeploymentSource("in-tree:shared", builtIn, true, false), registeredSource]));

        exception.Message.ShouldContain("in-tree:shared");
        exception.Message.ShouldContain("repository:community");
        File.ReadAllText(Path.Combine(liveRoot, "shared-id", "community.dll")).ShouldBe("last-known-good");
        File.Exists(Path.Combine(liveRoot, "shared-id", "built-in.dll")).ShouldBeFalse();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Reconcile_DisabledOrRemovedRegistrationUndeploysWithoutDeletingManagedSource(bool retainDisabledRegistration)
    {
        var liveRoot = Directory.CreateDirectory(Path.Combine(_root, "live")).FullName;
        var registered = CreateOutput("community-tools", "community.dll", "community");
        var enabled = new ExtensionDeploymentSource("repository:community", registered, true, true);
        ExtensionDeploymentReconciler.Reconcile(liveRoot, [enabled]).Failures.ShouldBeEmpty();
        File.WriteAllText(Path.Combine(registered, "operator-change.txt"), "dirty clone evidence");

        var sources = retainDisabledRegistration
            ? new[] { enabled with { Enabled = false } }
            : Array.Empty<ExtensionDeploymentSource>();
        ExtensionDeploymentReconciler.Reconcile(liveRoot, sources).Failures.ShouldBeEmpty();

        Directory.Exists(Path.Combine(liveRoot, "community-tools")).ShouldBeFalse();
        File.ReadAllText(Path.Combine(registered, "operator-change.txt")).ShouldBe("dirty clone evidence");
    }

    [Fact]
    public void Reconcile_DuplicateIdsWithinOneRegisteredRepositoryNameBothOutputsBeforeMutation()
    {
        var liveRoot = Directory.CreateDirectory(Path.Combine(_root, "live")).FullName;
        var first = CreateOutput("shared-id", "first.dll", "first");
        var second = CreateOutput("shared-id", "second.dll", "second");

        var exception = Should.Throw<InvalidOperationException>(() => ExtensionDeploymentReconciler.Reconcile(
            liveRoot,
            [new ExtensionDeploymentSource("repository:community:first", first, true, true),
             new ExtensionDeploymentSource("repository:community:second", second, true, true)]));

        exception.Message.ShouldContain("repository:community:first");
        exception.Message.ShouldContain("repository:community:second");
        Directory.GetDirectories(liveRoot).ShouldBeEmpty();
    }

    [Fact]
    public void Reconcile_ActivationFailureRestoresLastKnownGood()
    {
        var liveRoot = Directory.CreateDirectory(Path.Combine(_root, "live")).FullName;
        var registered = CreateOutput("community-tools", "community.dll", "last-known-good");
        var source = new ExtensionDeploymentSource("repository:community", registered, true, true);
        ExtensionDeploymentReconciler.Reconcile(liveRoot, [source]).Failures.ShouldBeEmpty();
        File.WriteAllText(Path.Combine(registered, "community.dll"), "candidate");

        var result = ExtensionDeploymentReconciler.Reconcile(
            liveRoot,
            [source],
            (_, _) => throw new IOException("simulated activation failure"));

        result.Failures.Single().Message.ShouldContain("simulated activation failure");
        File.ReadAllText(Path.Combine(liveRoot, "community-tools", "community.dll"))
            .ShouldBe("last-known-good");
    }

    [Fact]
    public void Reconcile_ActivationRaceRestoresLastKnownGoodOverUnexpectedDestination()
    {
        var liveRoot = Directory.CreateDirectory(Path.Combine(_root, "live")).FullName;
        var registered = CreateOutput("community-tools", "community.dll", "last-known-good");
        var source = new ExtensionDeploymentSource("repository:community", registered, true, true);
        ExtensionDeploymentReconciler.Reconcile(liveRoot, [source]).Failures.ShouldBeEmpty();
        File.WriteAllText(Path.Combine(registered, "community.dll"), "candidate");

        var result = ExtensionDeploymentReconciler.Reconcile(
            liveRoot,
            [source],
            (_, destination) =>
            {
                Directory.CreateDirectory(destination);
                File.WriteAllText(Path.Combine(destination, "racer.txt"), "unexpected");
            });

        result.Failures.ShouldHaveSingleItem();
        File.ReadAllText(Path.Combine(liveRoot, "community-tools", "community.dll"))
            .ShouldBe("last-known-good");
        File.Exists(Path.Combine(liveRoot, "community-tools", "racer.txt")).ShouldBeFalse();
    }

    [Theory]
    [InlineData("foo:bar")]
    [InlineData("foo\\bar")]
    [InlineData("foo/bar")]
    [InlineData("Foo")]
    public void Reconcile_NonPortableExtensionIdFailsBeforeLiveMutation(string id)
    {
        var liveRoot = Directory.CreateDirectory(Path.Combine(_root, "live")).FullName;
        var output = CreateOutput(id, "extension.dll", "candidate");

        var exception = Should.Throw<InvalidOperationException>(() => ExtensionDeploymentReconciler.Reconcile(
            liveRoot,
            [new ExtensionDeploymentSource("in-tree:invalid", output, true, false)]));

        exception.Message.ShouldContain("invalid id");
        Directory.GetDirectories(liveRoot).ShouldBeEmpty();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("unknown")]
    public void Reconcile_InvalidExtensionTypesPreservesLastKnownGood(string? extensionType)
    {
        var liveRoot = Directory.CreateDirectory(Path.Combine(_root, "live")).FullName;
        var registered = CreateOutput("community-tools", "community.dll", "last-known-good");
        var source = new ExtensionDeploymentSource("repository:community", registered, true, true);
        ExtensionDeploymentReconciler.Reconcile(liveRoot, [source]).Failures.ShouldBeEmpty();
        WriteManifest(registered, "community-tools", "community.dll", extensionType);

        var result = ExtensionDeploymentReconciler.Reconcile(liveRoot, [source]);

        result.Failures.ShouldHaveSingleItem();
        File.ReadAllText(Path.Combine(liveRoot, "community-tools", "community.dll"))
            .ShouldBe("last-known-good");
    }

    [Fact]
    public void Reconcile_MissingEntryAssemblyPreservesLastKnownGood()
    {
        var liveRoot = Directory.CreateDirectory(Path.Combine(_root, "live")).FullName;
        var registered = CreateOutput("community-tools", "community.dll", "last-known-good");
        var source = new ExtensionDeploymentSource("repository:community", registered, true, true);
        ExtensionDeploymentReconciler.Reconcile(liveRoot, [source]).Failures.ShouldBeEmpty();
        File.Delete(Path.Combine(registered, "community.dll"));

        var result = ExtensionDeploymentReconciler.Reconcile(liveRoot, [source]);

        result.Failures.Single().Message.ShouldContain("community.dll");
        File.ReadAllText(Path.Combine(liveRoot, "community-tools", "community.dll"))
            .ShouldBe("last-known-good");
    }

    private string CreateOutput(string id, string assembly, string contents)
    {
        var directory = Path.Combine(_root, "outputs", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        WriteManifest(directory, id, assembly);
        File.WriteAllText(Path.Combine(directory, assembly), contents);
        return directory;
    }

    private static void WriteManifest(string directory, string id, string assembly, string? extensionType = "tool")
    {
        File.WriteAllText(
            Path.Combine(directory, "botnexus-extension.json"),
            JsonSerializer.Serialize(new
            {
                id,
                name = id,
                version = "1.0.0",
                entryAssembly = assembly,
                extensionTypes = extensionType is null ? Array.Empty<string>() : new[] { extensionType }
            }));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
