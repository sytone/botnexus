namespace BotNexus.Cli.Wizard.Steps;

/// <summary>
/// Runs an arbitrary async action (e.g., write a file, call an API). The action
/// receives the wizard context and can modify it. Always continues to the next step.
/// </summary>
public sealed class ActionStep : IWizardStep
{
    private readonly Func<WizardContext, CancellationToken, Task> _action;

    public string Name { get; }

    public ActionStep(string name, Func<WizardContext, CancellationToken, Task> action)
    {
        Name = name;
        _action = action;
    }

    public async Task<StepResult> ExecuteAsync(WizardContext context, CancellationToken cancellationToken)
    {
        await _action(context, cancellationToken);
        return StepResult.Continue();
    }
}
