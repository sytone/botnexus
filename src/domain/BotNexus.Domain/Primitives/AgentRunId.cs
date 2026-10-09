using Vogen;
namespace BotNexus.Domain.Primitives;
/// <summary>Identifies one admitted agent execution; never a cron or transport identity.</summary>
[ValueObject<string>(conversions: Conversions.SystemTextJson)]
public readonly partial struct AgentRunId
{
    /// <summary>Creates a fresh execution identity.</summary>
    public static AgentRunId Create() => From(Guid.NewGuid().ToString("N"));
    private static Validation Validate(string value) => string.IsNullOrWhiteSpace(value)
        ? Validation.Invalid("AgentRunId cannot be null, empty, or whitespace.") : Validation.Ok;
    private static string NormalizeInput(string input) => input is null ? input! : input.Trim();
}
