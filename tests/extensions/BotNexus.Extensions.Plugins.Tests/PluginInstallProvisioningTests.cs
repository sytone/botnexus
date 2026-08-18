using BotNexus.Cron;
using BotNexus.Domain.Primitives;
using BotNexus.Extensions.Plugins.Cron;
using BotNexus.Extensions.Plugins.Lifecycle;

namespace BotNexus.Extensions.Plugins.Tests;

/// <summary>
/// Pins the seam that makes AC2 of #2683 true end-to-end: the platform-wide plugin-update job is
/// provisioned <b>by the act of installing a plugin</b>, not by a startup pass.
/// </summary>
/// <remarks>
/// <para>
/// Provisioning at startup would be wrong for the same reason the issue rejects a startup update
/// check: a gateway that never restarts never provisions, and every test restarts the process so
/// the defect would pass CI. Tying provisioning to install means the job exists exactly when
/// there is something for it to update, and never before.
/// </para>
/// <para>
/// The observer is optional so the lifecycle manager remains usable - and testable - with no cron
/// infrastructure at all. That is deliberate: a plugin extension that could not be constructed
/// without a cron store would force every consumer to take a dependency it does not need.
/// </para>
/// </remarks>
public sealed class PluginInstallProvisioningTests : IDisposable
{
    private readonly string _root;
    private readonly FakePluginSourceFetcher _fetcher = new();
    private readonly PluginStateStore _store;
    private readonly FakeCronStore _cronStore = new();
    private readonly PluginLifecycleManager _manager;

    public PluginInstallProvisioningTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "botnexus-plugin-cron-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _store = new PluginStateStore(_root);
        _manager = new PluginLifecycleManager(
            _store,
            _fetcher,
            installObserver: new PluginUpdateCronProvisioner(_cronStore));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // A leaked temp directory is not worth failing a test over.
        }
    }

    private static Dictionary<string, string> PluginContent(string name) => new(StringComparer.Ordinal)
    {
        [".botnexus-plugin/plugin.json"] = $$"""{ "name": "{{name}}" }""",
    };

    // AC2 - installing the first plugin provisions the platform-wide job with AgentId = null.
    [Fact]
    public async Task FirstInstallProvisionsThePlatformWideJob()
    {
        var jobId = JobId.From(PluginUpdateCronProvisioner.PlatformJobId);
        Assert.Null(await _cronStore.GetAsync(jobId));

        _fetcher.Enqueue("a1b2c3d4", PluginContent("alpha"));
        var result = await _manager.InstallAsync(new PluginInstallRequest
        {
            Source = "https://example.com/alpha.git",
        });

        Assert.Equal(PluginOperationOutcome.Installed, result.Outcome);

        var job = await _cronStore.GetAsync(jobId);
        Assert.NotNull(job);
        Assert.Null(job!.AgentId);
        Assert.Equal(1, _cronStore.CreateCallCount);
    }

    // AC3 at the install seam - a second install does not re-provision over the existing job.
    [Fact]
    public async Task ASecondInstallDoesNotReprovisionTheJob()
    {
        _fetcher.Enqueue("a1b2c3d4", PluginContent("alpha"));
        await _manager.InstallAsync(new PluginInstallRequest { Source = "https://example.com/alpha.git" });

        var jobId = JobId.From(PluginUpdateCronProvisioner.PlatformJobId);
        var provisioned = await _cronStore.GetAsync(jobId);
        Assert.NotNull(provisioned);
        await _cronStore.UpdateDefinitionAsync(provisioned! with { Schedule = "0 6 * * 1" });

        _fetcher.Enqueue("e5f6a7b8", PluginContent("beta"));
        await _manager.InstallAsync(new PluginInstallRequest { Source = "https://example.com/beta.git" });

        var after = await _cronStore.GetAsync(jobId);
        Assert.Equal("0 6 * * 1", after!.Schedule);
        Assert.Equal(1, _cronStore.CreateCallCount);
    }

    // A failed install must not provision the job: there is nothing installed for it to update,
    // and a job left behind by a failure would run forever over an empty plugin set.
    [Fact]
    public async Task AFailedInstallDoesNotProvisionTheJob()
    {
        _fetcher.EnqueueFaulting(PluginContent("alpha"), faultAfterFiles: 0);

        var result = await _manager.InstallAsync(new PluginInstallRequest
        {
            Source = "https://example.com/alpha.git",
        });

        Assert.Equal(PluginOperationOutcome.Failed, result.Outcome);
        Assert.Null(await _cronStore.GetAsync(JobId.From(PluginUpdateCronProvisioner.PlatformJobId)));
        Assert.Equal(0, _cronStore.CreateCallCount);
    }
}
