namespace BotNexus.Cli.Wizard.Steps;

/// <summary>
/// Runs an internal check or validation (no user interaction). The delegate inspects
/// the current context and returns a <see cref="StepResult"/> to control flow.
/// </summary>
public sealed class CheckStep : IWizardStep
{
    private readonly Func<WizardContext, CancellationToken, Task<StepResult>> _check;

    public string Name { get; }

    public CheckStep(string name, Func<WizardContext, CancellationToken, Task<StepResult>> check)
    {
        Name = name;
        _check = check;
    }

    public Task<StepResult> ExecuteAsync(WizardContext context, CancellationToken cancellationToken) =>
        _check(context, cancellationToken);
}
