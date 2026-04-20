using BotNexus.Cli.Wizard.Steps;
using Spectre.Console;

namespace BotNexus.Cli.Wizard;

/// <summary>
/// Fluent builder for constructing a <see cref="WizardRunner"/> with a chain of steps.
/// </summary>
public sealed class WizardBuilder
{
    private readonly IAnsiConsole _console;
    private readonly WizardRunner _runner;

    public WizardBuilder(IAnsiConsole? console = null)
    {
        _console = console ?? AnsiConsole.Console;
        _runner = new WizardRunner(_console);
    }

    /// <summary>
    /// Adds a free-text prompt step.
    /// </summary>
    public WizardBuilder AskText(
        string name,
        string prompt,
        string contextKey,
        string? defaultValue = null,
        bool secret = false,
        Func<string, ValidationResult>? validator = null)
    {
        _runner.AddStep(new TextPromptStep(_console, name, prompt, contextKey, defaultValue, secret, validator));
        return this;
    }

    /// <summary>
    /// Adds a single-selection prompt step.
    /// </summary>
    public WizardBuilder AskSelection<T>(
        string name,
        string prompt,
        string contextKey,
        IEnumerable<T> choices,
        Func<T, string>? displayConverter = null) where T : notnull
    {
        _runner.AddStep(new SelectionStep<T>(_console, name, prompt, contextKey, choices, displayConverter));
        return this;
    }

    /// <summary>
    /// Adds a multi-selection prompt step.
    /// </summary>
    public WizardBuilder AskMultiSelection<T>(
        string name,
        string prompt,
        string contextKey,
        IEnumerable<T> choices,
        Func<T, string>? displayConverter = null,
        bool required = false) where T : notnull
    {
        _runner.AddStep(new MultiSelectionStep<T>(_console, name, prompt, contextKey, choices, displayConverter, required));
        return this;
    }

    /// <summary>
    /// Adds a yes/no confirmation step.
    /// </summary>
    public WizardBuilder AskConfirm(
        string name,
        string prompt,
        string contextKey,
        bool defaultValue = true)
    {
        _runner.AddStep(new ConfirmStep(_console, name, prompt, contextKey, defaultValue));
        return this;
    }

    /// <summary>
    /// Adds an internal check step that can branch the flow.
    /// </summary>
    public WizardBuilder Check(
        string name,
        Func<WizardContext, CancellationToken, Task<StepResult>> check)
    {
        _runner.AddStep(new CheckStep(name, check));
        return this;
    }

    /// <summary>
    /// Adds an action step that performs work and always continues.
    /// </summary>
    public WizardBuilder Action(
        string name,
        Func<WizardContext, CancellationToken, Task> action)
    {
        _runner.AddStep(new ActionStep(name, action));
        return this;
    }

    /// <summary>
    /// Adds a custom step implementation.
    /// </summary>
    public WizardBuilder Step(IWizardStep step)
    {
        _runner.AddStep(step);
        return this;
    }

    /// <summary>
    /// Builds and returns the configured wizard runner.
    /// </summary>
    public WizardRunner Build() => _runner;
}
