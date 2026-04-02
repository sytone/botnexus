namespace BotNexus.Core.Extensions;

public sealed record ExtensionLoadResult(
    string Type,
    string Key,
    bool Success,
    string Message,
    string? Version = null,
    bool CountsAsFailure = true);

public sealed class ExtensionLoadReport
{
    public required IReadOnlyList<ExtensionLoadResult> Results { get; init; }
    public required int LoadedCount { get; init; }
    public required int FailedCount { get; init; }
    public required int WarningCount { get; init; }
    public required bool Completed { get; init; }

    public bool CompletedSuccessfully => Completed && FailedCount == 0;
}
