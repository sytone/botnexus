namespace BotNexus.Cli.Wizard;

/// <summary>
/// A single step in a wizard flow. Steps execute sequentially unless branching
/// is requested via <see cref="StepResult.GoTo"/>.
/// </summary>
public interface IWizardStep
{
    /// <summary>
    /// Unique name for this step, used as a target for GoTo branching.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Executes the step logic — prompt the user, run a check, or perform an action.
    /// </summary>
    Task<StepResult> ExecuteAsync(WizardContext context, CancellationToken cancellationToken);
}
