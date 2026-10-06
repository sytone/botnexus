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
    public void Reconcile_LaterActivationFailureIsolatedAndRestoresThatSourcesPriorLiveTree()
    {
        var liveRoot = Directory.CreateDirectory(Path.Combine(_root, "live")).FullName;
        var first = CreateOutput("first-tools", "first.dll", "first-old");
        var second = CreateOutput("second-tools", "second.dll", "second-old");
        var sources = new[]
        {
            new ExtensionDeploymentSource("repository:community:first", first, true, true),
            new ExtensionDeploymentSource("repository:community:second", second, true, true)
        };
        ExtensionDeploymentReconciler.Reconcile(liveRoot, sources).Failures.ShouldBeEmpty();
        File.WriteAllText(Path.Combine(first, "first.dll"), "first-new");
        File.WriteAllText(Path.Combine(second, "second.dll"), "second-new");

        var result = ExtensionDeploymentReconciler.Reconcile(
            liveRoot,
            sources,
            new ExtensionDeploymentHooks
            {
                BeforeOperation = (operation, _, destination) =>
                {
                    if (operation == ExtensionDeploymentOperation.Activate
                        && destination!.EndsWith("second-tools", StringComparison.Ordinal))
                        throw new IOException("later activation failed");
                },
                IsWindows = () => false
            });

        result.Failures.ShouldHaveSingleItem().Message.ShouldContain("later activation failed");
        File.ReadAllText(Path.Combine(liveRoot, "first-tools", "first.dll")).ShouldBe("first-new");
        File.ReadAllText(Path.Combine(liveRoot, "second-tools", "second.dll")).ShouldBe("second-old");
    }

    [Fact]
    public void Reconcile_TransientActivationLockRetriesAndPublishesOneCompleteNestedCandidate()
    {
        var liveRoot = Directory.CreateDirectory(Path.Combine(_root, "live")).FullName;
        var output = CreateOutput("audio-transcription", "audio.dll", "managed");
        var nativeDirectory = Path.Combine(output, "runtimes", "win-x64", "native");
        Directory.CreateDirectory(nativeDirectory);
        File.WriteAllText(Path.Combine(nativeDirectory, "whisper.dll"), "native");
        var activationAttempts = 0;
        var hooks = new ExtensionDeploymentHooks
        {
            BeforeOperation = (operation, _, _) =>
            {
                if (operation == ExtensionDeploymentOperation.Activate && activationAttempts++ == 0)
                    throw SharingViolation();
            },
            IsWindows = () => true,
            Delay = _ => { }
        };

        var result = ExtensionDeploymentReconciler.Reconcile(
            liveRoot,
            [new ExtensionDeploymentSource("in-tree:audio-transcription", output, true, false)],
            hooks);

        result.Failures.ShouldBeEmpty();
        activationAttempts.ShouldBe(2);
        Directory.GetDirectories(liveRoot, "audio-transcription", SearchOption.TopDirectoryOnly).Length.ShouldBe(1);
        File.ReadAllText(Path.Combine(liveRoot, "audio-transcription", "audio.dll")).ShouldBe("managed");
        File.ReadAllText(Path.Combine(liveRoot, "audio-transcription", "runtimes", "win-x64", "native", "whisper.dll"))
            .ShouldBe("native");
        Directory.GetDirectories(liveRoot, ".deploy-*", SearchOption.TopDirectoryOnly).ShouldBeEmpty();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Reconcile_PersistentActivationLockRestoresPriorBytesAndReportsEverySource(bool registered)
    {
        var liveRoot = Directory.CreateDirectory(Path.Combine(_root, "live")).FullName;
        var output = CreateOutput("audio-transcription", "audio.dll", "last-known-good");
        var sourceName = registered ? "repository:audio" : "in-tree:audio-transcription";
        var source = new ExtensionDeploymentSource(sourceName, output, true, registered);
        ExtensionDeploymentReconciler.Reconcile(liveRoot, [source]).Failures.ShouldBeEmpty();
        var priorBytes = File.ReadAllBytes(Path.Combine(liveRoot, "audio-transcription", "audio.dll"));
        File.WriteAllText(Path.Combine(output, "audio.dll"), "candidate");
        var hooks = new ExtensionDeploymentHooks
        {
            BeforeOperation = (operation, _, _) =>
            {
                if (operation == ExtensionDeploymentOperation.Activate)
                    throw SharingViolation();
            },
            IsWindows = () => true,
            Delay = _ => { }
        };

        var result = ExtensionDeploymentReconciler.Reconcile(liveRoot, [source], hooks);

        var failure = result.Failures.ShouldHaveSingleItem();
        failure.Source.ShouldBe(sourceName);
        failure.Message.ShouldContain("activation");
        File.ReadAllBytes(Path.Combine(liveRoot, "audio-transcription", "audio.dll")).ShouldBe(priorBytes);
    }

    [Fact]
    public void Reconcile_PersistentRollbackDeleteFailureReportsAndRetainsBackup()
    {
        var liveRoot = Directory.CreateDirectory(Path.Combine(_root, "live")).FullName;
        var output = CreateOutput("audio-transcription", "audio.dll", "last-known-good");
        var source = new ExtensionDeploymentSource("in-tree:audio-transcription", output, true, false);
        ExtensionDeploymentReconciler.Reconcile(liveRoot, [source]).Failures.ShouldBeEmpty();
        File.WriteAllText(Path.Combine(output, "audio.dll"), "candidate");
        var hooks = new ExtensionDeploymentHooks
        {
            BeforeOperation = (operation, _, destination) =>
            {
                if (operation == ExtensionDeploymentOperation.Activate && destination is not null)
                {
                    Directory.CreateDirectory(destination);
                    File.WriteAllText(Path.Combine(destination, "partial.txt"), "partial");
                    throw SharingViolation();
                }
                if (operation == ExtensionDeploymentOperation.Rollback)
                    throw SharingViolation();
            },
            IsWindows = () => true,
            Delay = _ => { }
        };

        var result = ExtensionDeploymentReconciler.Reconcile(liveRoot, [source], hooks);

        var failure = result.Failures.ShouldHaveSingleItem();
        failure.Message.ShouldContain("retained backup");
        var backup = Directory.GetDirectories(liveRoot, ".deploy-*-backup").ShouldHaveSingleItem();
        File.ReadAllText(Path.Combine(backup, "audio.dll")).ShouldBe("last-known-good");
    }

    [Fact]
    public void Reconcile_FirstActivationFailureDoesNotClaimPriorDeploymentWasRestored()
    {
        var liveRoot = Directory.CreateDirectory(Path.Combine(_root, "live")).FullName;
        var output = CreateOutput("audio-transcription", "audio.dll", "candidate");
        var hooks = new ExtensionDeploymentHooks
        {
            BeforeOperation = (operation, _, _) =>
            {
                if (operation == ExtensionDeploymentOperation.Activate)
                    throw SharingViolation();
            },
            IsWindows = () => true,
            Delay = _ => { }
        };

        var result = ExtensionDeploymentReconciler.Reconcile(
            liveRoot,
            [new ExtensionDeploymentSource("in-tree:audio-transcription", output, true, false)],
            hooks);

        var failure = result.Failures.ShouldHaveSingleItem();
        failure.Message.ShouldContain("no prior deployment existed");
        failure.Message.ShouldNotContain("prior deployment was restored");
    }

    [Fact]
    public void Reconcile_ExhaustedCleanupReportsRetainedDeployResidue()
    {
        var liveRoot = Directory.CreateDirectory(Path.Combine(_root, "live")).FullName;
        var output = CreateOutput("audio-transcription", "audio.dll", "candidate");
        var cleanupAttempts = 0;
        var hooks = new ExtensionDeploymentHooks
        {
            BeforeOperation = (operation, _, _) =>
            {
                if (operation == ExtensionDeploymentOperation.Cleanup)
                {
                    cleanupAttempts++;
                    throw SharingViolation();
                }
            },
            IsWindows = () => true,
            Delay = _ => { }
        };

        var result = ExtensionDeploymentReconciler.Reconcile(
            liveRoot,
            [new ExtensionDeploymentSource("in-tree:audio-transcription", output, true, false)],
            hooks);

        var failure = result.Failures.ShouldHaveSingleItem();
        cleanupAttempts.ShouldBe(4);
        failure.Message.ShouldContain("retained deployment residue");
        failure.Message.ShouldContain(".deploy-");
    }

    [Fact]
    public void Reconcile_WhenAnotherProcessOwnsLockReturnsControlledFailureBeforeMutation()
    {
        var liveRoot = Directory.CreateDirectory(Path.Combine(_root, "live")).FullName;
        var output = CreateOutput("audio-transcription", "audio.dll", "candidate");
        using var heldLock = new FileStream(
            Path.Combine(liveRoot, ".deployment.lock"),
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None);

        var result = ExtensionDeploymentReconciler.Reconcile(
            liveRoot,
            [new ExtensionDeploymentSource("in-tree:audio-transcription", output, true, false)]);

        result.DeployedCount.ShouldBe(0);
        result.Failures.ShouldHaveSingleItem().Message.ShouldContain("already in progress");
        Directory.Exists(Path.Combine(liveRoot, "audio-transcription")).ShouldBeFalse();
    }

    private static IOException SharingViolation()
        => new("simulated sharing violation", unchecked((int)0x80070020));

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
            new ExtensionDeploymentHooks
            {
                BeforeOperation = (operation, _, _) =>
                {
                    if (operation == ExtensionDeploymentOperation.Activate)
                        throw new IOException("simulated activation failure");
                }
            });

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
            new ExtensionDeploymentHooks
            {
                BeforeOperation = (operation, _, destination) =>
                {
                    if (operation != ExtensionDeploymentOperation.Activate || destination is null)
                        return;
                    Directory.CreateDirectory(destination);
                    File.WriteAllText(Path.Combine(destination, "racer.txt"), "unexpected");
                }
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
