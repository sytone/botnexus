namespace BotNexus.Probe;

public sealed record ProbeOptions(
    int Port,
    string? GatewayUrl,
    string LogsPath,
    string SessionsPath,
    int? OtlpPort);
