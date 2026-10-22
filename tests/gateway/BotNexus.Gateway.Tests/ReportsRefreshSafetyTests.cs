using System.IO.Abstractions;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Api.Controllers;
using BotNexus.Gateway.Configuration;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace BotNexus.Gateway.Tests;

public sealed class ReportsRefreshSafetyTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Reports_WorkspaceRefreshContentionReturnsControlled503(bool single)
    {
        var registry = Substitute.For<IAgentRegistry>();
        registry.Contains(AgentId.From("nexus-trailguide")).Returns(true);
        var workspace = Substitute.For<IAgentWorkspaceManager>();
        workspace.GetWorkspacePath("nexus-trailguide").Returns(_ => throw new TrailguideRefreshException("private path", new IOException("locked")));
        var controller = new ReportsController(registry, workspace, new FileSystem(), Options.Create(new PlatformConfig()));
        var result = single ? controller.GetReport("nexus-trailguide", "daily.md").Result : controller.GetReports("nexus-trailguide").Result;
        result.ShouldBeOfType<StatusCodeResult>().StatusCode.ShouldBe(503);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Reports_RealWorkspaceLockedSoulHasControlledPlatformOutcome(bool single)
    {
        var root = Path.Combine(Environment.CurrentDirectory, "artifacts", "tmp", "reports-safety-" + Guid.NewGuid().ToString("N"));
        try
        {
            var fs = new FileSystem();
            var home = new BotNexusHome(fs, root, root);
            var workspace = new BotNexus.Gateway.Agents.FileAgentWorkspaceManager(home, fs);
            var path = workspace.GetWorkspacePath("nexus-trailguide");
            Directory.CreateDirectory(Path.Combine(path, "reports"));
            File.WriteAllText(Path.Combine(path, "reports", "daily.md"), "daily report");
            var soul = Path.Combine(path, "SOUL.md");
            File.WriteAllText(soul, "old soul");
            var registry = Substitute.For<IAgentRegistry>();
            registry.Contains(AgentId.From("nexus-trailguide")).Returns(true);
            var controller = new ReportsController(registry, workspace, fs, Options.Create(new PlatformConfig()));
            using (var held = new FileStream(soul, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var result = single ? controller.GetReport("nexus-trailguide", "daily.md").Result : controller.GetReports("nexus-trailguide").Result;
                if (OperatingSystem.IsWindows())
                {
                    result.ShouldBeOfType<StatusCodeResult>().StatusCode.ShouldBe(503);
                    File.ReadAllText(soul).ShouldBe("old soul");
                }
                else
                {
                    result.ShouldBeOfType<OkObjectResult>();
                }
            }
            var recovered = single ? controller.GetReport("nexus-trailguide", "daily.md").Result : controller.GetReports("nexus-trailguide").Result;
            recovered.ShouldBeOfType<OkObjectResult>();
            Directory.EnumerateFiles(path, "*.tmp", SearchOption.AllDirectories).ShouldBeEmpty();
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Reports_QueuedBehindPublicationReturnsBounded503(bool single)
    {
        var root = Path.Combine(Environment.CurrentDirectory, "artifacts", "tmp", "reports-queue-" + Guid.NewGuid().ToString("N"));
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        Task<string>? first = null;
        Task<ActionResult?>? queued = null;
        try
        {
            var fs = new PublicationFileSystem();
            var home = new BotNexusHome(fs, root, root);
            var workspace = new BotNexus.Gateway.Agents.FileAgentWorkspaceManager(home, fs);
            var path = workspace.GetWorkspacePath("nexus-trailguide");
            File.WriteAllText(Path.Combine(path, "SOUL.md"), "complete old soul");
            fs.BeforeReplace = destination =>
            {
                if (!destination.EndsWith("SOUL.md", StringComparison.Ordinal)) return;
                entered.Set();
                release.Wait(TimeSpan.FromSeconds(10)).ShouldBeTrue();
            };
            var registry = Substitute.For<IAgentRegistry>();
            registry.Contains(AgentId.From("nexus-trailguide")).Returns(true);
            var controller = new ReportsController(registry, workspace, fs, Options.Create(new PlatformConfig()));
            first = Task.Factory.StartNew(() => workspace.GetWorkspacePath("nexus-trailguide"), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            entered.Wait(TimeSpan.FromSeconds(5)).ShouldBeTrue();
            queued = Task.Factory.StartNew<ActionResult?>(() => single ? controller.GetReport("nexus-trailguide", "daily.md").Result : controller.GetReports("nexus-trailguide").Result, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            var result = await queued.WaitAsync(TimeSpan.FromSeconds(3));
            result.ShouldBeOfType<StatusCodeResult>().StatusCode.ShouldBe(503);
            File.ReadAllText(Path.Combine(path, "SOUL.md")).ShouldBe("complete old soul");
        }
        finally
        {
            release.Set();
            if (first is not null) await first.WaitAsync(TimeSpan.FromSeconds(5));
            if (queued is not null) await queued.WaitAsync(TimeSpan.FromSeconds(5));
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Reports_ConcurrentResolutionUnderRepeatedRealLockPreservesBytesAndRecovers(bool single)
    {
        var root = Path.Combine(Environment.CurrentDirectory, "artifacts", "tmp", "reports-concurrent-" + Guid.NewGuid().ToString("N"));
        try
        {
            var fs = new FileSystem();
            var workspace = new BotNexus.Gateway.Agents.FileAgentWorkspaceManager(new BotNexusHome(fs, root, root), fs);
            var other = new BotNexus.Gateway.Agents.FileAgentWorkspaceManager(new BotNexusHome(fs, root, root), fs);
            var path = workspace.GetWorkspacePath("nexus-trailguide");
            var managed = new[] { "AGENTS.md", "SOUL.md", "skills/trailguide-documentation/SKILL.md", "skills/trailguide-troubleshooting/SKILL.md", "DOCUMENTATION_ROOT.md" };
            var canonical = managed.ToDictionary(file => file, file => File.ReadAllBytes(Path.Combine(path, file)));
            var custom = Path.Combine(path, "TRAILGUIDE.custom.md");
            byte[] customBytes = [0xEF, 0xBB, 0xBF, 0x23, 0x20, 0x63, 0x0D, 0x0A];
            File.WriteAllBytes(custom, customBytes);
            Directory.CreateDirectory(Path.Combine(path, "reports"));
            var report = Path.Combine(path, "reports", "daily.md");
            File.WriteAllText(report, "daily report");
            var reportBytes = File.ReadAllBytes(report);
            var registry = Substitute.For<IAgentRegistry>();
            registry.Contains(AgentId.From("nexus-trailguide")).Returns(true);
            var controller = new ReportsController(registry, workspace, fs, Options.Create(new PlatformConfig()));
            for (var round = 0; round < 3; round++)
            {
                var soul = Path.Combine(path, "SOUL.md");
                File.WriteAllText(soul, "complete old soul");
                using (var held = new FileStream(soul, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var ready = new CountdownEvent(2))
                using (var start = new ManualResetEventSlim())
                {
                    var reports = Task.Factory.StartNew(() =>
                    {
                        ready.Signal();
                        start.Wait(TimeSpan.FromSeconds(5)).ShouldBeTrue();
                        return single ? controller.GetReport("nexus-trailguide", "daily.md").Result : controller.GetReports("nexus-trailguide").Result;
                    }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
                    var resolution = Task.Factory.StartNew(() =>
                    {
                        ready.Signal();
                        start.Wait(TimeSpan.FromSeconds(5)).ShouldBeTrue();
                        return Record.Exception(() => other.GetWorkspacePath("nexus-trailguide"));
                    }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
                    try
                    {
                        ready.Wait(TimeSpan.FromSeconds(5)).ShouldBeTrue();
                        start.Set();
                        await Task.WhenAll(reports, resolution).WaitAsync(TimeSpan.FromSeconds(3));
                        var result = await reports;
                        var error = await resolution;
                        if (OperatingSystem.IsWindows())
                        {
                            result.ShouldBeOfType<StatusCodeResult>().StatusCode.ShouldBe(503);
                            error.ShouldBeOfType<TrailguideRefreshException>();
                            File.ReadAllText(soul).ShouldBe("complete old soul");
                        }
                        else
                        {
                            result.ShouldBeOfType<OkObjectResult>();
                            error.ShouldBeNull();
                            File.ReadAllBytes(soul).ShouldBe(canonical["SOUL.md"]);
                        }
                        File.ReadAllBytes(custom).ShouldBe(customBytes);
                        File.ReadAllBytes(report).ShouldBe(reportBytes);
                        Directory.EnumerateFiles(path, "*.tmp", SearchOption.AllDirectories).ShouldBeEmpty();
                    }
                    finally
                    {
                        start.Set();
                        await Task.WhenAll(reports, resolution).WaitAsync(TimeSpan.FromSeconds(5));
                    }
                }
                var recovered = single ? controller.GetReport("nexus-trailguide", "daily.md").Result : controller.GetReports("nexus-trailguide").Result;
                recovered.ShouldBeOfType<OkObjectResult>();
                foreach (var file in managed) File.ReadAllBytes(Path.Combine(path, file)).ShouldBe(canonical[file]);
                File.ReadAllBytes(custom).ShouldBe(customBytes);
                File.ReadAllBytes(report).ShouldBe(reportBytes);
                Directory.EnumerateFiles(path, "*.tmp", SearchOption.AllDirectories).ShouldBeEmpty();
            }
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private sealed class PublicationFileSystem : FileSystem
    {
        private readonly IFile _file;
        public PublicationFileSystem() => _file = new PublicationFile(this);
        public override IFile File => _file;
        public Action<string>? BeforeReplace { get; set; }
        private sealed class PublicationFile(PublicationFileSystem owner) : FileWrapper(owner)
        {
            public override void Replace(string source, string destination, string? destinationBackupFileName)
            {
                owner.BeforeReplace?.Invoke(destination);
                base.Replace(source, destination, destinationBackupFileName);
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Reports_UnrelatedWorkspaceIOExceptionIsNotSuppressed(bool single)
    {
        var registry = Substitute.For<IAgentRegistry>();
        registry.Contains(AgentId.From("nexus-trailguide")).Returns(true);
        var workspace = Substitute.For<IAgentWorkspaceManager>();
        workspace.GetWorkspacePath("nexus-trailguide").Returns(_ => throw new IOException("unrelated failure"));
        var controller = new ReportsController(registry, workspace, new FileSystem(), Options.Create(new PlatformConfig()));
        var error = Should.Throw<IOException>(() =>
        {
            if (single) controller.GetReport("nexus-trailguide", "daily.md");
            else controller.GetReports("nexus-trailguide");
        });
        error.Message.ShouldBe("unrelated failure");
    }
}
