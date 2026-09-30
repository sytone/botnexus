using System.IO.Abstractions;
using System.IO.Abstractions.TestingHelpers;
using System.Text.Json;
using BotNexus.Extensions.Plugins.Agents;
using BotNexus.Extensions.Plugins.Api;
using BotNexus.Extensions.Plugins.Lifecycle;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Agents;
using BotNexus.Gateway.Configuration;
using BotNexus.Gateway.Extensions;
using BotNexus.Gateway.Hooks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BotNexus.Extensions.Plugins.Tests;

/// <summary>
/// AC1 composition coverage: the installed extension contributes its source to the real gateway
/// container and the existing hosted reconciler registers what that source discovers.
/// </summary>
public sealed class PluginAgentCompositionTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "botnexus-plugin-agent-composition",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task LoadedPluginExtension_HostedReconciliation_RegistersInstalledPluginAgent()
    {
        var dataRoot = Path.Combine(_root, "data");
        var pluginRoot = Path.Combine(dataRoot, PluginSkillRootResolver.PluginRootDirectoryName);
        var extensionRoot = Path.Combine(_root, "extensions");
        var extensionDirectory = Path.Combine(extensionRoot, "botnexus-plugins-api");
        var allowedRoot = Path.Combine(_root, "allowed");
        var allowedChild = Path.Combine(allowedRoot, "child");
        Directory.CreateDirectory(Path.Combine(pluginRoot, "hello", "agents"));
        Directory.CreateDirectory(extensionDirectory);

        await File.WriteAllTextAsync(
            Path.Combine(pluginRoot, PluginStateStore.StateFileName),
            """
            [{"name":"hello","source":"https://example.test/hello.git","resolvedVersion":"abc123","installedAtUtc":"2026-01-01T00:00:00+00:00","files":[]}]
            """);
        await File.WriteAllTextAsync(
            Path.Combine(pluginRoot, "hello", "agents", "greeter.json"),
            JsonSerializer.Serialize(new
            {
                id = "plugin-greeter",
                displayName = "Plugin Greeter",
                model = "gpt-5",
                provider = "github-copilot",
                fileAccess = new
                {
                    allowedReadPaths = new[] { allowedChild, Path.GetPathRoot(allowedRoot)! }
                }
            }));
        CopyPluginExtension(extensionDirectory);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddBotNexusGateway();
        services.Replace(ServiceDescriptor.Singleton<IAgentRegistry, RecordingAgentRegistry>());
        services.AddSingleton(new BotNexusHome(new FileSystem(), Path.Combine(_root, "home"), dataRoot));
        services.AddSingleton<IOptionsMonitor<PlatformConfig>>(new StaticOptionsMonitor<PlatformConfig>(new PlatformConfig
        {
            Gateway = new GatewaySettingsConfig
            {
                FileAccess = new FileAccessPolicyConfig
                {
                    AllowedReadPaths = [allowedRoot]
                }
            }
        }));
        services.AddAgentConfigurationSource<EmptyAgentConfigurationSource>();

        var loader = new AssemblyLoadContextExtensionLoader(
            services,
            new HookDispatcher(),
            NullLogger<AssemblyLoadContextExtensionLoader>.Instance,
            new FileSystem());
        var extension = (await loader.DiscoverAsync(extensionRoot)).ShouldHaveSingleItem();
        var load = await loader.LoadAsync(extension);

        load.Success.ShouldBeTrue(load.Error);
        load.RegisteredServices.ShouldContain(name =>
            name.Contains("IServiceContributor->", StringComparison.Ordinal)
            && name.Contains("PluginAgentServiceContributor", StringComparison.Ordinal));

        var reconcilerDescriptor = services.Single(descriptor =>
            descriptor.ServiceType == typeof(IHostedService)
            && descriptor.ImplementationType?.Name == "AgentConfigurationHostedService");

        await using var provider = services.BuildServiceProvider();
        provider.GetServices<IAgentConfigurationSource>().Count().ShouldBe(2);
        var reconciler = (IHostedService)ActivatorUtilities.CreateInstance(
            provider,
            reconcilerDescriptor.ImplementationType.ShouldNotBeNull());

        await reconciler.StartAsync(CancellationToken.None);

        var descriptor = provider.GetRequiredService<IAgentRegistry>()
            .Get(BotNexus.Domain.Primitives.AgentId.From("plugin-greeter"))
            .ShouldNotBeNull();
        descriptor.DisplayName.ShouldBe("Plugin Greeter");
        descriptor.Metadata["plugin"].ShouldBe("hello");
        descriptor.FileAccess.ShouldNotBeNull().AllowedReadPaths.ShouldBe([allowedChild]);

        await reconciler.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task PluginSelectedPromptFiles_RespectFencedReadDenialsIncludingResolvedVariant()
    {
        var fileSystem = new MockFileSystem();
        var workspacePath = Path.GetFullPath(Path.Combine(_root, "workspace"));
        var allowedPath = Path.Combine(workspacePath, "allowed", "AGENTS.md");
        var deniedVariantPath = Path.Combine(workspacePath, "protected", "SECRET.gpt-5.md");
        fileSystem.AddFile(allowedPath, new MockFileData("ALLOWED PLUGIN PROMPT"));
        fileSystem.AddFile(
            Path.Combine(workspacePath, "protected", "SECRET.md"),
            new MockFileData("BASE PLUGIN PROMPT"));
        fileSystem.AddFile(deniedVariantPath, new MockFileData("DENIED PLUGIN PROMPT"));

        var json = JsonSerializer.Serialize(new
        {
            id = "plugin-policy-probe",
            displayName = "Plugin Policy Probe",
            model = "gpt-5",
            provider = "test-provider",
            systemPromptFiles = new[] { "allowed/AGENTS.md", "protected/SECRET.md" },
            fileAccess = new
            {
                allowedReadPaths = new[] { workspacePath }
            }
        });
        var definition = JsonSerializer.Deserialize<PluginAgentDefinition>(json).ShouldNotBeNull();
        var ceiling = new BotNexus.Gateway.Abstractions.Security.FileAccessPolicy
        {
            AllowedReadPaths = [workspacePath],
            DeniedPaths = [deniedVariantPath]
        };
        var fenced = PluginAgentDescriptorFence.Apply(definition.ToDescriptor("policy-probe"), ceiling);
        fenced.IsAccepted.ShouldBeTrue();

        var prompt = await new WorkspaceContextBuilder(
                new StubWorkspaceManager(workspacePath),
                fileSystem)
            .BuildSystemPromptAsync(fenced.Descriptor.ShouldNotBeNull());

        prompt.ShouldContain("ALLOWED PLUGIN PROMPT");
        prompt.ShouldNotContain("DENIED PLUGIN PROMPT");
        prompt.ShouldNotContain("BASE PLUGIN PROMPT");
    }

    private static void CopyPluginExtension(string destination)
    {
        foreach (var name in new[]
        {
            "BotNexus.Extensions.Plugins.Api.dll",
            "BotNexus.Extensions.Plugins.Api.pdb",
            "BotNexus.Extensions.Plugins.dll",
            "BotNexus.Extensions.Plugins.pdb",
            "botnexus-extension.json"
        })
        {
            var sourcePath = Path.Combine(AppContext.BaseDirectory, name);
            File.Exists(sourcePath).ShouldBeTrue($"plugin extension artifact '{name}' must be present in the test output");
            File.Copy(sourcePath, Path.Combine(destination, name), overwrite: true);
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private sealed class StubWorkspaceManager(string workspacePath) : IAgentWorkspaceManager
    {
        public Task<AgentWorkspace> LoadWorkspaceAsync(string agentName, CancellationToken ct = default)
            => Task.FromResult(new AgentWorkspace(
                agentName,
                Soul: string.Empty,
                Identity: string.Empty,
                User: string.Empty,
                Memory: string.Empty));

        public Task SaveMemoryAsync(string agentName, string content, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task SaveMemoryAsync(
            string agentName,
            string? filePath,
            string content,
            CancellationToken ct = default)
            => Task.CompletedTask;

        public Task SaveMemoryAsync(
            string agentName,
            string? filePath,
            string content,
            string? memoryPathOverride,
            CancellationToken ct = default)
            => Task.CompletedTask;

        public string GetWorkspacePath(string agentName) => workspacePath;
    }

    private sealed class RecordingAgentRegistry : IAgentRegistry
    {
        private readonly Dictionary<string, AgentDescriptor> _descriptors = new(StringComparer.OrdinalIgnoreCase);

        public void Register(AgentDescriptor descriptor) => _descriptors.Add(descriptor.AgentId.Value, descriptor);

        public void Unregister(BotNexus.Domain.Primitives.AgentId agentId) => _descriptors.Remove(agentId.Value);

        public bool Update(BotNexus.Domain.Primitives.AgentId agentId, AgentDescriptor descriptor)
        {
            if (!_descriptors.ContainsKey(agentId.Value))
                return false;

            _descriptors[agentId.Value] = descriptor;
            return true;
        }

        public AgentDescriptor? Get(BotNexus.Domain.Primitives.AgentId agentId) =>
            _descriptors.GetValueOrDefault(agentId.Value);

        public IReadOnlyList<AgentDescriptor> GetAll() => _descriptors.Values.ToArray();

        public bool Contains(BotNexus.Domain.Primitives.AgentId agentId) => _descriptors.ContainsKey(agentId.Value);
    }

    private sealed class EmptyAgentConfigurationSource : IAgentConfigurationSource
    {
        public Task<IReadOnlyList<AgentDescriptor>> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<AgentDescriptor>>([]);

        public IDisposable? Watch(Action<IReadOnlyList<AgentDescriptor>> onChanged) => null;
    }

    private sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;
        public T Get(string? name) => value;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
