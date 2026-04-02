using BotNexus.Core.Configuration;

namespace BotNexus.Diagnostics;

public sealed class DiagnosticsPaths
{
    public DiagnosticsPaths(string homePath, string configPath)
    {
        HomePath = BotNexusHome.ResolvePath(homePath);
        ConfigPath = BotNexusHome.ResolvePath(configPath);
    }

    public string HomePath { get; }
    public string ConfigPath { get; }
    public string TokensPath => Path.Combine(HomePath, "tokens");
    public string LogsPath => Path.Combine(HomePath, "logs");

    public static DiagnosticsPaths FromBotNexusHome()
    {
        var homePath = BotNexusHome.ResolveHomePath();
        return new DiagnosticsPaths(homePath, Path.Combine(homePath, "config.json"));
    }
}
