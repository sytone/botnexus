using System.Text.Json;
using BotNexus.Domain.Primitives;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Security;
using Shouldly;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

/// <summary>
/// #2795 AC7 (fallback option). The Agent Configuration panel cannot reference the shared contract
/// types the two endpoints serialize:
/// <list type="bullet">
/// <item><c>GET /api/agents/{id}</c> serializes <c>AgentDescriptor</c>, which lives in
/// <c>BotNexus.Domain</c> - an assembly the WASM client is structurally forbidden from referencing
/// by <c>WasmPayloadDependencyArchitectureTests</c> (it drags Vogen into the browser download).</item>
/// <item><c>GET /api/agents/{id}/sessions/{sid}/context</c> returns an <b>anonymous type</b>; there
/// is no contract type to reference at all.</item>
/// </list>
/// So instead of a shared type, these tests bind the panel's DTOs to the <b>actually serialized
/// payload</b> of the real server objects. A server-side rename now reddens a named test rather
/// than silently blanking the panel, which is exactly the #2795 failure mode.
/// <para>
/// These tests deliberately serialize the REAL types (never a hand-written JSON literal); a literal
/// would be a third spelling of the contract and could drift with the DTO in lockstep.
/// </para>
/// </summary>
public sealed class AgentConfigContractTests
{
    /// <summary>Matches the gateway's ASP.NET Core JSON defaults (camelCase).</summary>
    private static readonly JsonSerializerOptions Wire = new(JsonSerializerDefaults.Web);

    private static AgentDescriptor SampleDescriptor() => new()
    {
        AgentId = AgentId.From("farnsworth"),
        DisplayName = "Farnsworth",
        ModelId = "claude-opus-5",
        ApiProvider = "github-copilot",
        ToolIds = ["read", "write", "shell"],
        Memory = new MemoryAgentConfig { Enabled = true },
        Heartbeat = new HeartbeatAgentConfig { Enabled = true, IntervalMinutes = 45 },
        FileAccess = new FileAccessPolicy { AllowedReadPaths = ["/tmp"] },
    };

    // ── Clause 1/2: the agent descriptor payload ─────────────────────────────

    [Fact]
    public void AgentDescriptorDto_binds_model_provider_and_tools_from_the_real_serialized_descriptor()
    {
        var json = JsonSerializer.Serialize(SampleDescriptor(), Wire);

        var dto = JsonSerializer.Deserialize<AgentDescriptorDto>(json, Wire);

        dto.ShouldNotBeNull();
        // These three are the #2795 defect-1 fields. The panel previously declared Model/Provider/
        // ToolCount, none of which appear in this payload, so they silently stayed null/null/0.
        dto.ModelId.ShouldBe("claude-opus-5");
        dto.ApiProvider.ShouldBe("github-copilot");
        dto.ToolIds.ShouldNotBeNull();
        dto.ToolIds.Count.ShouldBe(3);
        dto.AgentId.ShouldBe("farnsworth");
        dto.DisplayName.ShouldBe("Farnsworth");
    }

    [Fact]
    public void AgentDescriptorDto_binds_the_nested_memory_heartbeat_and_file_access_objects()
    {
        var json = JsonSerializer.Serialize(SampleDescriptor(), Wire);

        var dto = JsonSerializer.Deserialize<AgentDescriptorDto>(json, Wire);

        dto.ShouldNotBeNull();
        dto.Memory.ShouldNotBeNull();
        dto.Memory.Enabled.ShouldBeTrue();
        dto.Heartbeat.ShouldNotBeNull();
        dto.Heartbeat.Enabled.ShouldBeTrue();
        dto.Heartbeat.IntervalMinutes.ShouldBe(45);
        dto.FileAccess.ShouldNotBeNull();
    }

    [Fact]
    public void Every_AgentDescriptorDto_property_name_exists_in_the_real_serialized_descriptor()
    {
        // Name-level fence: catches a server-side rename even for a field no assertion above reads.
        using var doc = JsonSerializer.Deserialize<JsonDocument>(
            JsonSerializer.Serialize(SampleDescriptor(), Wire), Wire)!;
        var payloadNames = doc.RootElement.EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);

        foreach (var prop in typeof(AgentDescriptorDto).GetProperties())
        {
            var wireName = JsonNamingPolicy.CamelCase.ConvertName(prop.Name);
            payloadNames.ShouldContain(
                wireName,
                $"AgentDescriptorDto.{prop.Name} maps to '{wireName}', which GET /api/agents/{{id}} does not return. " +
                "Fix the DTO to match AgentDescriptor - do not weaken this test (#2795).");
        }
    }

    // ── Clause 3: the nested context payload ─────────────────────────────────

    [Fact]
    public void ContextInfoDto_binds_system_prompt_tokens_from_the_nested_sections_shape()
    {
        var diag = new ContextDiagnostics
        {
            SystemPromptTokens = 12345,
            SystemPromptChars = 50000,
            ToolDefinitionTokens = 900,
            HistoryTokens = 400,
            TotalEstimatedTokens = 13645,
        };

        // The REAL controller response builder, not a restated literal.
        var json = JsonSerializer.Serialize(
            BotNexus.Gateway.Api.Controllers.AgentsController.BuildContextResponse("a", "s", diag), Wire);

        var dto = JsonSerializer.Deserialize<ContextInfoDto>(json, Wire);

        dto.ShouldNotBeNull();
        // #2795 defect 2: the panel used to declare a FLAT SystemPromptTokens, which never bound.
        dto.Sections.ShouldNotBeNull();
        dto.Sections.SystemPrompt.ShouldNotBeNull();
        dto.Sections.SystemPrompt.Tokens.ShouldBe(12345);
        dto.Sections.ToolDefinitions?.Tokens.ShouldBe(900);
        dto.Sections.ConversationHistory?.Tokens.ShouldBe(400);
        dto.TotalEstimatedTokens.ShouldBe(13645);
    }

    [Fact]
    public void Context_payload_does_not_expose_a_flat_system_prompt_tokens_field()
    {
        // Pins the exact mistake #2795 made: if someone "fixes" the panel by re-adding a flat
        // SystemPromptTokens, this documents that no such field exists on the wire.
        var json = JsonSerializer.Serialize(
            BotNexus.Gateway.Api.Controllers.AgentsController.BuildContextResponse(
                "a", "s", new ContextDiagnostics { SystemPromptTokens = 7 }), Wire);

        using var doc = JsonDocument.Parse(json);
        doc.RootElement.TryGetProperty("systemPromptTokens", out _).ShouldBeFalse();
        doc.RootElement.GetProperty("sections").GetProperty("systemPrompt").GetProperty("tokens").GetInt32().ShouldBe(7);
    }

    [Fact]
    public void ContextInfoDto_still_binds_when_the_context_window_is_unresolvable()
    {
        // #3091 changed contextWindowTokens/usagePercent from always-present numbers to nullable
        // values. The panel binds sections.systemPrompt.tokens and totalEstimatedTokens; a null
        // window must not break that binding, or the whole panel blanks on an unresolvable model.
        var json = JsonSerializer.Serialize(
            BotNexus.Gateway.Api.Controllers.AgentsController.BuildContextResponse(
                "a", "s", new ContextDiagnostics { SystemPromptTokens = 7, TotalEstimatedTokens = 9 }, null),
            Wire);

        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("contextWindowTokens").ValueKind.ShouldBe(JsonValueKind.Null);
        doc.RootElement.GetProperty("usagePercent").ValueKind.ShouldBe(JsonValueKind.Null);

        var dto = JsonSerializer.Deserialize<ContextInfoDto>(json, Wire);
        dto.ShouldNotBeNull();
        dto.Sections?.SystemPrompt?.Tokens.ShouldBe(7);
        dto.TotalEstimatedTokens.ShouldBe(9);
    }
}
