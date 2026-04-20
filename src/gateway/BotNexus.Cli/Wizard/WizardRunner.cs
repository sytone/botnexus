using Spectre.Console;

namespace BotNexus.Cli.Wizard;

public enum WizardOutcome
{
    Completed,
    Aborted
}

public sealed record WizardResult(WizardOutcome Outcome, WizardContext Context);

/// <summary>
/// Runs a sequence of <see cref="IWizardStep"/> instances, threading a shared
/// <see cref="WizardContext"/> through each. Steps execute in order unless a step
/// returns <see cref="StepOutcome.GoTo"/> to jump to a named step.
/// </summary>
public sealed class WizardRunner
{
    private readonly List<IWizardStep> _steps = [];
    private readonly IAnsiConsole _console;

    public WizardRunner(IAnsiConsole? console = null)
    {
        _console = console ?? AnsiConsole.Console;
    }

    public IAnsiConsole Console => _console;

    public WizardRunner AddStep(IWizardStep step)
    {
        _steps.Add(step);
        return this;
    }

    public async Task<WizardResult> RunAsync(WizardContext? context = null, CancellationToken cancellationToken = default)
    {
        context ??= new WizardContext();
        var stepIndex = 0;

        while (stepIndex < _steps.Count)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var step = _steps[stepIndex];
            var result = await step.ExecuteAsync(context, cancellationToken);

            switch (result.Outcome)
            {
                case StepOutcome.Continue:
                    stepIndex++;
                    break;

                case StepOutcome.GoTo:
                    var target = _steps.FindIndex(s =>
                        string.Equals(s.Name, result.GoToStep, StringComparison.OrdinalIgnoreCase));
                    if (target < 0)
                        throw new InvalidOperationException(
                            $"Step '{step.Name}' requested GoTo '{result.GoToStep}', but no step with that name exists.");
                    stepIndex = target;
                    break;

                case StepOutcome.Abort:
                    return new WizardResult(WizardOutcome.Aborted, context);

                default:
                    throw new InvalidOperationException($"Unknown step outcome: {result.Outcome}");
            }
        }

        return new WizardResult(WizardOutcome.Completed, context);
    }
}
