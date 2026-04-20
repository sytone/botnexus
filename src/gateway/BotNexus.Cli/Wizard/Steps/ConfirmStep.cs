using Spectre.Console;

namespace BotNexus.Cli.Wizard.Steps;

/// <summary>
/// Asks a yes/no confirmation question and stores the boolean result in the wizard context.
/// </summary>
public sealed class ConfirmStep : IWizardStep
{
    private readonly IAnsiConsole _console;
    private readonly string _prompt;
    private readonly string _contextKey;
    private readonly bool _defaultValue;

    public string Name { get; }

    public ConfirmStep(
        IAnsiConsole console,
        string name,
        string prompt,
        string contextKey,
        bool defaultValue = true)
    {
        _console = console;
        Name = name;
        _prompt = prompt;
        _contextKey = contextKey;
        _defaultValue = defaultValue;
    }

    public Task<StepResult> ExecuteAsync(WizardContext context, CancellationToken cancellationToken)
    {
        var confirmed = _console.Confirm(_prompt, _defaultValue);
        context.Set(_contextKey, confirmed);

        return Task.FromResult(StepResult.Continue());
    }
}
