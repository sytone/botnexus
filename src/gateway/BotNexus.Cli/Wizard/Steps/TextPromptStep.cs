using Spectre.Console;

namespace BotNexus.Cli.Wizard.Steps;

/// <summary>
/// Prompts the user for text input and stores the result in the wizard context.
/// </summary>
public sealed class TextPromptStep : IWizardStep
{
    private readonly IAnsiConsole _console;
    private readonly string _prompt;
    private readonly string _contextKey;
    private readonly string? _defaultValue;
    private readonly bool _secret;
    private readonly Func<string, ValidationResult>? _validator;

    public string Name { get; }

    public TextPromptStep(
        IAnsiConsole console,
        string name,
        string prompt,
        string contextKey,
        string? defaultValue = null,
        bool secret = false,
        Func<string, ValidationResult>? validator = null)
    {
        _console = console;
        Name = name;
        _prompt = prompt;
        _contextKey = contextKey;
        _defaultValue = defaultValue;
        _secret = secret;
        _validator = validator;
    }

    public Task<StepResult> ExecuteAsync(WizardContext context, CancellationToken cancellationToken)
    {
        var prompt = new TextPrompt<string>(_prompt);

        if (_defaultValue is not null)
            prompt.DefaultValue(_defaultValue);

        if (_secret)
            prompt.Secret();

        if (_validator is not null)
            prompt.Validate(_validator);

        prompt.AllowEmpty = false;

        var value = _console.Prompt(prompt);
        context.Set(_contextKey, value);

        return Task.FromResult(StepResult.Continue());
    }
}
