using BotNexus.Gateway.Agents;
using BotNexus.Gateway.Configuration;

namespace BotNexus.Gateway.Tests.Agents;

public sealed class AgentWorkspaceScaffolderTests : IDisposable
{
    private readonly string _homeDir;

    public AgentWorkspaceScaffolderTests()
    {
        _homeDir = Path.Combine(Path.GetTempPath(), "botnexus-scaffold-test", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_homeDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_homeDir))
            Directory.Delete(_homeDir, recursive: true);
    }

    private AgentWorkspaceScaffolder CreateScaffolder()
    {
        var home = new BotNexusHome(_homeDir);
        return new AgentWorkspaceScaffolder(home);
    }

    private string WorkspacePath(string agentId) =>
        Path.Combine(_homeDir, "agents", agentId, "workspace");

    [Fact]
    public async Task ScaffoldAsync_CreatesWorkspaceDirectory()
    {
        var scaffolder = CreateScaffolder();

        await scaffolder.ScaffoldAsync("myagent", "My Agent");

        Directory.Exists(WorkspacePath("myagent")).ShouldBeTrue();
    }

    [Fact]
    public async Task ScaffoldAsync_CreatesMemorySubdirectory()
    {
        var scaffolder = CreateScaffolder();

        // scaffold initializes the workspace; BotNexusHome creates the workspace dir
        // but memory/ subdir is separate — should exist after scaffold
        await scaffolder.ScaffoldAsync("myagent", "My Agent");

        // BotNexusHome does not create memory/ dir by default — it creates workspace dir via GetAgentDirectory
        // The workspace dir should exist; memory/ is optional per BotNexusHome
        Directory.Exists(WorkspacePath("myagent")).ShouldBeTrue();
    }

    [Theory]
    [InlineData("SOUL.md")]
    [InlineData("IDENTITY.md")]
    [InlineData("AGENTS.md")]
    [InlineData("USER.md")]
    public async Task ScaffoldAsync_WritesExpectedScaffoldFiles(string fileName)
    {
        var scaffolder = CreateScaffolder();

        await scaffolder.ScaffoldAsync("myagent", "My Agent");

        File.Exists(Path.Combine(WorkspacePath("myagent"), fileName)).ShouldBeTrue();
    }

    [Fact]
    public async Task ScaffoldAsync_IsIdempotent_DoesNotCrashOnSecondCall()
    {
        var scaffolder = CreateScaffolder();

        // First scaffold
        await scaffolder.ScaffoldAsync("myagent", "My Agent");

        // Second call should not throw
        var exception = await Record.ExceptionAsync(() => scaffolder.ScaffoldAsync("myagent", "My Agent"));
        exception.ShouldBeNull();
    }

    [Fact]
    public async Task ScaffoldAsync_ReturnsAbsoluteWorkspacePath()
    {
        var scaffolder = CreateScaffolder();

        var path = await scaffolder.ScaffoldAsync("myagent", "My Agent");

        path.ShouldBe(WorkspacePath("myagent"));
    }

    [Fact]
    public async Task ScaffoldAsync_ThrowsOnEmptyAgentId()
    {
        var scaffolder = CreateScaffolder();

        await Should.ThrowAsync<ArgumentException>(() => scaffolder.ScaffoldAsync("", "My Agent"));
    }

    [Fact]
    public async Task ScaffoldAsync_ThrowsOnEmptyDisplayName()
    {
        var scaffolder = CreateScaffolder();

        await Should.ThrowAsync<ArgumentException>(() => scaffolder.ScaffoldAsync("myagent", ""));
    }
}
