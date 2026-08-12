using System.Text.Json;
using BotNexus.Gateway.Abstractions.Security;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Agents;
using BotNexus.Gateway.Api.Controllers;
using BotNexus.Gateway.Api.Models;
using BotNexus.Gateway.Configuration;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace BotNexus.Gateway.Tests;

/// <summary>
/// Pins the lean list projection returned by <c>GET /api/agents</c> (issue #2755).
/// </summary>
/// <remarks>
/// These tests exist because the list endpoint previously serialised the full
/// <see cref="AgentDescriptor"/> domain model. Removing the projection — i.e. returning
/// descriptors again — must redden <see cref="ListResponse_DoesNotLeakConfigurationDetail"/>
/// and <see cref="ListResponse_SerialisedSize_StaysUnderDocumentedBound"/> by name (AC5).
/// </remarks>
public sealed class AgentsListProjectionTests
{
    /// <summary>
    /// Properties that must never appear in the list response. Each is either configuration detail
    /// broadcast on a call that is unauthenticated by default (#506) or a top byte-cost contributor
    /// measured on the live gateway: fileAccess 6,290 B, systemPrompt 5,833 B,
    /// extensionConfig 5,700 B, memory 2,671 B across 18 agents.
    /// </summary>
    private static readonly string[] ForbiddenProperties =
    [
        "systemPrompt",
        "systemPromptFile",
        "systemPromptFiles",
        "fileAccess",
        "extensionConfig",
        "memory",
        "toolPolicy",
        "toolIds",
        "metadata",
        "isolationOptions",
        "heartbeat",
        "soul",
        "shellCommand",
    ];

    /// <summary>
    /// The list-view fields, matching <c>AgentSummary</c> (<c>HubContracts.cs:18-23</c>) plus the two
    /// fields <c>Pages/Agents.razor:92-93</c> renders as grid columns.
    /// </summary>
    private static readonly string[] ExpectedProperties =
    [
        "agentId",
        "displayName",
        "emoji",
        "description",
        "isBuiltIn",
        "apiProvider",
        "modelId",
    ];

    // ── AC1: the list response carries only list-view fields ────────────────────

    [Fact]
    public void ListResponse_DoesNotLeakConfigurationDetail()
    {
        var registry = new DefaultAgentRegistry(NullLogger<DefaultAgentRegistry>.Instance);
        registry.Register(CreateFatDescriptor("agent-a"));
        var controller = CreateController(registry);

        var json = SerialiseList(controller.List());

        using var doc = JsonDocument.Parse(json);
        var element = doc.RootElement[0];
        foreach (var forbidden in ForbiddenProperties)
        {
            element.TryGetProperty(forbidden, out _)
                .ShouldBeFalse($"'{forbidden}' must not appear in the GET /api/agents list response (#2755).");
        }

        // Sad path corollary: the secret value itself must be absent from the raw payload, not merely
        // absent under its own property name.
        json.ShouldNotContain(SecretSystemPrompt);
        json.ShouldNotContain("/etc/secrets");
    }

    [Fact]
    public void ListResponse_ContainsExactlyTheListViewFields()
    {
        var registry = new DefaultAgentRegistry(NullLogger<DefaultAgentRegistry>.Instance);
        registry.Register(CreateFatDescriptor("agent-a"));
        var controller = CreateController(registry);

        var json = SerialiseList(controller.List());

        using var doc = JsonDocument.Parse(json);
        var actual = doc.RootElement[0].EnumerateObject().Select(p => p.Name).OrderBy(n => n).ToArray();
        actual.ShouldBe(ExpectedProperties.OrderBy(n => n).ToArray());
    }

    [Fact]
    public void ListResponse_PreservesTheFieldsConsumersRead()
    {
        var registry = new DefaultAgentRegistry(NullLogger<DefaultAgentRegistry>.Instance);
        registry.Register(CreateFatDescriptor("agent-a"));
        var controller = CreateController(registry);

        var item = ExtractItems(controller.List()).Single();

        // PortalLoadService.cs:97-108, AgentInteractionService.cs:771-783, GatewayRestClient.cs:88-95.
        item.AgentId.ShouldBe("agent-a");
        item.DisplayName.ShouldBe("agent-a-display");
        item.Emoji.ShouldBe("🔬");
        item.Description.ShouldBe("a description");
        item.IsBuiltIn.ShouldBeFalse();
        // Pages/Agents.razor:92-93 renders these two as grid columns.
        item.ApiProvider.ShouldBe("test-provider");
        item.ModelId.ShouldBe("test-model");
    }

    // ── AC2: the per-agent endpoint still returns the full descriptor ───────────

    [Fact]
    public void GetByAgentId_StillReturnsFullDescriptor()
    {
        var registry = new DefaultAgentRegistry(NullLogger<DefaultAgentRegistry>.Instance);
        registry.Register(CreateFatDescriptor("agent-a"));
        var controller = CreateController(registry);

        var ok = controller.Get("agent-a").Result.ShouldBeOfType<OkObjectResult>();
        var descriptor = ok.Value.ShouldBeOfType<AgentDescriptor>();

        // SystemPrompt is a full-descriptor-ONLY property: it is explicitly excluded from the list
        // projection, so its presence here proves the detail endpoint was not narrowed alongside it.
        descriptor.SystemPrompt.ShouldNotBeNull();
        descriptor.SystemPrompt.ShouldStartWith(SecretSystemPrompt);

        var json = JsonSerializer.Serialize(descriptor, SerializerOptions);
        using var doc = JsonDocument.Parse(json);
        doc.RootElement.TryGetProperty("systemPrompt", out _).ShouldBeTrue(
            "GET /api/agents/{agentId} must keep serialising the full descriptor (#2755 AC2).");
    }

    // ── AC3: documented size bound ──────────────────────────────────────────────

    /// <summary>
    /// Number of agents in the size fixture. Chosen to match the live gateway population measured in
    /// the 2026-08-03 profiling run that produced issue #2755.
    /// </summary>
    private const int SizeFixtureAgentCount = 18;

    /// <summary>
    /// Upper bound, in bytes, on the uncompressed serialised list response for
    /// <see cref="SizeFixtureAgentCount"/> agents.
    /// <para>
    /// Reasoning: the same 18-agent population serialised as full descriptors measured 37,881 B; the
    /// projection measured 4,169 B (89% saving). The fixture descriptors here are deliberately
    /// fatter per agent than production (a 4 KB system prompt and a 2 KB extension config each), so
    /// an unprojected response would be far above 37 KB while the projected one is dominated by the
    /// seven short list fields. The bound is set at 6,000 B — comfortably above the ~4.2 KB measured
    /// projection so ordinary field-length growth does not cause flakes, and an order of magnitude
    /// below any response that carries systemPrompt/fileAccess/extensionConfig. Exceeding it means a
    /// field was added to <see cref="AgentListItem"/> that does not belong on a list payload.
    /// </para>
    /// </summary>
    private const int ListResponseByteBudget = 6_000;

    [Fact]
    public void ListResponse_SerialisedSize_StaysUnderDocumentedBound()
    {
        var registry = new DefaultAgentRegistry(NullLogger<DefaultAgentRegistry>.Instance);
        for (var i = 0; i < SizeFixtureAgentCount; i++)
            registry.Register(CreateFatDescriptor($"agent-{i:D2}"));
        var controller = CreateController(registry);

        var items = ExtractItems(controller.List());
        items.Count.ShouldBe(SizeFixtureAgentCount, "the size fixture must actually contain agents (non-vacuity).");

        var bytes = System.Text.Encoding.UTF8.GetByteCount(SerialiseList(controller.List()));

        bytes.ShouldBeLessThan(
            ListResponseByteBudget,
            $"GET /api/agents for {SizeFixtureAgentCount} agents serialised to {bytes} B, over the " +
            $"{ListResponseByteBudget} B budget documented on {nameof(ListResponseByteBudget)}. " +
            "Either a fat field was added to AgentListItem or the projection was removed (#2755).");
    }

    // ── helpers ─────────────────────────────────────────────────────────────────

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private static string SerialiseList(ActionResult<IReadOnlyList<AgentListItem>> result)
    {
        var ok = result.Result.ShouldBeOfType<OkObjectResult>();
        return JsonSerializer.Serialize(ok.Value, SerializerOptions);
    }

    private static IReadOnlyList<AgentListItem> ExtractItems(ActionResult<IReadOnlyList<AgentListItem>> result)
    {
        var ok = result.Result.ShouldBeOfType<OkObjectResult>();
        var items = ok.Value.ShouldBeAssignableTo<IReadOnlyList<AgentListItem>>();
        return items ?? throw new InvalidOperationException("Expected agent list.");
    }

    /// <summary>
    /// Sentinel prefix of the fixture system prompt. Asserted absent from the list payload and
    /// present on the per-agent payload, so the two AC1/AC2 clauses key off one value.
    /// </summary>
    private const string SecretSystemPrompt = "SUPER-SECRET-SYSTEM-PROMPT";

    /// <summary>
    /// A descriptor populated with the properties issue #2755 identifies as the payload cost and the
    /// disclosure risk, so a missing projection is unmissable in both the field and the size assertions.
    /// </summary>
    private static AgentDescriptor CreateFatDescriptor(string agentId) => new()
    {
        AgentId = AgentId.From(agentId),
        DisplayName = $"{agentId}-display",
        Emoji = "🔬",
        Description = "a description",
        ModelId = "test-model",
        ApiProvider = "test-provider",
        SystemPrompt = SecretSystemPrompt + new string('p', 4_000),
        ToolIds = ["read", "write", "shell", "exec", "grep", "glob"],
        FileAccess = new FileAccessPolicy
        {
            AllowedReadPaths = ["/etc/secrets", "/var/data"],
            AllowedWritePaths = ["/var/data"],
            DeniedPaths = ["/root"],
        },
        ExtensionConfig = new Dictionary<string, JsonElement>
        {
            ["bulky"] = JsonDocument.Parse($"\"{new string('x', 2_000)}\"").RootElement,
        },
    };

    private static AgentsController CreateController(IAgentRegistry registry)
    {
        var notifier = new Mock<IAgentChangeNotifier>();
        notifier
            .Setup(c => c.NotifyAgentsChangedAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        return new AgentsController(
            registry,
            Mock.Of<IAgentSupervisor>(),
            new NoOpAgentConfigurationWriter(),
            [notifier.Object]);
    }
}
