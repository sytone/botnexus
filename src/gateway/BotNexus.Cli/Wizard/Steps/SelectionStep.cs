using Spectre.Console;

namespace BotNexus.Cli.Wizard.Steps;

/// <summary>
/// Presents a single-selection list and stores the chosen value in the wizard context.
/// </summary>
public sealed class SelectionStep<T> : IWizardStep where T : notnull
{
    private readonly IAnsiConsole _console;
    private readonly string _prompt;
    private readonly string _contextKey;
    private readonly List<T> _choices;
    private readonly Func<T, string>? _displayConverter;

    public string Name { get; }

    public SelectionStep(
        IAnsiConsole console,
        string name,
        string prompt,
        string contextKey,
        IEnumerable<T> choices,
        Func<T, string>? displayConverter = null)
    {
        _console = console;
        Name = name;
        _prompt = prompt;
        _contextKey = contextKey;
        _choices = choices.ToList();
        _displayConverter = displayConverter;
    }

    public Task<StepResult> ExecuteAsync(WizardContext context, CancellationToken cancellationToken)
    {
        var prompt = new SelectionPrompt<T>()
            .Title(_prompt)
            .AddChoices(_choices);

        if (_displayConverter is not null)
            prompt.UseConverter(_displayConverter);

        var selected = _console.Prompt(prompt);
        context.Set(_contextKey, selected);

        return Task.FromResult(StepResult.Continue());
    }
}
