namespace BotNexus.Gateway.Abstractions.Models;

/// <summary>
/// Declares the interaction shape an agent expects when pausing for user input.
/// Channels use this to choose the correct UI (text box, buttons, or hybrid).
/// </summary>
public enum AskUserInputType
{
    /// <summary>Collect a free-form text response.</summary>
    FreeForm,

    /// <summary>Collect a single selection from predefined choices.</summary>
    SingleChoice,

    /// <summary>Collect multiple selections from predefined choices.</summary>
    MultipleChoice,

    /// <summary>Allow either selecting a predefined choice or entering custom text.</summary>
    ChoiceOrFreeForm
}
