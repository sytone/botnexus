using Spectre.Console;

namespace BotNexus.Cli.Wizard.Steps;

/// <summary>
/// Presents a multi-selection list and stores the chosen values in the wizard context.
/// </summary>
public sealed class MultiSelectionStep<T> : IWizardStep where T : notnull
{
    private readonly IAnsiConsole _console;
    private readonly string _prompt;
    private readonly string _contextKey;
    private readonly List<T> _choices;
    private readonly Func<T, string>? _displayConverter;
    private readonly bool _required;

    public string Name { get; }

    public MultiSelectionStep(
        IAnsiConsole console,
        string name,
        string prompt,
        string contextKey,
        IEnumerable<T> choices,
        Func<T, string>? displayConverter = null,
        bool required = false)
    {
        _console = console;
        Name = name;
        _prompt = prompt;
        _contextKey = contextKey;
        _choices = choices.ToList();
        _displayConverter = displayConverter;
        _required = required;
    }

    public Task<StepResult> ExecuteAsync(WizardContext context, CancellationToken cancellationToken)
    {
        var prompt = new MultiSelectionPrompt<T>()
            .Title(_prompt)
            .AddChoices(_choices)
            .Required(_required);

        if (_displayConverter is not null)
            prompt.UseConverter(_displayConverter);

        var selected = _console.Prompt(prompt);
        context.Set(_contextKey, selected);

        return Task.FromResult(StepResult.Continue());
    }
}
