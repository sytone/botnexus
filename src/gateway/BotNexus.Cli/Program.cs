using System.CommandLine;
using BotNexus.Cli.Commands;
using BotNexus.Gateway.Abstractions.Configuration;
using BotNexus.Gateway.Configuration;
using Microsoft.Extensions.DependencyInjection;

var verboseOption = new Option<bool>("--verbose", "Show additional command output.");

using var serviceProvider = new ServiceCollection()
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
    .AddSingleton<ServeCommand>()
    .AddSingleton<ProviderCommand>()
    .BuildServiceProvider();

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
root.AddCommand(serviceProvider.GetRequiredService<ProviderCommand>().Build(verboseOption));
return await root.InvokeAsync(args);
