using System.CommandLine;
using System.Text;
using BotNexus.Cli.Commands;
using BotNexus.Cli.Services;
using BotNexus.Gateway.Abstractions.Configuration;
using BotNexus.Gateway.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BotNexus.Cli;

internal static class CliApp
{
    internal static async Task<int> RunAsync(string[] args, TextWriter bannerWriter)
    {
        CliBanner.WriteTo(bannerWriter);

        using var serviceProvider = BuildServiceProvider();
        var root = BuildRootCommand(serviceProvider);
        return await root.InvokeAsync(args);
    }

    /// <summary>
    /// Picks the writer that should receive the decorative startup banner.
    /// <para>
    /// The banner is always routed to stderr so that stdout remains clean for
    /// machine-readable output regardless of redirection state. When stderr is also
    /// redirected (e.g. in CI pipelines that capture both streams) the banner is
    /// suppressed entirely via <see cref="TextWriter.Null"/> to avoid polluting
    /// captured output.
    /// </para>
    /// </summary>
    /// <param name="consoleError">The CLI's stderr writer (typically <see cref="Console.Error"/>).</param>
    /// <param name="isOutputRedirected">
    /// Whether stdout is currently redirected. Pass <see cref="Console.IsOutputRedirected"/>
    /// from <c>Program.cs</c>; tests supply an explicit value.
    /// </param>
    /// <returns>The writer the banner should be sent to.</returns>
    internal static TextWriter ResolveBannerWriter(TextWriter consoleError, bool isOutputRedirected)
    {
        ArgumentNullException.ThrowIfNull(consoleError);
        return isOutputRedirected ? TextWriter.Null : consoleError;
    }

    /// <summary>
    /// Switches the console standard output and error streams to UTF-8 so the
    /// Unicode box-drawing and shaded-block characters in the startup banner
    /// render correctly on consoles whose default code page cannot represent them
    /// (notably Windows, where the legacy OEM code page produces replacement glyphs).
    /// <para>
    /// Must be called <b>before</b> <see cref="Console.Out"/> or <see cref="Console.Error"/>
    /// are captured for later use: assigning <see cref="Console.OutputEncoding"/> recreates
    /// both writers, so any reference captured beforehand keeps the old encoding.
    /// </para>
    /// </summary>
    internal static void ConfigureOutputEncoding()
        => ApplyOutputEncoding(static encoding => Console.OutputEncoding = encoding);

    /// <summary>
    /// Applies the banner-safe UTF-8 encoding via the supplied setter, swallowing the
    /// failures that legitimately occur on locked-down or redirected hosts. The setter
    /// is injected so the resilience contract can be verified without mutating the
    /// process-global <see cref="Console.OutputEncoding"/>, which would recreate
    /// <see cref="Console.Out"/>/<see cref="Console.Error"/> and break console-writing
    /// tests running in parallel.
    /// </summary>
    /// <param name="applyEncoding">Receives the encoding the CLI wants the console to use.</param>
    internal static void ApplyOutputEncoding(Action<Encoding> applyEncoding)
    {
        ArgumentNullException.ThrowIfNull(applyEncoding);
        try
        {
            applyEncoding(Encoding.UTF8);
        }
        catch (IOException)
        {
            // Some redirected or hosted consoles do not allow changing the output encoding.
        }
        catch (ArgumentException)
        {
            // UTF-8 should be available, but keep CLI startup resilient on unusual hosts.
        }
    }

    private static ServiceProvider BuildServiceProvider()
    {
        return new ServiceCollection()
            .AddLogging(builder => builder.AddConsole().SetMinimumLevel(LogLevel.Warning))
            .AddSingleton<IHealthChecker, HttpHealthChecker>()
            .AddSingleton<IGatewayProcessManager, GatewayProcessManager>()
            .AddSingleton<IConfigPathResolver, ConfigPathResolver>()
            .AddSingleton<ValidateCommand>()
            .AddSingleton<DoctorCommand>()
            .AddSingleton<InitCommand>()
            .AddSingleton<AgentCommands>()
            .AddSingleton<MemoryCommands>()
            .AddSingleton<ConfigCommands>()
            .AddSingleton<LocationsCommand>()
            .AddSingleton<InstallCommand>()
            .AddSingleton<BuildCommand>()
            .AddSingleton<GatewayCommand>()
            .AddSingleton<PromptCommands>()
            .AddSingleton<ServeCommand>()
            .AddSingleton<UpdateCommand>()
            .AddSingleton<ProviderCommand>()
            .AddSingleton<CronCommands>()
            .AddSingleton<SatelliteCommand>()
            .AddSingleton<DebugCommand>()
            .AddSingleton<ConversationCommands>()
            .AddSingleton<SubAgentCommand>()
            .BuildServiceProvider();
    }

    private static RootCommand BuildRootCommand(IServiceProvider serviceProvider)
    {
        var verboseOption = new Option<bool>("--verbose", "Show additional command output.");
        var targetOption = new Option<string?>("--target", () => null, "BotNexus home directory (config, workspace, extensions). Defaults to ~/.botnexus or BOTNEXUS_HOME.");
        var root = new RootCommand("BotNexus platform CLI");

        root.AddGlobalOption(verboseOption);
        root.AddGlobalOption(targetOption);
        root.AddCommand(serviceProvider.GetRequiredService<ValidateCommand>().Build(verboseOption, targetOption));
        root.AddCommand(serviceProvider.GetRequiredService<InitCommand>().Build(verboseOption, targetOption));
        root.AddCommand(serviceProvider.GetRequiredService<AgentCommands>().Build(verboseOption, targetOption));
        root.AddCommand(serviceProvider.GetRequiredService<MemoryCommands>().Build(verboseOption, targetOption));
        root.AddCommand(serviceProvider.GetRequiredService<ConfigCommands>().Build(verboseOption, targetOption));
        root.AddCommand(serviceProvider.GetRequiredService<LocationsCommand>().Build(verboseOption, targetOption));
        root.AddCommand(serviceProvider.GetRequiredService<DoctorCommand>().Build(verboseOption, targetOption));
        root.AddCommand(serviceProvider.GetRequiredService<InstallCommand>().Build(verboseOption, targetOption));
        root.AddCommand(serviceProvider.GetRequiredService<BuildCommand>().Build(verboseOption));
        root.AddCommand(serviceProvider.GetRequiredService<ServeCommand>().Build(verboseOption, targetOption));
        root.AddCommand(serviceProvider.GetRequiredService<GatewayCommand>().Build(verboseOption, targetOption));
        root.AddCommand(serviceProvider.GetRequiredService<PromptCommands>().Build(verboseOption, targetOption));
        root.AddCommand(serviceProvider.GetRequiredService<UpdateCommand>().Build(verboseOption, targetOption));
        root.AddCommand(serviceProvider.GetRequiredService<ProviderCommand>().Build(verboseOption, targetOption));
        root.AddCommand(serviceProvider.GetRequiredService<CronCommands>().Build(verboseOption, targetOption));
        root.AddCommand(serviceProvider.GetRequiredService<SatelliteCommand>().Build(verboseOption, targetOption));
        root.AddCommand(serviceProvider.GetRequiredService<DebugCommand>().Build(verboseOption, targetOption));
        root.AddCommand(serviceProvider.GetRequiredService<ConversationCommands>().Build(verboseOption, targetOption));
        root.AddCommand(serviceProvider.GetRequiredService<SubAgentCommand>().Build(targetOption));

        return root;
    }
}