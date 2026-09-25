using BotNexus.Domain.Primitives;

namespace BotNexus.Cron.Prompts;

/// <summary>Resolves named prompt templates for runtime execution and safe catalogue projection.</summary>
public interface IPromptTemplateResolver
{
    /// <summary>Lists safe metadata for effective templates available to an agent.</summary>
    IReadOnlyList<PromptTemplateDescriptor> ListTemplates(AgentId agentId, int limit);

    /// <summary>Renders the effective named template without exposing its source path or body on failure.</summary>
    PromptTemplateRenderResult Render(AgentId agentId, string templateName, IReadOnlyDictionary<string, string?>? parameters);

    /// <summary>Lists discovered template names available to the specified agent.</summary>
    IReadOnlyList<string> ListTemplateNames(AgentId agentId);

    /// <summary>Renders a named template while preserving the legacy boolean contract.</summary>
    bool TryRender(AgentId agentId, string templateName, IReadOnlyDictionary<string, string?>? parameters, out string renderedPrompt, out string? error);
}

/// <summary>Identifies the precedence layer supplying an effective prompt template.</summary>
public enum PromptTemplateSource
{
    Configured,
    Shared,
    Agent,
    Workspace
}

/// <summary>Describes one parameter without exposing template content.</summary>
public sealed record PromptTemplateParameterDescriptor(string Name, string? Description, string? Default, bool Required);

/// <summary>Safe catalogue projection of an effective prompt template.</summary>
public sealed record PromptTemplateDescriptor(
    string Name,
    string? Description,
    PromptTemplateSource Source,
    IReadOnlyList<PromptTemplateSource> ShadowedSources,
    IReadOnlyList<PromptTemplateParameterDescriptor> Parameters);

/// <summary>States how render handles supplied keys absent from template metadata and placeholders.</summary>
public enum PromptTemplateUnknownParameterPolicy
{
    Ignore
}

/// <summary>Structured result shared by API preview and scheduled execution resolution.</summary>
public sealed record PromptTemplateRenderResult
{
    private PromptTemplateRenderResult(bool succeeded, string renderedPrompt, string error, IReadOnlyList<string> missing)
    {
        Succeeded = succeeded;
        RenderedPrompt = renderedPrompt;
        Error = error;
        MissingRequiredParameters = missing;
    }

    /// <summary>Indicates whether rendering produced a prompt.</summary>
    public bool Succeeded { get; }
    /// <summary>Contains the exact rendered text on success.</summary>
    public string RenderedPrompt { get; }
    /// <summary>Contains a safe client-facing error on failure, or an empty string on success.</summary>
    public string Error { get; }
    /// <summary>Names missing fields without including supplied values or template content.</summary>
    public IReadOnlyList<string> MissingRequiredParameters { get; }
    /// <summary>Unknown inputs are accepted but never appended to rendered output.</summary>
    public PromptTemplateUnknownParameterPolicy UnknownParameterPolicy => PromptTemplateUnknownParameterPolicy.Ignore;

    /// <summary>Creates a successful render result.</summary>
    public static PromptTemplateRenderResult Success(string renderedPrompt) => new(true, renderedPrompt, string.Empty, []);
    /// <summary>Creates a safe general failure result.</summary>
    public static PromptTemplateRenderResult Failure(string error) => new(false, string.Empty, error, []);
    /// <summary>Creates a structured missing-field result.</summary>
    public static PromptTemplateRenderResult MissingRequired(IReadOnlyList<string> names)
        => new(false, string.Empty, "One or more required template parameters are missing.", names);
}
