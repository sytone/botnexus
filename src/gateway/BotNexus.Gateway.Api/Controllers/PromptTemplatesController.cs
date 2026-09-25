using BotNexus.Cron.Prompts;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Security;
using Microsoft.AspNetCore.Mvc;

namespace BotNexus.Gateway.Api.Controllers;

/// <summary>Provides bounded, agent-scoped prompt-template catalogue and preview operations.</summary>
[ApiController]
[Route("api/prompt-templates")]
public sealed class PromptTemplatesController(IPromptTemplateResolver resolver) : ControllerBase
{
    /// <summary>Caps catalogue responses to prevent unbounded discovery payloads.</summary>
    public const int MaximumCatalogueSize = 100;
    /// <summary>Caps parameter-map cardinality before resolution work begins.</summary>
    public const int MaximumParameterCount = 50;
    /// <summary>Caps each preview input while preserving rendered output exactly.</summary>
    public const int MaximumParameterValueLength = 16_384;
    /// <summary>Caps parameters projected for each catalogue entry.</summary>
    public const int MaximumCatalogueParameterCount = 50;
    /// <summary>Caps human-readable catalogue descriptions.</summary>
    public const int MaximumDescriptionLength = 1_024;
    /// <summary>Caps catalogue defaults without exposing unbounded values.</summary>
    public const int MaximumDefaultValueLength = 4_096;
    /// <summary>Caps rendered preview output at the HTTP boundary.</summary>
    public const int MaximumRenderedOutputLength = 262_144;
    private const int MaximumTemplateNameLength = 200;
    private const int MaximumParameterNameLength = 200;

    /// <summary>Lists safe metadata for templates visible in the requested agent scope.</summary>
    [HttpGet]
    public ActionResult<IReadOnlyList<PromptTemplateDescriptor>> List(
        [FromQuery] string agentId,
        [FromQuery] int limit = MaximumCatalogueSize)
    {
        if (!TryAuthorizeAndParseAgent(agentId, out var typedAgent, out var failure))
            return failure!;
        if (limit is <= 0 or > MaximumCatalogueSize)
            return BadRequest(new { error = $"Limit must be between 1 and {MaximumCatalogueSize}." });

        var templates = resolver.ListTemplates(typedAgent!.Value, limit)
            .Select(template => new PromptTemplateDescriptorResponse(
                template.Name,
                Truncate(template.Description, MaximumDescriptionLength),
                ToWireValue(template.Source),
                template.Parameters.Take(MaximumCatalogueParameterCount).Select(parameter => new PromptTemplateParameterResponse(
                    parameter.Name,
                    Truncate(parameter.Description, MaximumDescriptionLength),
                    Truncate(parameter.Default, MaximumDefaultValueLength),
                    parameter.Required)).ToList(),
                template.ShadowedSources.Select(ToWireValue).ToList()))
            .ToList();
        return Ok(templates);
    }

    /// <summary>Renders a template preview with structured field errors for missing values.</summary>
    [HttpPost("render")]
    public ActionResult<PromptTemplateRenderResponse> Render(
        [FromQuery] string agentId,
        [FromBody] PromptTemplateRenderRequest request)
    {
        if (!TryAuthorizeAndParseAgent(agentId, out var typedAgent, out var failure))
            return failure!;
        if (request is null || string.IsNullOrWhiteSpace(request.TemplateName) || request.TemplateName.Length > MaximumTemplateNameLength)
            return BadRequest(new { error = $"TemplateName is required and must be {MaximumTemplateNameLength} characters or fewer." });
        var parameters = request.Parameters ?? new Dictionary<string, string?>();
        if (parameters.Count > MaximumParameterCount)
            return BadRequest(new { error = $"At most {MaximumParameterCount} parameters are allowed." });
        if (parameters.Any(pair => string.IsNullOrWhiteSpace(pair.Key) || pair.Key.Length > MaximumParameterNameLength))
            return BadRequest(new { error = $"Parameter names must not be blank or exceed {MaximumParameterNameLength} characters." });
        if (parameters.Any(pair => pair.Value?.Length > MaximumParameterValueLength))
            return BadRequest(new { error = $"Parameter values must be {MaximumParameterValueLength} characters or fewer." });

        var result = resolver.Render(typedAgent!.Value, request.TemplateName, parameters);
        if (result.MissingRequiredParameters.Count > 0)
        {
            var errors = result.MissingRequiredParameters.ToDictionary(
                name => $"parameters.{name}",
                _ => new[] { "A value is required." },
                StringComparer.OrdinalIgnoreCase);
            return BadRequest(new ValidationProblemDetails(errors));
        }

        if (!result.Succeeded)
            return BadRequest(new { error = result.Error });
        if (result.RenderedPrompt.Length > MaximumRenderedOutputLength)
            return BadRequest(new { error = "The rendered prompt is too large." });

        return Ok(new PromptTemplateRenderResponse(result.RenderedPrompt, result.UnknownParameterPolicy));
    }

    private static string? Truncate(string? value, int maximumLength)
        => value is null || value.Length <= maximumLength ? value : value[..maximumLength];

    private static string ToWireValue(PromptTemplateSource source) => source switch
    {
        PromptTemplateSource.Configured => "configured",
        PromptTemplateSource.Shared => "shared",
        PromptTemplateSource.Agent => "agent",
        PromptTemplateSource.Workspace => "workspace",
        _ => throw new ArgumentOutOfRangeException(nameof(source), source, null)
    };

    private bool TryAuthorizeAndParseAgent(string? agentId, out AgentId? typedAgent, out ActionResult? failure)
    {
        typedAgent = null;
        if (string.IsNullOrWhiteSpace(agentId) || agentId.Trim().Length > 200)
        {
            failure = BadRequest(new { error = "AgentId is required and must be 200 characters or fewer." });
            return false;
        }

        if (HttpContext?.Items.TryGetValue(GatewayAuthMiddleware.CallerIdentityItemKey, out var value) == true
            && value is GatewayCallerIdentity identity
            && !identity.IsAdmin
            && identity.AllowedAgents.Count > 0
            && !identity.AllowedAgents.Any(allowed => string.Equals(allowed, agentId, StringComparison.OrdinalIgnoreCase)))
        {
            failure = StatusCode(StatusCodes.Status403Forbidden, new { error = "Caller is not authorized for the requested agent." });
            return false;
        }

        typedAgent = AgentId.From(agentId);
        failure = null;
        return true;
    }
}

/// <summary>Template preview request received at the API boundary.</summary>
public sealed record PromptTemplateRenderRequest(string TemplateName, IReadOnlyDictionary<string, string?> Parameters);

/// <summary>Safe wire descriptor using stable lowercase source names rather than enum ordinals.</summary>
public sealed record PromptTemplateDescriptorResponse(
    string Name,
    string? Description,
    string Source,
    IReadOnlyList<PromptTemplateParameterResponse> Parameters,
    IReadOnlyList<string> ShadowedSources);

/// <summary>Safe wire parameter metadata.</summary>
public sealed record PromptTemplateParameterResponse(string Name, string? Description, string? Default, bool Required);

/// <summary>Successful template preview preserving the rendered text exactly.</summary>
public sealed record PromptTemplateRenderResponse(
    string RenderedPrompt,
    PromptTemplateUnknownParameterPolicy UnknownParameterPolicy);
