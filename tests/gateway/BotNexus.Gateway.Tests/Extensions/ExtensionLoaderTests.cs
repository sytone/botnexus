using System.Runtime.Loader;
using System.Text.Json;
using BotNexus.Gateway.Abstractions.Activity;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Channels;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Extensions;
using BotNexus.Gateway.Abstractions.Hooks;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Agents;
using BotNexus.Gateway.Dispatching;
using BotNexus.Gateway.Extensions;
using BotNexus.Gateway.Hooks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using System.IO.Abstractions;

namespace BotNexus.Gateway.Tests.Extensions;

public sealed class ExtensionLoaderTests : IDisposable
{
    private readonly string _rootPath;

    public ExtensionLoaderTests()
    {
        _rootPath = Path.Combine(Path.GetTempPath(), "botnexus-extension-loader-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_rootPath);
    }

    [Fact]
    public async Task DiscoverAsync_ReturnsValidExtensions_AndSkipsMissingOrInvalidManifests()
    {
        var valid = Path.Combine(_rootPath, "valid");
        Directory.CreateDirectory(valid);
        await File.WriteAllTextAsync(Path.Combine(valid, "botnexus-extension.json"), JsonSerializer.Serialize(new ExtensionManifest
        {
            Id = "valid-extension",
            Name = "Valid",
            Version = "1.0.0",
            EntryAssembly = "entry.dll",
            ExtensionTypes = ["channel"]
        }));
        await File.WriteAllTextAsync(Path.Combine(valid, "entry.dll"), "placeholder");

        var missingManifest = Path.Combine(_rootPath, "missing-manifest");
        Directory.CreateDirectory(missingManifest);

        var invalid = Path.Combine(_rootPath, "invalid");
        Directory.CreateDirectory(invalid);
        await File.WriteAllTextAsync(Path.Combine(invalid, "botnexus-extension.json"), """{"id":"","name":"Broken","version":"1.0.0"}""");

        var loader = CreateLoader(new ServiceCollection());

        var discovered = await loader.DiscoverAsync(_rootPath);

        discovered.ShouldHaveSingleItem();
        discovered[0].Manifest.Id.ShouldBe("valid-extension");
    }

    [Fact]
    public async Task DiscoverAsync_AllowsMediaHandlerExtensionType()
    {
        var mediaHandler = Path.Combine(_rootPath, "media-handler");
        Directory.CreateDirectory(mediaHandler);
        await File.WriteAllTextAsync(Path.Combine(mediaHandler, "entry.dll"), "placeholder");
        await File.WriteAllTextAsync(Path.Combine(mediaHandler, "botnexus-extension.json"), JsonSerializer.Serialize(new ExtensionManifest
        {
            Id = "media-handler-extension",
            Name = "Media Handler",
            Version = "1.0.0",
            EntryAssembly = "entry.dll",
            ExtensionTypes = ["media-handler"]
        }));

        var loader = CreateLoader(new ServiceCollection());

        var discovered = await loader.DiscoverAsync(_rootPath);

        discovered.ShouldContain(x => x.Manifest.Id == "media-handler-extension");
    }

    [Fact]
    public async Task LoadAsync_RegistersDiscoveredTypes_InCollectibleAssemblyLoadContext_AndSupportsUnload()
    {
        var extensionDirectory = Path.Combine(_rootPath, "telegram-extension");
        Directory.CreateDirectory(extensionDirectory);

        var telegramAssembly = ResolveTelegramAssemblyPath();
        var copiedAssemblyName = Path.GetFileName(telegramAssembly);
        File.Copy(telegramAssembly, Path.Combine(extensionDirectory, copiedAssemblyName), overwrite: true);

        await File.WriteAllTextAsync(Path.Combine(extensionDirectory, "botnexus-extension.json"), JsonSerializer.Serialize(new ExtensionManifest
        {
            Id = "telegram-extension",
            Name = "Telegram Extension",
            Version = "1.0.0",
            EntryAssembly = copiedAssemblyName,
            ExtensionTypes = ["channel"]
        }));

        var services = new ServiceCollection();
        var loader = CreateLoader(services);
        var discovered = await loader.DiscoverAsync(_rootPath);

        var result = await loader.LoadAsync(discovered.Single());

        result.Success.ShouldBeTrue();
        result.RegisteredServices.ShouldContain(service => service.StartsWith("IChannelAdapter->", StringComparison.Ordinal));
        var descriptor = services.Single(d => d.ServiceType == typeof(IChannelAdapter));
        descriptor.ImplementationType.ShouldNotBeNull();
        var implementationType = descriptor.ImplementationType ?? throw new InvalidOperationException("Expected implementation type.");
        implementationType.FullName.ShouldNotBeNull();
        var fullName = implementationType.FullName ?? throw new InvalidOperationException("Expected implementation full name.");
        fullName.ShouldContain("TelegramChannelAdapter");

        var loadContext = AssemblyLoadContext.GetLoadContext(implementationType.Assembly);
        loadContext.ShouldNotBeNull();
        var context = loadContext ?? throw new InvalidOperationException("Expected load context.");
        context.IsCollectible.ShouldBeTrue();
        loadContext.ShouldNotBe(AssemblyLoadContext.Default);

        loader.GetLoaded().Where(x => x.ExtensionId == "telegram-extension").ShouldHaveSingleItem();
        await loader.UnloadAsync("telegram-extension");
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        loader.GetLoaded().ShouldBeEmpty();
    }

    [Fact]
    public async Task LoadAsync_WithBadAssembly_ReturnsFailure()
    {
        var extensionDirectory = Path.Combine(_rootPath, "bad-assembly");
        Directory.CreateDirectory(extensionDirectory);

        await File.WriteAllTextAsync(Path.Combine(extensionDirectory, "not-an-assembly.dll"), "this is not a dll");
        await File.WriteAllTextAsync(Path.Combine(extensionDirectory, "botnexus-extension.json"), JsonSerializer.Serialize(new ExtensionManifest
        {
            Id = "bad-assembly",
            Name = "Bad Assembly",
            Version = "1.0.0",
            EntryAssembly = "not-an-assembly.dll",
            ExtensionTypes = ["channel"]
        }));

        var loader = CreateLoader(new ServiceCollection());
        var discovered = await loader.DiscoverAsync(_rootPath);

        var result = await loader.LoadAsync(discovered.Single(x => x.Manifest.Id == "bad-assembly"));

        result.Success.ShouldBeFalse();
        result.Error.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task DiscoverAsync_WithMissingEntryAssembly_SkipsExtension()
    {
        var extensionDirectory = Path.Combine(_rootPath, "missing-entry");
        Directory.CreateDirectory(extensionDirectory);
        await File.WriteAllTextAsync(Path.Combine(extensionDirectory, "botnexus-extension.json"), JsonSerializer.Serialize(new ExtensionManifest
        {
            Id = "missing-entry",
            Name = "Missing Entry",
            Version = "1.0.0",
            EntryAssembly = "missing.dll",
            ExtensionTypes = ["channel"]
        }));

        var loader = CreateLoader(new ServiceCollection());

        var discovered = await loader.DiscoverAsync(_rootPath);

        discovered.ShouldNotContain(x => x.Manifest.Id == "missing-entry");
    }

    [Fact]
    public async Task LoadAsync_SignalRExtension_HubType_ActivatesFromBuiltProvider()
    {
        var extensionDirectory = Path.Combine(_rootPath, "signalr-extension");
        Directory.CreateDirectory(extensionDirectory);
        CopySignalRExtensionArtifacts(extensionDirectory);

        await File.WriteAllTextAsync(Path.Combine(extensionDirectory, "botnexus-extension.json"), JsonSerializer.Serialize(new ExtensionManifest
        {
            Id = "botnexus-signalr",
            Name = "SignalR Channel",
            Version = "1.0.0",
            EntryAssembly = "BotNexus.Extensions.Channels.SignalR.dll",
            ExtensionTypes = ["channel", "endpoint-contributor"]
        }));

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Mock.Of<IAgentSupervisor>());
        services.AddSingleton(Mock.Of<IAgentRegistry>());
        services.AddSingleton(Mock.Of<ISessionStore>());
        services.AddSingleton(Mock.Of<IChannelDispatcher>());
        services.AddSingleton(Mock.Of<IInboundMessageOrchestrator>());
        services.AddSingleton(Mock.Of<IActivityBroadcaster>());
        services.AddSingleton(Mock.Of<ISessionCompactor>());
        services.AddSingleton(Mock.Of<ISessionCompactionCoordinator>());
        services.AddSingleton(Mock.Of<ISessionWarmupService>());
        services.AddSingleton(Mock.Of<IConversationDispatcher>());
        services.AddSingleton(Mock.Of<IConversationRouter>());
        services.AddSingleton<Microsoft.Extensions.Options.IOptionsMonitor<CompactionOptions>>(
            new TestOptionsMonitor<CompactionOptions>(new CompactionOptions()));

        var loader = CreateLoader(services);
        var discovered = await loader.DiscoverAsync(_rootPath);
        var loadResult = await loader.LoadAsync(discovered.Single(x => x.Manifest.Id == "botnexus-signalr"));

        loadResult.Success.ShouldBeTrue(loadResult.Error);

        var endpointContributor = services
            .Where(descriptor => descriptor.ServiceType == typeof(IEndpointContributor))
            .Select(descriptor => descriptor.ImplementationType)
            .LastOrDefault(type => type?.Assembly.GetName().Name == "BotNexus.Extensions.Channels.SignalR");

        endpointContributor.ShouldNotBeNull("dynamic SignalR extension should register endpoint contributors from the extension assembly");

        var hubType = endpointContributor!.Assembly.GetType("BotNexus.Extensions.Channels.SignalR.GatewayHub");
        hubType.ShouldNotBeNull("GatewayHub should be loadable from the dynamic SignalR extension assembly");

        using var provider = services.BuildServiceProvider();

        Should.NotThrow(() => ActivatorUtilities.CreateInstance(provider, hubType!));
    }

    [Fact]
    public async Task LoadAsync_SignalRExtension_RegistersHubAuthorizationPolicyAndUserIdProvider()
    {
        var extensionDirectory = Path.Combine(_rootPath, "signalr-extension");
        Directory.CreateDirectory(extensionDirectory);
        CopySignalRExtensionArtifacts(extensionDirectory);

        await File.WriteAllTextAsync(Path.Combine(extensionDirectory, "botnexus-extension.json"), JsonSerializer.Serialize(new ExtensionManifest
        {
            Id = "botnexus-signalr",
            Name = "SignalR Channel",
            Version = "1.0.0",
            EntryAssembly = "BotNexus.Extensions.Channels.SignalR.dll",
            ExtensionTypes = ["channel", "endpoint-contributor"]
        }));

        var services = new ServiceCollection();
        services.AddLogging();

        var loader = CreateLoader(services);
        var discovered = await loader.DiscoverAsync(_rootPath);
        var loadResult = await loader.LoadAsync(discovered.Single(x => x.Manifest.Id == "botnexus-signalr"));

        loadResult.Success.ShouldBeTrue(loadResult.Error);

        // The claims-based IUserIdProvider must override SignalR's connection-id default.
        var userIdProvider = services
            .Where(descriptor => descriptor.ServiceType == typeof(Microsoft.AspNetCore.SignalR.IUserIdProvider))
            .Select(descriptor => descriptor.ImplementationType)
            .LastOrDefault(type => type?.FullName == "BotNexus.Extensions.Channels.SignalR.ClaimsUserIdProvider");

        userIdProvider.ShouldNotBeNull(
            "SignalR extension load should register ClaimsUserIdProvider as the IUserIdProvider");

        // The hub's [Authorize(Policy = "SignalRHubAuth")] attribute requires the named policy to
        // be registered, otherwise AuthorizationMiddleware throws and negotiate returns HTTP 500.
        using var provider = services.BuildServiceProvider();
        var policyProvider = provider.GetRequiredService<Microsoft.AspNetCore.Authorization.IAuthorizationPolicyProvider>();
        var policy = await policyProvider.GetPolicyAsync("SignalRHubAuth");

        policy.ShouldNotBeNull(
            "SignalR extension load should register the 'SignalRHubAuth' authorization policy required by GatewayHub");
    }

    [Fact]
    public async Task LoadAsync_SkillsExtension_RegistersSkillPromptHookHandler()
    {
        var extensionDirectory = Path.Combine(_rootPath, "skills-extension");
        Directory.CreateDirectory(extensionDirectory);
        CopySkillsExtensionArtifacts(extensionDirectory);

        await File.WriteAllTextAsync(Path.Combine(extensionDirectory, "botnexus-extension.json"), JsonSerializer.Serialize(new ExtensionManifest
        {
            Id = "botnexus-skills",
            Name = "Skills Manager",
            Version = "1.0.0",
            EntryAssembly = "BotNexus.Extensions.Skills.dll",
            ExtensionTypes = ["tool", "command"]
        }));

        var workspacePath = Path.Combine(_rootPath, "workspace");
        Directory.CreateDirectory(workspacePath);

        var services = CreateSkillsServices(workspacePath, CreateDescriptor());
        var loader = CreateLoader(services);
        var discovered = await loader.DiscoverAsync(_rootPath);

        var loadResult = await loader.LoadAsync(discovered.Single(x => x.Manifest.Id == "botnexus-skills"));

        loadResult.Success.ShouldBeTrue(loadResult.Error);
        loadResult.RegisteredServices.ShouldContain(s =>
            s.StartsWith("IHookHandler<BeforePromptBuildEvent,BeforePromptBuildResult>->", StringComparison.Ordinal) &&
            s.Contains("SkillPromptHookHandler", StringComparison.Ordinal));
    }

    [Fact]
    public async Task BuildSystemPromptAsync_WithEnabledSkillsExtension_AppendsSkillsContext()
    {
        var extensionDirectory = Path.Combine(_rootPath, "skills-extension");
        Directory.CreateDirectory(extensionDirectory);
        CopySkillsExtensionArtifacts(extensionDirectory);

        await File.WriteAllTextAsync(Path.Combine(extensionDirectory, "botnexus-extension.json"), JsonSerializer.Serialize(new ExtensionManifest
        {
            Id = "botnexus-skills",
            Name = "Skills Manager",
            Version = "1.0.0",
            EntryAssembly = "BotNexus.Extensions.Skills.dll",
            ExtensionTypes = ["tool", "command"]
        }));

        var workspacePath = Path.Combine(_rootPath, "workspace");
        Directory.CreateDirectory(workspacePath);
        await File.WriteAllTextAsync(Path.Combine(workspacePath, "AGENTS.md"), "AGENTS");

        var userProfile = Path.Combine(_rootPath, "user");
        var skillPath = Path.Combine(userProfile, ".botnexus", "skills", "calendar-interaction");
        Directory.CreateDirectory(skillPath);
        await File.WriteAllTextAsync(Path.Combine(skillPath, "SKILL.md"), """
            ---
            name: calendar-interaction
            description: Calendar triage guidance
            ---
            Use this skill for meeting cleanup.
            """);

        var originalUserProfile = Environment.GetEnvironmentVariable("USERPROFILE");
        var originalHome = Environment.GetEnvironmentVariable("HOME");
        try
        {
            Environment.SetEnvironmentVariable("USERPROFILE", userProfile);
            Environment.SetEnvironmentVariable("HOME", userProfile);

            var descriptor = CreateDescriptor();
            var hookDispatcher = new HookDispatcher();
            var workspaceManager = new StubWorkspaceManager(workspacePath);
            var registry = new StubAgentRegistry(descriptor);
            var services = new ServiceCollection();
            services.AddSingleton<IAgentWorkspaceManager>(workspaceManager);
            services.AddSingleton<IAgentRegistry>(registry);
            var loader = CreateLoader(services, hookDispatcher);
            var discovered = await loader.DiscoverAsync(_rootPath);

            var loadResult = await loader.LoadAsync(discovered.Single(x => x.Manifest.Id == "botnexus-skills"));
            loadResult.Success.ShouldBeTrue(loadResult.Error);

            var builder = new WorkspaceContextBuilder(
                workspaceManager,
                new FileSystem(),
                hookDispatcher);

            var prompt = await builder.BuildSystemPromptAsync(descriptor);

            prompt.ShouldContain("<!-- SKILLS_CONTEXT -->");
            prompt.ShouldContain("## Skills Available (not loaded)");
            prompt.ShouldContain("calendar-interaction");
        }
        finally
        {
            Environment.SetEnvironmentVariable("USERPROFILE", originalUserProfile);
            Environment.SetEnvironmentVariable("HOME", originalHome);
        }
    }

    private static AssemblyLoadContextExtensionLoader CreateLoader(IServiceCollection services)
        => new(services, new HookDispatcher(), NullLogger<AssemblyLoadContextExtensionLoader>.Instance, new FileSystem());

    private static AssemblyLoadContextExtensionLoader CreateLoader(IServiceCollection services, IHookDispatcher hookDispatcher)
        => new(services, hookDispatcher, NullLogger<AssemblyLoadContextExtensionLoader>.Instance, new FileSystem());

    private static string ResolveTelegramAssemblyPath()
    {
        var localCopy = Path.Combine(AppContext.BaseDirectory, "BotNexus.Extensions.Channels.Telegram.dll");
        if (File.Exists(localCopy))
            return localCopy;

        var root = FindRepositoryRoot();
        var fallback = Path.Combine(root, "src", "extensions", "BotNexus.Extensions.Channels.Telegram", "bin", "Debug", "net10.0", "BotNexus.Extensions.Channels.Telegram.dll");
        if (File.Exists(fallback))
            return fallback;

        throw new FileNotFoundException("Unable to locate BotNexus.Extensions.Channels.Telegram.dll for extension loader tests.");
    }

    private static void CopySignalRExtensionArtifacts(string destinationDirectory)
    {
        var sourceDirectory = ResolveSignalRExtensionSourceDirectory();
        var filesToCopy = new[]
        {
            "BotNexus.Extensions.Channels.SignalR.dll",
            "BotNexus.Extensions.Channels.SignalR.pdb",
            "BotNexus.Extensions.Channels.SignalR.deps.json",
            "BotNexus.Gateway.Dispatching.dll",
            "BotNexus.Gateway.Dispatching.pdb",
            "BotNexus.Gateway.Dispatching.deps.json"
        };

        foreach (var fileName in filesToCopy)
        {
            var sourcePath = Path.Combine(sourceDirectory, fileName);
            if (!File.Exists(sourcePath))
                continue;

            File.Copy(sourcePath, Path.Combine(destinationDirectory, fileName), overwrite: true);
        }
    }

    private static void CopySkillsExtensionArtifacts(string destinationDirectory)
    {
        var sourceDirectory = ResolveSkillsExtensionSourceDirectory();
        var filesToCopy = new[]
        {
            "BotNexus.Extensions.Skills.dll",
            "BotNexus.Extensions.Skills.pdb",
            "BotNexus.Extensions.Skills.deps.json"
        };

        foreach (var fileName in filesToCopy)
        {
            var sourcePath = Path.Combine(sourceDirectory, fileName);
            if (!File.Exists(sourcePath))
                continue;

            File.Copy(sourcePath, Path.Combine(destinationDirectory, fileName), overwrite: true);
        }
    }

    private static string ResolveSignalRExtensionSourceDirectory()
    {
        if (File.Exists(Path.Combine(AppContext.BaseDirectory, "BotNexus.Extensions.Channels.SignalR.dll")))
            return AppContext.BaseDirectory;

        var root = FindRepositoryRoot();
        var fallback = Path.Combine(root, "src", "extensions", "BotNexus.Extensions.Channels.SignalR", "bin", "Debug", "net10.0");
        if (File.Exists(Path.Combine(fallback, "BotNexus.Extensions.Channels.SignalR.dll")))
            return fallback;

        throw new DirectoryNotFoundException("Unable to locate SignalR extension build output for extension loader tests.");
    }

    private static string ResolveSkillsExtensionSourceDirectory()
    {
        if (File.Exists(Path.Combine(AppContext.BaseDirectory, "BotNexus.Extensions.Skills.dll")))
            return AppContext.BaseDirectory;

        var root = FindRepositoryRoot();
        var fallback = Path.Combine(root, "src", "extensions", "BotNexus.Extensions.Skills", "bin", "Debug", "net10.0");
        if (File.Exists(Path.Combine(fallback, "BotNexus.Extensions.Skills.dll")))
            return fallback;

        throw new DirectoryNotFoundException("Unable to locate Skills extension build output for extension loader tests.");
    }

    private static ServiceCollection CreateSkillsServices(string workspacePath, AgentDescriptor descriptor)
    {
        var workspaceManager = new StubWorkspaceManager(workspacePath);
        var registry = new StubAgentRegistry(descriptor);
        var services = new ServiceCollection();
        services.AddSingleton<IAgentWorkspaceManager>(workspaceManager);
        services.AddSingleton<IAgentRegistry>(registry);
        return services;
    }

    private static AgentDescriptor CreateDescriptor() => new()
    {
        AgentId = BotNexus.Domain.Primitives.AgentId.From("farnsworth"),
        DisplayName = "Farnsworth",
        ModelId = "test-model",
        ApiProvider = "test-provider",
        ExtensionConfig = new Dictionary<string, JsonElement>
        {
            ["botnexus-skills"] = JsonSerializer.SerializeToElement(new { enabled = true })
        }
    };

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "BotNexus.slnx")))
                return current.FullName;
            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Repository root could not be resolved from test base path.");
    }

    private sealed class StubWorkspaceManager(string workspacePath) : IAgentWorkspaceManager
    {
        public Task<AgentWorkspace> LoadWorkspaceAsync(string agentName, CancellationToken ct = default)
            => Task.FromResult(new AgentWorkspace(agentName, Soul: string.Empty, Identity: string.Empty, User: string.Empty, Memory: string.Empty));

        public Task SaveMemoryAsync(string agentName, string content, CancellationToken ct = default) => Task.CompletedTask;

        public Task SaveMemoryAsync(string agentName, string? filePath, string content, CancellationToken ct = default) => Task.CompletedTask;

        public Task SaveMemoryAsync(string agentName, string? filePath, string content, string? memoryPathOverride, CancellationToken ct = default) => Task.CompletedTask;

        public string GetWorkspacePath(string agentName) => workspacePath;
    }

    private sealed class StubAgentRegistry(AgentDescriptor descriptor) : IAgentRegistry
    {
        public void Register(AgentDescriptor descriptor) => throw new NotSupportedException();

        public void Unregister(BotNexus.Domain.Primitives.AgentId agentId) => throw new NotSupportedException();

        public bool Update(BotNexus.Domain.Primitives.AgentId agentId, AgentDescriptor descriptor) => throw new NotSupportedException();

        public AgentDescriptor? Get(BotNexus.Domain.Primitives.AgentId agentId)
            => descriptor.AgentId == agentId ? descriptor : null;

        public IReadOnlyList<AgentDescriptor> GetAll() => [descriptor];

        public bool Contains(BotNexus.Domain.Primitives.AgentId agentId) => descriptor.AgentId == agentId;
    }

    public void Dispose()
    {
        if (!Directory.Exists(_rootPath))
            return;

        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                Directory.Delete(_rootPath, recursive: true);
                return;
            }
            catch (UnauthorizedAccessException)
            {
                if (attempt == 4)
                    return;
                Thread.Sleep(100);
            }
            catch (IOException)
            {
                if (attempt == 4)
                    return;
                Thread.Sleep(100);
            }
        }
    }
}
