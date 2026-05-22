using System.Net;
using System.Text;
using System.Text.Json;
using Bunit;
using Bunit.TestDoubles;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Components;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests.Components;

public sealed class AgentExtensionConfigBlockTests : IDisposable
{
    private readonly BunitContext _ctx = new();

    public void Dispose() => _ctx.Dispose();

    private static AgentExtensionConfigBlock.ExtensionConfigFieldSchemaDto Field(
        string id, string type = "string", string? description = null, bool required = false, bool sensitive = false, string? def = null)
        => new() { Id = id, Type = type, Description = description, Required = required, Sensitive = sensitive, Default = def };

    // ──────────────────────────────────────────────────────────────────────
    //  No schema fields -> nothing rendered
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void NoSchemaFields_RendersNothing()
    {
        RegisterApi(HttpStatusCode.NoContent, "");

        var cut = _ctx.Render<AgentExtensionConfigBlock>(p => p
            .Add(c => c.AgentId, "agent-a")
            .Add(c => c.ExtensionId, "botnexus-skills")
            .Add(c => c.Schema, Array.Empty<AgentExtensionConfigBlock.ExtensionConfigFieldSchemaDto>()));

        cut.Markup.Trim().ShouldBe("");
    }

    // ──────────────────────────────────────────────────────────────────────
    //  Has schema -> block header rendered
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void WithSchemaFields_RendersBlockHeader()
    {
        RegisterApi(HttpStatusCode.NoContent, "");

        var cut = _ctx.Render<AgentExtensionConfigBlock>(p => p
            .Add(c => c.AgentId, "agent-a")
            .Add(c => c.ExtensionId, "botnexus-skills")
            .Add(c => c.ExtensionName, "BotNexus Skills")
            .Add(c => c.Schema, new[] { Field("skillsPath") }));

        cut.Find(".ext-agent-block-header").ShouldNotBeNull();
        cut.Find(".ext-agent-block-name").TextContent.ShouldContain("BotNexus Skills");
    }

    // ──────────────────────────────────────────────────────────────────────
    //  Collapsed by default -> body not shown
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void CollapsedByDefault_BodyNotRendered()
    {
        RegisterApi(HttpStatusCode.NoContent, "");

        var cut = _ctx.Render<AgentExtensionConfigBlock>(p => p
            .Add(c => c.AgentId, "agent-a")
            .Add(c => c.ExtensionId, "botnexus-skills")
            .Add(c => c.Schema, new[] { Field("skillsPath") }));

        cut.FindAll(".ext-agent-block-body").Count.ShouldBe(0);
    }

    // ──────────────────────────────────────────────────────────────────────
    //  Clicking header expands body and shows fields
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void ClickHeader_ExpandsBody_AndShowsFields()
    {
        RegisterApi(HttpStatusCode.NoContent, "");

        var cut = _ctx.Render<AgentExtensionConfigBlock>(p => p
            .Add(c => c.AgentId, "agent-a")
            .Add(c => c.ExtensionId, "botnexus-skills")
            .Add(c => c.Schema, new[] { Field("skillsPath", description: "Path to skills") }));

        cut.Find(".ext-agent-block-header").Click();

        cut.WaitForAssertion(() =>
        {
            cut.Find(".ext-agent-block-body").ShouldNotBeNull();
            cut.Find(".ext-agent-field-label").TextContent.ShouldContain("skillsPath");
        });
    }

    // ──────────────────────────────────────────────────────────────────────
    //  Sensitive field uses password input
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void SensitiveField_RendersPasswordInput()
    {
        RegisterApi(HttpStatusCode.NoContent, "");

        var cut = _ctx.Render<AgentExtensionConfigBlock>(p => p
            .Add(c => c.AgentId, "agent-a")
            .Add(c => c.ExtensionId, "botnexus-skills")
            .Add(c => c.Schema, new[] { Field("apiKey", sensitive: true) }));

        cut.Find(".ext-agent-block-header").Click();

        cut.WaitForAssertion(() =>
        {
            cut.Find("input[type=password]").ShouldNotBeNull();
            cut.Find(".ext-sensitive-badge").ShouldNotBeNull();
        });
    }

    // ──────────────────────────────────────────────────────────────────────
    //  Existing config is loaded and pre-populated into fields
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void ExistingConfig_IsPrePopulated()
    {
        var json = "{\"skillsPath\": \"/custom/path\"}";
        RegisterApi(HttpStatusCode.OK, json);

        var cut = _ctx.Render<AgentExtensionConfigBlock>(p => p
            .Add(c => c.AgentId, "agent-a")
            .Add(c => c.ExtensionId, "botnexus-skills")
            .Add(c => c.Schema, new[] { Field("skillsPath") }));

        cut.Find(".ext-agent-block-header").Click();

        cut.WaitForAssertion(() =>
        {
            var input = cut.Find("input[type=text]");
            input.GetAttribute("value").ShouldBe("/custom/path");
        });
    }

    // ──────────────────────────────────────────────────────────────────────
    //  Error response from API shows error text
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void ApiError_ShowsErrorMessage()
    {
        RegisterApi(HttpStatusCode.InternalServerError, "");

        var cut = _ctx.Render<AgentExtensionConfigBlock>(p => p
            .Add(c => c.AgentId, "agent-a")
            .Add(c => c.ExtensionId, "botnexus-skills")
            .Add(c => c.Schema, new[] { Field("skillsPath") }));

        cut.Find(".ext-agent-block-header").Click();

        cut.WaitForAssertion(() =>
        {
            cut.Find(".agents-error").ShouldNotBeNull();
        });
    }

    // ──────────────────────────────────────────────────────────────────────
    //  Helpers
    // ──────────────────────────────────────────────────────────────────────

    private void RegisterApi(HttpStatusCode statusCode, string body)
    {
        var handler = new FakeHttpHandler(statusCode, body);
        _ctx.Services.AddSingleton<HttpClient>(_ => new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") });
    }

    private sealed class FakeHttpHandler(HttpStatusCode statusCode, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
    }
}
