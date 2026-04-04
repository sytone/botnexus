using BotNexus.Core.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace BotNexus.Tests.Extensions.Registrar;

public sealed class RegistrarExtensionRegistrar : IExtensionRegistrar
{
    public static string? LastMessage { get; private set; }
    public static string? LastConfigPath { get; private set; }

    public static void Reset()
    {
        LastMessage = null;
        LastConfigPath = null;
    }

    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        var message = configuration["Message"] ?? "unset";
        LastMessage = message;
        LastConfigPath = (configuration as IConfigurationSection)?.Path;
        services.AddSingleton<ITool>(_ => new RegistrarEchoTool(message));
    }
}

public sealed class ThrowingExtensionRegistrar : IExtensionRegistrar
{
    public void Register(IServiceCollection services, IConfiguration configuration)
        => throw new InvalidOperationException("Simulated registrar failure");
}
