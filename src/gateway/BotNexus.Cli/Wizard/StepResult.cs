namespace BotNexus.Cli.Wizard;

public enum StepOutcome
{
    /// <summary>Proceed to the next step in sequence.</summary>
    Continue,

    /// <summary>Jump to a named step.</summary>
    GoTo,

    /// <summary>Abort the wizard.</summary>
    Abort
}

public sealed record StepResult(StepOutcome Outcome, string? GoToStep = null)
{
    public static StepResult Continue() => new(StepOutcome.Continue);
    public static StepResult GoTo(string stepName) => new(StepOutcome.GoTo, stepName);
    public static StepResult Abort() => new(StepOutcome.Abort);
}
