namespace BotNexus.Tools;

/// <summary>Shared optional argument used to classify base-instruction-file mutations (#3462).</summary>
public static class InstructionScopeArgument
{
    public const string Name = "instructionScope";
    public const string Agnostic = "agnostic";
    public const string ModelSpecific = "model-specific";

    public static bool IsValid(string? value) =>
        string.Equals(value, Agnostic, StringComparison.OrdinalIgnoreCase)
        || string.Equals(value, ModelSpecific, StringComparison.OrdinalIgnoreCase);
}
