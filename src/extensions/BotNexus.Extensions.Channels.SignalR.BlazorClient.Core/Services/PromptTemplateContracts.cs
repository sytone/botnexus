namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;

/// <summary>Client-owned projection of a prompt template returned by the gateway catalogue.</summary>
public sealed record PromptTemplateDescriptorDto(
    string Name,
    string? Description,
    string Source,
    IReadOnlyList<PromptTemplateParameterDto> Parameters,
    IReadOnlyList<string>? ShadowedSources = null);

/// <summary>Parameter metadata needed to collect values without exposing template contents.</summary>
public sealed record PromptTemplateParameterDto(
    string Name,
    string? Description,
    string? Default,
    bool Required);

/// <summary>Portal render request; values absent from this map are resolved by the server.</summary>
public sealed record PromptTemplateRenderRequestDto(
    string TemplateName,
    IReadOnlyDictionary<string, string?> Parameters);

/// <summary>Rendered insertion text and the gateway's explicit policy for extra parameters.</summary>
public sealed record PromptTemplateRenderResponseDto(string RenderedPrompt);

/// <summary>Render result consumed by the picker, including safe field-level validation messages.</summary>
public sealed record PromptTemplateRenderResultDto(
    string? RenderedPrompt,
    IReadOnlyDictionary<string, string[]> Errors,
    string? GeneralError = null);

/// <summary>Textarea state returned after a native selection replacement.</summary>
public sealed record PromptTemplateInsertionResult(string Value, int SelectionStart, int SelectionEnd);

internal sealed record PromptTemplateValidationProblemDto(
    IReadOnlyDictionary<string, string[]>? Errors,
    string? Error);
