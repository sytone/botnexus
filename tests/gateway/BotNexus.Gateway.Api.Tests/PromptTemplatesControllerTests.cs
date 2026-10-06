using BotNexus.Cron.Prompts;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Api.Controllers;
using Microsoft.AspNetCore.Mvc;

namespace BotNexus.Gateway.Api.Tests;

public sealed class PromptTemplatesControllerTests
{
    [Fact]
    public void List_UsesAgentScopeAndEnforcesLimitBounds()
    {
        var resolver = new StubResolver();
        var controller = new PromptTemplatesController(resolver);

        controller.List("farnsworth", 0).Result.ShouldBeOfType<BadRequestObjectResult>();
        controller.List("farnsworth", PromptTemplatesController.MaximumCatalogueSize + 1)
            .Result.ShouldBeOfType<BadRequestObjectResult>();

        var ok = controller.List("farnsworth", 7).Result.ShouldBeOfType<OkObjectResult>();
        var descriptors = ok.Value.ShouldBeOfType<List<PromptTemplateDescriptorResponse>>();
        descriptors.Count.ShouldBe(1);
        descriptors[0].Source.ShouldBe("configured");
        resolver.ListAgentId.ShouldBe(AgentId.From("farnsworth"));
        resolver.ListLimit.ShouldBe(7);
    }

    [Fact]
    public void List_bounds_parameter_metadata()
    {
        var resolver = new StubResolver
        {
            Templates =
            [
                new PromptTemplateDescriptor(
                    "daily",
                    new string('d', PromptTemplatesController.MaximumDescriptionLength + 10),
                    PromptTemplateSource.Workspace,
                    [],
                    Enumerable.Range(0, PromptTemplatesController.MaximumCatalogueParameterCount + 5)
                        .Select(index => new PromptTemplateParameterDescriptor(
                            $"p{index}",
                            new string('x', PromptTemplatesController.MaximumDescriptionLength + 10),
                            new string('s', PromptTemplatesController.MaximumDefaultValueLength + 10),
                            false)).ToList())
            ]
        };
        var response = new PromptTemplatesController(resolver).List("farnsworth", 10).Result
            .ShouldBeOfType<OkObjectResult>().Value
            .ShouldBeOfType<List<PromptTemplateDescriptorResponse>>().ShouldHaveSingleItem();
        response.Description?.Length.ShouldBe(PromptTemplatesController.MaximumDescriptionLength);
        response.Parameters.Count.ShouldBe(PromptTemplatesController.MaximumCatalogueParameterCount);
        response.Parameters.All(parameter => parameter.Description is null || parameter.Description.Length <= PromptTemplatesController.MaximumDescriptionLength).ShouldBeTrue();
        response.Parameters.All(parameter => parameter.Default is null || parameter.Default.Length <= PromptTemplatesController.MaximumDefaultValueLength).ShouldBeTrue();
    }

    [Fact]
    public void Render_ReturnsFieldSpecificValidationForMissingRequiredParameters()
    {
        var resolver = new StubResolver
        {
            RenderResult = PromptTemplateRenderResult.MissingRequired(["audience", "topic"])
        };
        var controller = new PromptTemplatesController(resolver);

        var badRequest = controller.Render(
            "farnsworth",
            new PromptTemplateRenderRequest("daily", new Dictionary<string, string?>()))
            .Result.ShouldBeOfType<BadRequestObjectResult>();
        var details = badRequest.Value.ShouldBeOfType<ValidationProblemDetails>();

        details.Errors.Keys.OrderBy(key => key, StringComparer.Ordinal).ShouldBe([
            "parameters.audience",
            "parameters.topic"
        ]);
        details.Errors["parameters.audience"].ShouldBe(["A value is required."]);
        details.Errors["parameters.topic"].ShouldBe(["A value is required."]);
    }

    [Fact]
    public void Render_EnforcesRequestBoundsBeforeCallingResolver()
    {
        var resolver = new StubResolver();
        var controller = new PromptTemplatesController(resolver);
        var tooMany = Enumerable.Range(0, PromptTemplatesController.MaximumParameterCount + 1)
            .ToDictionary(index => $"p{index}", index => (string?)index.ToString());

        controller.Render("farnsworth", new PromptTemplateRenderRequest("daily", tooMany))
            .Result.ShouldBeOfType<BadRequestObjectResult>();
        resolver.RenderAgentId.ShouldBeNull();

        controller.Render(
                "farnsworth",
                new PromptTemplateRenderRequest("daily", new Dictionary<string, string?>
                {
                    ["topic"] = new string('x', PromptTemplatesController.MaximumParameterValueLength + 1)
                }))
            .Result.ShouldBeOfType<BadRequestObjectResult>();
        resolver.RenderAgentId.ShouldBeNull();
    }

    [Fact]
    public void Render_ReturnsExactRenderedTextAndDoesNotRejectUnknownParameters()
    {
        var resolver = new StubResolver
        {
            RenderResult = PromptTemplateRenderResult.Success("prefix\r\n suffix")
        };
        var controller = new PromptTemplatesController(resolver);
        var request = new PromptTemplateRenderRequest(
            "daily",
            new Dictionary<string, string?> { ["unknown"] = "ignored" });

        var ok = controller.Render("farnsworth", request).Result.ShouldBeOfType<OkObjectResult>();
        var response = ok.Value.ShouldBeOfType<PromptTemplateRenderResponse>();

        response.RenderedPrompt.ShouldBe("prefix\r\n suffix");
        response.UnknownParameterPolicy.ShouldBe(PromptTemplateUnknownParameterPolicy.Ignore);
        resolver.RenderParameters.ShouldContainKey("unknown");
    }

    [Fact]
    public void Render_rejects_oversized_output_without_leaking_it()
    {
        var resolver = new StubResolver
        {
            RenderResult = PromptTemplateRenderResult.Success(
                "SECRET:" + new string('x', PromptTemplatesController.MaximumRenderedOutputLength))
        };
        var badRequest = new PromptTemplatesController(resolver).Render(
            "farnsworth", new PromptTemplateRenderRequest("daily", new Dictionary<string, string?>()))
            .Result.ShouldBeOfType<BadRequestObjectResult>();
        var serialized = System.Text.Json.JsonSerializer.Serialize(badRequest.Value);
        serialized.ShouldContain("too large");
        serialized.ShouldNotContain("SECRET");
    }

    private sealed class StubResolver : IPromptTemplateResolver
    {
        public AgentId? ListAgentId { get; private set; }
        public int? ListLimit { get; private set; }
        public AgentId? RenderAgentId { get; private set; }
        public IReadOnlyDictionary<string, string?> RenderParameters { get; private set; }
            = new Dictionary<string, string?>();

        public PromptTemplateRenderResult RenderResult { get; init; }
            = PromptTemplateRenderResult.Success("rendered");
        public IReadOnlyList<PromptTemplateDescriptor>? Templates { get; init; }

        public IReadOnlyList<PromptTemplateDescriptor> ListTemplates(AgentId agentId, int limit)
        {
            ListAgentId = agentId;
            ListLimit = limit;
            return Templates ??
            [
                new PromptTemplateDescriptor(
                    "daily",
                    "safe description",
                    PromptTemplateSource.Configured,
                    [],
                    [])
            ];
        }

        public PromptTemplateRenderResult Render(
            AgentId agentId,
            string templateName,
            IReadOnlyDictionary<string, string?>? parameters)
        {
            RenderAgentId = agentId;
            RenderParameters = parameters ?? new Dictionary<string, string?>();
            return RenderResult;
        }

        public IReadOnlyList<string> ListTemplateNames(AgentId agentId)
            => ListTemplates(agentId, 100).Select(template => template.Name).ToList();

        public bool TryRender(
            AgentId agentId,
            string templateName,
            IReadOnlyDictionary<string, string?>? parameters,
            out string renderedPrompt,
            out string? error)
        {
            var result = Render(agentId, templateName, parameters);
            renderedPrompt = result.RenderedPrompt;
            error = result.Error;
            return result.Succeeded;
        }
    }
}
