using System.Diagnostics;
using System.Reflection;
using System.Runtime.Loader;
using BotNexus.Core.Abstractions;
using BotNexus.Core.Extensions;
using BotNexus.Tests.Extensions.Convention;
using BotNexus.Tests.Extensions.Registrar;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace BotNexus.Tests.Unit.Tests;

public class ExtensionLoaderTests : IDisposable
{
    private readonly string _testRoot;
    private readonly string _outsideRoot;

    public ExtensionLoaderTests()
    {
        _testRoot = Path.Combine(AppContext.BaseDirectory, "extension-loader-tests", Guid.NewGuid().ToString("N"));
        _outsideRoot = Path.Combine(AppContext.BaseDirectory, "extension-loader-tests-outside", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testRoot);
        Directory.CreateDirectory(_outsideRoot);
    }

    [Fact]
    public async Task AddBotNexusExtensions_HappyPath_LoadsAndRegistersConfiguredExtension()
    {
        var result = await ExecuteSingleToolAsync(
            extensionType: "tools",
            extensionKey: "convention",
            extensionAssemblyPath: typeof(ConventionEchoTool).Assembly.Location,
            configValues: new Dictionary<string, string?>
            {
                ["BotNexus:Tools:Extensions:convention:Message"] = "hello"
            });

        result.Should().Be("convention:hello");
    }

    [Fact]
    public void AddBotNexusExtensions_MissingFolder_LogsWarningAndDoesNotThrow()
    {
        var config = BuildConfiguration(new Dictionary<string, string?>
        {
            ["BotNexus:ExtensionsPath"] = _testRoot,
            ["BotNexus:Tools:Extensions:missing:Enabled"] = "true"
        });

        var services = new ServiceCollection();
        var logs = CaptureConsole(() => services.AddBotNexusExtensions(config));
        using var provider = services.BuildServiceProvider();
        var report = provider.GetRequiredService<ExtensionLoadReport>();

        logs.Should().Contain("Extension folder not found");
        report.FailedCount.Should().Be(0);
        report.WarningCount.Should().Be(1);
    }

    [Fact]
    public void AddBotNexusExtensions_EmptyFolder_LogsWarningAndDoesNotThrow()
    {
        Directory.CreateDirectory(Path.Combine(_testRoot, "tools", "empty"));

        var config = BuildConfiguration(new Dictionary<string, string?>
        {
            ["BotNexus:ExtensionsPath"] = _testRoot,
            ["BotNexus:Tools:Extensions:empty:Enabled"] = "true"
        });

        var services = new ServiceCollection();
        var logs = CaptureConsole(() => services.AddBotNexusExtensions(config));
        using var provider = services.BuildServiceProvider();
        var report = provider.GetRequiredService<ExtensionLoadReport>();

        logs.Should().Contain("No assemblies found in extension folder");
        report.FailedCount.Should().Be(0);
        report.WarningCount.Should().Be(1);
    }

    [Fact]
    public async Task AddBotNexusExtensions_RegistrarBasedLoading_UsesRegistrarRegistration()
    {
        var result = await ExecuteSingleToolAsync(
            extensionType: "tools",
            extensionKey: "registrar",
            extensionAssemblyPath: typeof(RegistrarEchoTool).Assembly.Location,
            configValues: new Dictionary<string, string?>
            {
                ["BotNexus:Tools:Extensions:registrar:Message"] = "from-registrar"
            });

        result.Should().Be("registrar:from-registrar");
    }

    [Fact]
    public async Task AddBotNexusExtensions_ConventionBasedLoading_RegistersInterfaceImplementations()
    {
        var result = await ExecuteSingleToolAsync(
            extensionType: "tools",
            extensionKey: "convention",
            extensionAssemblyPath: typeof(ConventionEchoTool).Assembly.Location,
            configValues: new Dictionary<string, string?>
            {
                ["BotNexus:Tools:Extensions:convention:Message"] = "from-convention"
            });

        result.Should().Be("convention:from-convention");
    }

    [Fact]
    public void AddBotNexusExtensions_NoMatchingTypes_LogsWarning()
    {
        var extensionFolder = Path.Combine(_testRoot, "providers", "nomatch");
        Directory.CreateDirectory(extensionFolder);
        File.Copy(typeof(ConventionEchoTool).Assembly.Location, Path.Combine(extensionFolder, "BotNexus.Tests.Extensions.Convention.dll"), overwrite: true);

        var config = BuildConfiguration(new Dictionary<string, string?>
        {
            ["BotNexus:ExtensionsPath"] = _testRoot,
            ["BotNexus:Providers:nomatch:ApiKey"] = "test-key"
        });

        var services = new ServiceCollection();
        var logs = CaptureConsole(() => services.AddBotNexusExtensions(config));

        logs.Should().Contain("No types implementing 'ILlmProvider' found in extension 'providers/nomatch'");
    }

    [Fact]
    public void AddBotNexusExtensions_MultipleTypes_AllChannelsRegistered()
    {
        var extensionFolder = Path.Combine(_testRoot, "channels", "multi");
        Directory.CreateDirectory(extensionFolder);
        File.Copy(typeof(ConventionEchoTool).Assembly.Location, Path.Combine(extensionFolder, "BotNexus.Tests.Extensions.Convention.dll"), overwrite: true);

        var config = BuildConfiguration(new Dictionary<string, string?>
        {
            ["BotNexus:ExtensionsPath"] = _testRoot,
            ["BotNexus:Channels:Instances:multi:Enabled"] = "true",
            ["BotNexus:Channels:Instances:multi:ChannelName"] = "multi"
        });

        var services = new ServiceCollection();
        services.AddSingleton(Mock.Of<IMemoryStore>());
        services.AddBotNexusExtensions(config);
        using var provider = services.BuildServiceProvider();

        var channels = provider.GetServices<IChannel>().ToList();
        channels.Should().HaveCount(2);
        channels.Select(c => c.Name).Should().Contain("alpha:multi");
        channels.Select(c => c.Name).Should().Contain(name => name.StartsWith("beta:multi:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AddBotNexusExtensions_RegistrarBasedLoading_UsesCorrectConfigSection()
    {
        RegistrarExtensionRegistrar.Reset();

        var result = await ExecuteSingleToolAsync(
            extensionType: "tools",
            extensionKey: "registrar",
            extensionAssemblyPath: typeof(RegistrarEchoTool).Assembly.Location,
            configValues: new Dictionary<string, string?>
            {
                ["BotNexus:Tools:Extensions:registrar:Message"] = "tools-config"
            });

        result.Should().Be("registrar:tools-config");
    }

    [Fact]
    public void AddBotNexusExtensions_ConventionBasedLoading_UsesActivatorUtilities()
    {
        var extensionFolder = Path.Combine(_testRoot, "channels", "activator");
        Directory.CreateDirectory(extensionFolder);
        File.Copy(typeof(ConventionEchoTool).Assembly.Location, Path.Combine(extensionFolder, "BotNexus.Tests.Extensions.Convention.dll"), overwrite: true);

        var config = BuildConfiguration(new Dictionary<string, string?>
        {
            ["BotNexus:ExtensionsPath"] = _testRoot,
            ["BotNexus:Channels:Instances:activator:Enabled"] = "true",
            ["BotNexus:Channels:Instances:activator:ChannelName"] = "injected"
        });

        var memoryStoreMock = new Mock<IMemoryStore>(MockBehavior.Strict);
        var services = new ServiceCollection();
        services.AddSingleton(memoryStoreMock.Object);
        services.AddBotNexusExtensions(config);

        using var provider = services.BuildServiceProvider();
        var channels = provider.GetServices<IChannel>().ToList();
        channels.Select(c => c.Name).Should().Contain(name => name.StartsWith("beta:injected:", StringComparison.Ordinal));
    }

    [Fact]
    public void AddBotNexusExtensions_PathTraversalAttempt_IsRejected()
    {
        var config = BuildConfiguration(new Dictionary<string, string?>
        {
            ["BotNexus:ExtensionsPath"] = _testRoot,
            ["BotNexus:Tools:Extensions:..\\evil:Enabled"] = "true"
        });

        var services = new ServiceCollection();
        var logs = CaptureConsole(() => services.AddBotNexusExtensions(config));

        logs.Should().Contain("Rejected extension");
    }

    [Fact]
    public void AddBotNexusExtensions_InvalidAssembly_IsRejectedGracefully()
    {
        var extensionFolder = Path.Combine(_testRoot, "tools", "invalid");
        Directory.CreateDirectory(extensionFolder);
        File.WriteAllText(Path.Combine(extensionFolder, "not-an-assembly.dll"), "this is not a .NET assembly");

        var config = BuildConfiguration(new Dictionary<string, string?>
        {
            ["BotNexus:ExtensionsPath"] = _testRoot,
            ["BotNexus:Tools:Extensions:invalid:Enabled"] = "true"
        });

        var services = new ServiceCollection();
        var logs = CaptureConsole(() => services.AddBotNexusExtensions(config));

        logs.Should().Contain("Not a valid .NET assembly");
    }

    [Fact]
    public void AddBotNexusExtensions_RequireSignedAssemblies_RejectsUnsignedAssembly()
    {
        var extensionFolder = Path.Combine(_testRoot, "tools", "unsigned");
        Directory.CreateDirectory(extensionFolder);
        File.Copy(typeof(ConventionEchoTool).Assembly.Location, Path.Combine(extensionFolder, "BotNexus.Tests.Extensions.Convention.dll"), overwrite: true);

        var config = BuildConfiguration(new Dictionary<string, string?>
        {
            ["BotNexus:ExtensionsPath"] = _testRoot,
            ["BotNexus:Extensions:RequireSignedAssemblies"] = "true",
            ["BotNexus:Tools:Extensions:unsigned:Enabled"] = "true"
        });

        var services = new ServiceCollection();
        var logs = CaptureConsole(() => services.AddBotNexusExtensions(config));

        logs.Should().Contain("Assembly is not strong-name signed");
    }

    [Fact]
    public void AddBotNexusExtensions_MaxAssembliesPerExtension_IsEnforced()
    {
        var extensionFolder = Path.Combine(_testRoot, "tools", "too-many");
        Directory.CreateDirectory(extensionFolder);

        for (var i = 0; i < 51; i++)
            File.Copy(typeof(ConventionEchoTool).Assembly.Location, Path.Combine(extensionFolder, $"copy-{i}.dll"), overwrite: true);

        var config = BuildConfiguration(new Dictionary<string, string?>
        {
            ["BotNexus:ExtensionsPath"] = _testRoot,
            ["BotNexus:Extensions:MaxAssembliesPerExtension"] = "50",
            ["BotNexus:Tools:Extensions:too-many:Enabled"] = "true"
        });

        var services = new ServiceCollection();
        var logs = CaptureConsole(() => services.AddBotNexusExtensions(config));

        logs.Should().Contain("exceeds limit 50");
    }

    [Fact]
    public void AddBotNexusExtensions_DryRun_DoesNotLoadOrRegisterAssemblies()
    {
        var extensionFolder = Path.Combine(_testRoot, "tools", "dryrun");
        Directory.CreateDirectory(extensionFolder);
        File.Copy(typeof(ConventionEchoTool).Assembly.Location, Path.Combine(extensionFolder, "BotNexus.Tests.Extensions.Convention.dll"), overwrite: true);

        var config = BuildConfiguration(new Dictionary<string, string?>
        {
            ["BotNexus:ExtensionsPath"] = _testRoot,
            ["BotNexus:Extensions:DryRun"] = "true",
            ["BotNexus:Tools:Extensions:dryrun:Enabled"] = "true"
        });

        var services = new ServiceCollection();
        var logs = CaptureConsole(() => services.AddBotNexusExtensions(config));
        using var provider = services.BuildServiceProvider();

        provider.GetServices<ITool>().Should().BeEmpty();
        logs.Should().Contain("Dry run would load assembly");
    }

    [Fact]
    public void AddBotNexusExtensions_LogsAssemblyPathVersionAndTypes()
    {
        var extensionFolder = Path.Combine(_testRoot, "tools", "logging");
        Directory.CreateDirectory(extensionFolder);
        File.Copy(typeof(ConventionEchoTool).Assembly.Location, Path.Combine(extensionFolder, "BotNexus.Tests.Extensions.Convention.dll"), overwrite: true);

        var config = BuildConfiguration(new Dictionary<string, string?>
        {
            ["BotNexus:ExtensionsPath"] = _testRoot,
            ["BotNexus:Tools:Extensions:logging:Enabled"] = "true"
        });

        var services = new ServiceCollection();
        var logs = CaptureConsole(() => services.AddBotNexusExtensions(config));

        logs.Should().Contain("Path=");
        logs.Should().Contain("Version=");
        logs.Should().Contain("DiscoveredTypes=[");
    }

    [Fact]
    public void AddBotNexusExtensions_JunctionEscapingRoot_IsRejected()
    {
        var outsideFolder = Path.Combine(_outsideRoot, "outside");
        var junctionLink = Path.Combine(_testRoot, "tools", "linked");
        Directory.CreateDirectory(outsideFolder);
        Directory.CreateDirectory(Path.GetDirectoryName(junctionLink)!);

        var process = Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c mklink /J \"{junctionLink}\" \"{outsideFolder}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        });

        process.Should().NotBeNull();
        process!.WaitForExit();
        process.ExitCode.Should().Be(0, $"mklink output: {process.StandardOutput.ReadToEnd()} {process.StandardError.ReadToEnd()}");

        var config = BuildConfiguration(new Dictionary<string, string?>
        {
            ["BotNexus:ExtensionsPath"] = _testRoot,
            ["BotNexus:Tools:Extensions:linked:Enabled"] = "true"
        });

        var services = new ServiceCollection();
        var logs = CaptureConsole(() => services.AddBotNexusExtensions(config));

        logs.Should().Contain("escapes extensions root");
    }

    [Fact]
    public void AddBotNexusExtensions_AssemblyLoadContextIsolation_ExtensionTypesAreNotInDefaultContext()
    {
        var extensionFolder = Path.Combine(_testRoot, "tools", "isolation");
        Directory.CreateDirectory(extensionFolder);
        File.Copy(typeof(ConventionEchoTool).Assembly.Location, Path.Combine(extensionFolder, "BotNexus.Tests.Extensions.Convention.dll"), overwrite: true);

        var config = BuildConfiguration(new Dictionary<string, string?>
        {
            ["BotNexus:ExtensionsPath"] = _testRoot,
            ["BotNexus:Tools:Extensions:isolation:Enabled"] = "true"
        });

        var services = new ServiceCollection();
        services.AddBotNexusExtensions(config);
        using var provider = services.BuildServiceProvider();

        var tool = provider.GetServices<ITool>().Should().ContainSingle().Subject;
        var context = AssemblyLoadContext.GetLoadContext(tool.GetType().Assembly);
        context.Should().NotBeNull();
        context.Should().NotBe(AssemblyLoadContext.Default);
    }

    [Fact]
    public async Task AddBotNexusExtensions_ConfigBinding_ExtensionReceivesConfigSection()
    {
        var result = await ExecuteSingleToolAsync(
            extensionType: "tools",
            extensionKey: "convention",
            extensionAssemblyPath: typeof(ConventionEchoTool).Assembly.Location,
            configValues: new Dictionary<string, string?>
            {
                ["BotNexus:Tools:Extensions:convention:Message"] = "config-bound"
            });

        result.Should().Be("convention:config-bound");
    }

    [Fact]
    public void AddBotNexusExtensions_DryRun_InvalidAssembly_LogsValidationFailure()
    {
        var extensionFolder = Path.Combine(_testRoot, "tools", "dryrun-invalid");
        Directory.CreateDirectory(extensionFolder);
        File.WriteAllText(Path.Combine(extensionFolder, "invalid.dll"), "bad");

        var config = BuildConfiguration(new Dictionary<string, string?>
        {
            ["BotNexus:ExtensionsPath"] = _testRoot,
            ["BotNexus:Extensions:DryRun"] = "true",
            ["BotNexus:Tools:Extensions:dryrun-invalid:Enabled"] = "true"
        });

        var services = new ServiceCollection();
        var logs = CaptureConsole(() => services.AddBotNexusExtensions(config));
        logs.Should().Contain("Dry run validation failed");
    }

    [Fact]
    public void AddBotNexusExtensions_SharedAssemblyAllowed_ReusesHostAssembly()
    {
        var extensionFolder = Path.Combine(_testRoot, "providers", "shared");
        Directory.CreateDirectory(extensionFolder);
        File.Copy(typeof(BotNexus.Core.Configuration.BotNexusConfig).Assembly.Location, Path.Combine(extensionFolder, "BotNexus.Core.dll"), overwrite: true);

        var config = BuildConfiguration(new Dictionary<string, string?>
        {
            ["BotNexus:ExtensionsPath"] = _testRoot,
            ["BotNexus:Providers:shared:ApiKey"] = "x"
        });

        var services = new ServiceCollection();
        var logs = CaptureConsole(() => services.AddBotNexusExtensions(config));
        logs.Should().Contain("Reused shared assembly");
    }

    [Fact]
    public void AddBotNexusExtensions_RegistrarThrows_LogsErrorAndContinues()
    {
        var extensionFolder = Path.Combine(_testRoot, "tools", "registrar-throws");
        Directory.CreateDirectory(extensionFolder);
        File.Copy(typeof(RegistrarEchoTool).Assembly.Location, Path.Combine(extensionFolder, "BotNexus.Tests.Extensions.Registrar.dll"), overwrite: true);

        var config = BuildConfiguration(new Dictionary<string, string?>
        {
            ["BotNexus:ExtensionsPath"] = _testRoot,
            ["BotNexus:Tools:Extensions:registrar-throws:Enabled"] = "true",
            ["BotNexus:Tools:Extensions:registrar-throws:Message"] = "value"
        });

        var services = new ServiceCollection();
        var logs = CaptureConsole(() => services.AddBotNexusExtensions(config));
        logs.Should().Contain("Registrar 'BotNexus.Tests.Extensions.Registrar.ThrowingExtensionRegistrar' failed");
    }

    [Fact]
    public void ExtensionLoader_PrivateHelpers_HandleGuardBranches()
    {
        var resolver = typeof(ExtensionLoaderExtensions).GetMethod("TryResolveExtensionFolder", BindingFlags.NonPublic | BindingFlags.Static);
        resolver.Should().NotBeNull();

        var args = new object?[] { _testRoot, "tools", "", null, null };
        var emptyResult = (bool)resolver!.Invoke(null, args)!;
        emptyResult.Should().BeFalse();
        args[4].Should().Be("Extension key is empty");

        args = [ _testRoot, "tools", "C:\\absolute", null, null ];
        var rootedResult = (bool)resolver.Invoke(null, args)!;
        rootedResult.Should().BeFalse();
        args[4].Should().Be("Rooted paths are not allowed");

        args = [ _testRoot, "tools", "bad\0key", null, null ];
        var invalidResult = (bool)resolver.Invoke(null, args)!;
        invalidResult.Should().BeFalse();
        args[4].Should().Be("Key contains invalid path characters");

        var sectionGetter = typeof(ExtensionLoaderExtensions).GetMethod("GetExtensionConfigSection", BindingFlags.NonPublic | BindingFlags.Static);
        sectionGetter.Should().NotBeNull();
        var config = BuildConfiguration(new Dictionary<string, string?> { ["BotNexus:Any:Value"] = "x" });
        var botSection = config.GetSection("BotNexus");
        var fallback = (IConfiguration)sectionGetter!.Invoke(null, [ botSection, "unknown", "x" ])!;
        fallback.Should().BeSameAs(botSection);

        var allowed = typeof(ExtensionLoaderExtensions).GetMethod("IsAllowedSharedAssembly", BindingFlags.NonPublic | BindingFlags.Static);
        allowed.Should().NotBeNull();
        ((bool)allowed!.Invoke(null, [ null ])!).Should().BeFalse();
    }

    [Fact]
    public void ExtensionLoader_CreateExtensionInstance_FallsBackWhenConfigCtorThrows()
    {
        var createInstance = typeof(ExtensionLoaderExtensions).GetMethod("CreateExtensionInstance", BindingFlags.NonPublic | BindingFlags.Static);
        createInstance.Should().NotBeNull();

        var services = new ServiceCollection().BuildServiceProvider();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>()).Build();
        var instance = createInstance!.Invoke(null, [ services, typeof(ThrowingConfigConstructorType), config ]);

        instance.Should().BeOfType<ThrowingConfigConstructorType>();
        ((ThrowingConfigConstructorType)instance!).UsedFallbackConstructor.Should().BeTrue();
    }

    private sealed class ThrowingConfigConstructorType
    {
        public bool UsedFallbackConstructor { get; }

        public ThrowingConfigConstructorType(IConfiguration configuration) => throw new InvalidOperationException("fail");
        public ThrowingConfigConstructorType() => UsedFallbackConstructor = true;
    }

    private static IConfiguration BuildConfiguration(Dictionary<string, string?> values)
        => new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    private async Task<string> ExecuteSingleToolAsync(
        string extensionType,
        string extensionKey,
        string extensionAssemblyPath,
        Dictionary<string, string?> configValues)
    {
        var extensionFolder = Path.Combine(_testRoot, extensionType, extensionKey);
        Directory.CreateDirectory(extensionFolder);

        var assemblyFileName = Path.GetFileName(extensionAssemblyPath);
        File.Copy(extensionAssemblyPath, Path.Combine(extensionFolder, assemblyFileName), overwrite: true);

        configValues["BotNexus:ExtensionsPath"] = _testRoot;
        configValues[$"BotNexus:Tools:Extensions:{extensionKey}:Enabled"] = "true";

        var config = BuildConfiguration(configValues);
        var services = new ServiceCollection();
        services.AddBotNexusExtensions(config);

        using var provider = services.BuildServiceProvider();
        var tools = provider.GetServices<ITool>().ToList();
        tools.Should().ContainSingle();
        var result = await tools.Single().ExecuteAsync(new Dictionary<string, object?>());

        var contextStore = provider.GetService<ExtensionLoadContextStore>();
        if (contextStore is not null)
        {
            foreach (var context in contextStore.Contexts)
                context.Unload();
        }

        GC.Collect();
        GC.WaitForPendingFinalizers();
        return result;
    }

    private static string CaptureConsole(Action action)
    {
        var originalOut = Console.Out;
        using var writer = new StringWriter();

        try
        {
            Console.SetOut(writer);
            action();
            return writer.ToString();
        }
        finally
        {
            Console.SetOut(originalOut);
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_testRoot))
        {
            try
            {
                DeleteReparsePointsUnderRoot();
                Directory.Delete(_testRoot, recursive: true);
            }
            catch
            {
                // Best-effort cleanup for collectible AssemblyLoadContext file locks.
            }
        }

        if (Directory.Exists(_outsideRoot))
        {
            try
            {
                Directory.Delete(_outsideRoot, recursive: true);
            }
            catch
            {
                // Best effort cleanup.
            }
        }
    }

    private void DeleteReparsePointsUnderRoot()
    {
        var allDirectories = Directory
            .EnumerateDirectories(_testRoot, "*", SearchOption.AllDirectories)
            .OrderByDescending(path => path.Length)
            .ToList();

        foreach (var directory in allDirectories)
        {
            try
            {
                var attributes = File.GetAttributes(directory);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    Directory.Delete(directory, recursive: false);
            }
            catch
            {
                // Best effort cleanup.
            }
        }
    }
}
