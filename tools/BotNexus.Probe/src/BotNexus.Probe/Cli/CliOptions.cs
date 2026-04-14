namespace BotNexus.Probe.Cli;

public sealed record CliOptions(
    string LogsPath,
    string SessionsPath,
    string SessionDbPath,
    string? GatewayUrl,
    bool TextOutput,
    string[] RemainingArgs);
