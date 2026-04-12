namespace BotNexus.Gateway.Abstractions.Security;

public sealed record FileAccessPolicy
{
    public IReadOnlyList<string> AllowedReadPaths { get; init; } = [];
    public IReadOnlyList<string> AllowedWritePaths { get; init; } = [];
    public IReadOnlyList<string> DeniedPaths { get; init; } = [];
}
