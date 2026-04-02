namespace BotNexus.Core.Abstractions;

public interface IHealthCheckup
{
    string Name { get; }
    string Category { get; }
    string Description { get; }
    Task<CheckupResult> RunAsync(CancellationToken ct = default);
}

public record CheckupResult(CheckupStatus Status, string Message, string? Advice = null);

public enum CheckupStatus
{
    Pass,
    Warn,
    Fail
}
