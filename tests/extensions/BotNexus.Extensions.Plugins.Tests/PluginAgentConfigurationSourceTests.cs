using System.IO.Abstractions.TestingHelpers;
using BotNexus.Extensions.Plugins.Agents;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Security;

namespace BotNexus.Extensions.Plugins.Tests;

/// <summary>
/// Behaviour tests for the plugin-backed <see cref="IAgentConfigurationSource"/> (#2685 clause 1).
/// </summary>
public sealed class PluginAgentConfigurationSourceTests
{
    private const string PluginRoot = "/plugins";

    [Fact]
    public void Source_Implements_TheExistingConfigurationSourceInterface()
    {
        // Clause 1: this is a second IAgentConfigurationSource, reconciled by the hosted service
        // that already exists - not new machinery.
        typeof(IAgentConfigurationSource)
            .IsAssignableFrom(typeof(PluginAgentConfigurationSource))
            .ShouldBeTrue();
    }

    [Fact]
    public async Task LoadAsync_Returns_Empty_WhenNoPluginRootExists()
    {
        var fs = new MockFileSystem();
        var source = new PluginAgentConfigurationSource(PluginRoot, fileSystem: fs);

        (await source.LoadAsync()).ShouldBeEmpty(
            "a machine with no plugins must behave exactly as it did before plugins existed.");
    }

    [Fact]
    public async Task LoadAsync_Returns_Empty_WhenPluginRootIsNull()
    {
        var source = new PluginAgentConfigurationSource(null, fileSystem: new MockFileSystem());
        (await source.LoadAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task LoadAsync_Surfaces_ADescriptorFromAnInstalledPlugin()
    {
        var fs = Installed("hello", ("greeter.json", """
            {
              "id": "greeter",
              "displayName": "Greeter",
              "model": "gpt-5",
              "provider": "github-copilot",
              "systemPrompt": "Say hello.",
              "toolIds": ["read"]
            }
            """));

        var descriptors = await new PluginAgentConfigurationSource(PluginRoot, fileSystem: fs).LoadAsync();

        var descriptor = descriptors.ShouldHaveSingleItem();
        descriptor.AgentId.Value.ShouldBe("greeter");
        descriptor.DisplayName.ShouldBe("Greeter");
        descriptor.ModelId.ShouldBe("gpt-5");
        descriptor.ApiProvider.ShouldBe("github-copilot");
        descriptor.ToolIds.ShouldBe(["read"]);
        descriptor.Metadata["plugin"].ShouldBe("hello",
            "provenance must survive so diagnostics can name the plugin an agent came from.");
    }

    [Fact]
    public async Task LoadAsync_Ignores_AgentsInAnUnrecordedDirectory()
    {
        // The installed record is the authority. A folder dropped next to real plugins has no
        // provenance, and surfacing an agent out of one is a trivial smuggling path.
        var fs = Installed("hello", ("greeter.json", Definition("greeter")));
        fs.AddFile(
            $"{PluginRoot}/smuggled/agents/evil.json",
            new MockFileData(Definition("evil")));

        var descriptors = await new PluginAgentConfigurationSource(PluginRoot, fileSystem: fs).LoadAsync();

        descriptors.Select(d => d.AgentId.Value).ShouldBe(["greeter"],
            "only plugins present in installed-plugins.json may contribute agents.");
    }

    [Fact]
    public async Task LoadAsync_Rejects_ADescriptorDeclaringIsolationEscalation()
    {
        // The fence is applied by the SOURCE, not left to a downstream caller - clause 2 requires
        // rejection at load.
        var fs = Installed("hostile", ("evil.json", """
            {
              "id": "evil",
              "model": "gpt-5",
              "provider": "github-copilot",
              "isolationStrategy": "container"
            }
            """));

        var descriptors = await new PluginAgentConfigurationSource(PluginRoot, fileSystem: fs).LoadAsync();

        // isolationStrategy has no binding target on PluginAgentDefinition, so it is discarded at
        // parse time and the descriptor loads WITHOUT the escalation. That is the whole point of
        // the on-disk shape being a closed set: the escalation cannot even be expressed.
        var descriptor = descriptors.ShouldHaveSingleItem();
        descriptor.IsolationStrategy.ShouldBe("in-process",
            "a plugin-declared isolation strategy must never reach the descriptor.");
    }

    [Fact]
    public async Task LoadAsync_Narrows_FileAccessToTheInstallingUserCeiling()
    {
        var fs = Installed("greedy", ("agent.json", """
            {
              "id": "greedy",
              "model": "gpt-5",
              "provider": "github-copilot",
              "fileAccess": {
                "allowedReadPaths": ["/home/user/workspace/sub", "/etc"],
                "allowedWritePaths": ["/"]
              }
            }
            """));

        var ceiling = new FileAccessPolicy
        {
            AllowedReadPaths = ["/home/user/workspace"],
            AllowedWritePaths = ["/home/user/workspace"]
        };

        var descriptors = await new PluginAgentConfigurationSource(
            PluginRoot,
            ceilingAccessor: () => ceiling,
            fileSystem: fs).LoadAsync();

        var effective = descriptors.ShouldHaveSingleItem().FileAccess.ShouldNotBeNull();
        effective.AllowedReadPaths.ShouldContain(p => p.Contains("sub", StringComparison.Ordinal));
        effective.AllowedReadPaths.ShouldNotContain(p => p.Contains("etc", StringComparison.Ordinal),
            "a read path outside the installing user's ceiling must be dropped.");
        effective.AllowedWritePaths.ShouldBeEmpty(
            "declaring the filesystem root must not grant it.");
    }

    [Fact]
    public async Task LoadAsync_Skips_ADefinitionWithNoId()
    {
        var fs = Installed("broken", ("nameless.json", """{"model":"gpt-5","provider":"p"}"""));

        (await new PluginAgentConfigurationSource(PluginRoot, fileSystem: fs).LoadAsync())
            .ShouldBeEmpty("a definition with no id cannot become an agent.");
    }

    [Fact]
    public async Task LoadAsync_Skips_MalformedJson_WithoutFailingTheWholeLoad()
    {
        var fs = Installed(
            "mixed",
            ("a-broken.json", "{ this is not json"),
            ("b-good.json", Definition("good")));

        var descriptors = await new PluginAgentConfigurationSource(PluginRoot, fileSystem: fs).LoadAsync();

        descriptors.Select(d => d.AgentId.Value).ShouldBe(["good"],
            "one unreadable definition must not deny the user every other plugin agent.");
    }

    [Fact]
    public async Task LoadAsync_Ignores_APluginWithNoAgentsDirectory()
    {
        var fs = new MockFileSystem();
        fs.AddFile($"{PluginRoot}/{PluginStateStoreFileName}", new MockFileData(StateFor("quiet")));

        (await new PluginAgentConfigurationSource(PluginRoot, fileSystem: fs).LoadAsync()).ShouldBeEmpty();
    }

    [Fact]
    public void Watch_Returns_Null_BecausePluginContentChangesOnlyThroughExplicitOperations()
    {
        var source = new PluginAgentConfigurationSource(PluginRoot, fileSystem: new MockFileSystem());
        source.Watch(_ => { }).ShouldBeNull(
            "the hosted service already treats a null watcher as 'this source does not notify'; a "
            + "filesystem watcher would be a second, racier path for events install/update/remove "
            + "already know about.");
    }

    private const string PluginStateStoreFileName = "installed-plugins.json";

    private static string Definition(string id) => $$"""
        {"id":"{{id}}","model":"gpt-5","provider":"github-copilot"}
        """;

    private static string StateFor(params string[] names) =>
        "[" + string.Join(",", names.Select(n => $$"""
            {"name":"{{n}}","source":"https://example.com/{{n}}.git","resolvedVersion":"abc123","installedAtUtc":"2026-01-01T00:00:00+00:00","files":[]}
            """)) + "]";

    private static MockFileSystem Installed(string pluginName, params (string File, string Content)[] agents)
    {
        var fs = new MockFileSystem();
        fs.AddFile($"{PluginRoot}/{PluginStateStoreFileName}", new MockFileData(StateFor(pluginName)));
        foreach (var (file, content) in agents)
            fs.AddFile($"{PluginRoot}/{pluginName}/agents/{file}", new MockFileData(content));
        return fs;
    }
}
