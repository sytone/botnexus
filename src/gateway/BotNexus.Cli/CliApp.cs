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
        ConfigureOutputEncoding();
        CliBanner.WriteTo(bannerWriter);

        using var serviceProvider = BuildServiceProvider();
        var root = BuildRootCommand(serviceProvider);
        return await root.InvokeAsync(args);
    }

    /// <summary>
    /// Picks the writer that should receive the decorative startup banner. The banner
    /// is rendered only when the CLI is connected to an interactive terminal — when
    /// stdout is redirected to a pipe, a file, or a process capture the banner would
    /// pollute machine-readable output (and the box-drawing characters break tests
    /// that diff exact strings). Returns <see cref="TextWriter.Null"/> when redirected
    /// so the banner is suppressed, otherwise the supplied console writer.
    /// </summary>
    /// <param name="consoleOut">The CLI's normal stdout writer (typically <see cref="Console.Out"/>).</param>
    /// <param name="isOutputRedirected">
    /// Whether stdout is currently redirected. Pass <see cref="Console.IsOutputRedirected"/>
    /// from <c>Program.cs</c>; tests supply an explicit value.
    /// </param>
    /// <returns>The writer the banner should be sent to.</returns>
    internal static TextWriter ResolveBannerWriter(TextWriter consoleOut, bool isOutputRedirected)
    {
        ArgumentNullException.ThrowIfNull(consoleOut);
        return isOutputRedirected ? TextWriter.Null : consoleOut;
    }

    private static void ConfigureOutputEncoding()
    {
        try
        {
            Console.OutputEncoding = Encoding.UTF8;
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
            .BuildServiceProvider();
    }

    private static RootCommand BuildRootCommand(IServiceProvider serviceProvider)
    {
        var verboseOption = new Option<bool>("--verbose", "Show additional command output.");
        var root = new RootCommand("BotNexus platform CLI");

        root.AddGlobalOption(verboseOption);
        root.AddCommand(serviceProvider.GetRequiredService<ValidateCommand>().Build(verboseOption));
        root.AddCommand(serviceProvider.GetRequiredService<InitCommand>().Build(verboseOption));
        root.AddCommand(serviceProvider.GetRequiredService<AgentCommands>().Build(verboseOption));
        root.AddCommand(serviceProvider.GetRequiredService<MemoryCommands>().Build(verboseOption));
        root.AddCommand(serviceProvider.GetRequiredService<ConfigCommands>().Build(verboseOption));
        root.AddCommand(serviceProvider.GetRequiredService<LocationsCommand>().Build(verboseOption));
        root.AddCommand(serviceProvider.GetRequiredService<DoctorCommand>().Build(verboseOption));
        root.AddCommand(serviceProvider.GetRequiredService<InstallCommand>().Build(verboseOption));
        root.AddCommand(serviceProvider.GetRequiredService<BuildCommand>().Build(verboseOption));
        root.AddCommand(serviceProvider.GetRequiredService<ServeCommand>().Build(verboseOption));
        root.AddCommand(serviceProvider.GetRequiredService<GatewayCommand>().Build(verboseOption));
        root.AddCommand(serviceProvider.GetRequiredService<PromptCommands>().Build(verboseOption));
        root.AddCommand(serviceProvider.GetRequiredService<UpdateCommand>().Build(verboseOption));
        root.AddCommand(serviceProvider.GetRequiredService<ProviderCommand>().Build(verboseOption));
        root.AddCommand(serviceProvider.GetRequiredService<CronCommands>().Build(verboseOption));

        return root;
    }
}