using System.Text.Json;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Configuration;
using BotNexus.Gateway.Tools;
using Moq;

namespace BotNexus.Gateway.Tests.Tools;

/// <summary>
/// Issue #3596 acceptance suite for the agent-maintained <c>summary</c> field: the self-only write
/// policy, the length bound, and the "absent means absent" projection contract.
/// </summary>
/// <remarks>
/// The descriptor/persistence round trip (AC2) is proven separately by
/// <c>PlatformConfigAgentRoundTripTests</c>, which exercises the real writer, the real config file
/// and a fresh source built from disk only - a genuine restart simulation rather than a mock.
/// </remarks>
public sealed class AgentSummaryTests
{
    private static AgentDescriptor MakeDescriptor(string id, string? summary = null) =>
        new()
        {
            AgentId = AgentId.From(id),
            DisplayName = "Test Agent",
            ModelId = "test-model",
            ApiProvider = "test",
            Summary = summary
        };

    private static (Mock<IAgentRegistry> registry, Mock<IAgentConfigurationWriter> writer, Mock<IAgentChangeNotifier> notifier)
        MakeDeps(string existingId)
    {
        var registry = new Mock<IAgentRegistry>();
        var writer = new Mock<IAgentConfigurationWriter>();
        var notifier = new Mock<IAgentChangeNotifier>();

        registry.Setup(r => r.Contains(AgentId.From(existingId))).Returns(true);
        registry.Setup(r => r.Get(AgentId.From(existingId))).Returns(MakeDescriptor(existingId));
        registry.Setup(r => r.Update(AgentId.From(existingId), It.IsAny<AgentDescriptor>())).Returns(true);
        writer.Setup(w => w.SaveAsync(It.IsAny<AgentDescriptor>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        notifier.Setup(n => n.NotifyAgentsChangedAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        return (registry, writer, notifier);
    }

    private static IReadOnlyDictionary<string, object?> Args(params (string key, object? value)[] pairs) =>
        pairs.ToDictionary(p => p.key, p => p.value);

    // ------------------------------------------------------------------
    // AC1 - Summary is settable; Description remains init-only.
    // ------------------------------------------------------------------

    [Fact]
    public void Descriptor_SummaryIsSettable_AndDescriptionRemainsInitOnly()
    {
        var summarySetter = typeof(AgentDescriptor).GetProperty(nameof(AgentDescriptor.Summary))!.SetMethod!;
        var descriptionSetter = typeof(AgentDescriptor).GetProperty(nameof(AgentDescriptor.Description))!.SetMethod!;

        static bool IsInitOnly(System.Reflection.MethodInfo setter) => setter.ReturnParameter
            .GetRequiredCustomModifiers()
            .Any(t => t.FullName == "System.Runtime.CompilerServices.IsExternalInit");

        IsInitOnly(summarySetter).ShouldBeFalse(
            "Summary is agent-owned and must be mutable, unlike the human-owned Description.");
        IsInitOnly(descriptionSetter).ShouldBeTrue(
            "#3596 must not change the ownership or editability of the existing Description.");
    }

    // ------------------------------------------------------------------
    // AC4 - self-write allowed, cross-agent write refused.
    // ------------------------------------------------------------------

    [Fact]
    public async Task UpdateAgent_SelfSummaryWrite_IsPersistedAndApplied()
    {
        var (registry, writer, notifier) = MakeDeps("farnsworth");
        var tool = new UpdateAgentTool(
            registry.Object, writer.Object, [notifier.Object],
            modelRegistry: null,
            callerAgentId: AgentId.From("farnsworth"));

        var result = await tool.ExecuteAsync("t1", Args(
            ("id", "farnsworth"),
            ("summary", "Shipping platform fixes and triaging issues.")));

        result.Content[0].Value.ShouldNotContain("error");
        writer.Verify(w => w.SaveAsync(
            It.Is<AgentDescriptor>(d => d.Summary == "Shipping platform fixes and triaging issues."),
            It.IsAny<CancellationToken>()), Times.Once);
        registry.Verify(r => r.Update(
            AgentId.From("farnsworth"),
            It.Is<AgentDescriptor>(d => d.Summary == "Shipping platform fixes and triaging issues.")), Times.Once);
    }

    [Fact]
    public async Task UpdateAgent_PeerSummaryWrite_IsRefusedWithPolicyDenial()
    {
        var (registry, writer, notifier) = MakeDeps("nova");
        var tool = new UpdateAgentTool(
            registry.Object, writer.Object, [notifier.Object],
            modelRegistry: null,
            callerAgentId: AgentId.From("farnsworth"));

        var result = await tool.ExecuteAsync("t1", Args(
            ("id", "nova"),
            ("summary", "Nova now reports to me.")));

        var text = result.Content[0].Value;
        text.ShouldContain("error");
        text.ShouldContain("Policy denial");
        writer.Verify(w => w.SaveAsync(It.IsAny<AgentDescriptor>(), It.IsAny<CancellationToken>()), Times.Never);
        registry.Verify(r => r.Update(It.IsAny<AgentId>(), It.IsAny<AgentDescriptor>()), Times.Never);
    }

    [Fact]
    public async Task UpdateAgent_UnattributedCaller_CannotWriteAnySummary()
    {
        // No caller identity => the tool cannot prove self-ownership, so it refuses rather than
        // defaulting to permit. Fail-closed, not fail-open.
        var (registry, writer, notifier) = MakeDeps("farnsworth");
        var tool = new UpdateAgentTool(registry.Object, writer.Object, [notifier.Object]);

        var result = await tool.ExecuteAsync("t1", Args(
            ("id", "farnsworth"),
            ("summary", "Anyone can write this.")));

        result.Content[0].Value.ShouldContain("Policy denial");
        writer.Verify(w => w.SaveAsync(It.IsAny<AgentDescriptor>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task UpdateAgent_PeerUpdateWithoutSummaryArgument_StillSucceeds()
    {
        // The denial is scoped to the summary ARGUMENT: every other field keeps its existing reach.
        var (registry, writer, notifier) = MakeDeps("nova");
        var tool = new UpdateAgentTool(
            registry.Object, writer.Object, [notifier.Object],
            modelRegistry: null,
            callerAgentId: AgentId.From("farnsworth"));

        var result = await tool.ExecuteAsync("t1", Args(
            ("id", "nova"),
            ("description", "Updated by a peer, as before.")));

        result.Content[0].Value.ShouldNotContain("error");
        writer.Verify(w => w.SaveAsync(It.IsAny<AgentDescriptor>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    // ------------------------------------------------------------------
    // AC5 - deterministic length boundary.
    // ------------------------------------------------------------------

    [Fact]
    public async Task UpdateAgent_SummaryAtExactlyMaxLength_IsAccepted()
    {
        var options = new AgentSummaryOptions { MaxLength = 50 };
        var (registry, writer, notifier) = MakeDeps("farnsworth");
        var tool = new UpdateAgentTool(
            registry.Object, writer.Object, [notifier.Object],
            modelRegistry: null,
            callerAgentId: AgentId.From("farnsworth"),
            summaryOptions: options);

        var atLimit = new string('a', 50);
        var result = await tool.ExecuteAsync("t1", Args(("id", "farnsworth"), ("summary", atLimit)));

        result.Content[0].Value.ShouldNotContain("error");
        writer.Verify(w => w.SaveAsync(
            It.Is<AgentDescriptor>(d => d.Summary == atLimit), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task UpdateAgent_SummaryOneOverMaxLength_IsRefusedAndNotTruncated()
    {
        var options = new AgentSummaryOptions { MaxLength = 50 };
        var (registry, writer, notifier) = MakeDeps("farnsworth");
        var tool = new UpdateAgentTool(
            registry.Object, writer.Object, [notifier.Object],
            modelRegistry: null,
            callerAgentId: AgentId.From("farnsworth"),
            summaryOptions: options);

        var overLimit = new string('a', 51);
        var result = await tool.ExecuteAsync("t1", Args(("id", "farnsworth"), ("summary", overLimit)));

        var text = result.Content[0].Value;
        text.ShouldContain("error");
        text.ShouldContain("51");
        text.ShouldContain("50");
        // Refused, not silently truncated: nothing at all reaches persistence.
        writer.Verify(w => w.SaveAsync(It.IsAny<AgentDescriptor>(), It.IsAny<CancellationToken>()), Times.Never);
        registry.Verify(r => r.Update(It.IsAny<AgentId>(), It.IsAny<AgentDescriptor>()), Times.Never);
    }

    [Fact]
    public async Task UpdateAgent_EmptySummary_ClearsTheField()
    {
        var (registry, writer, notifier) = MakeDeps("farnsworth");
        var tool = new UpdateAgentTool(
            registry.Object, writer.Object, [notifier.Object],
            modelRegistry: null,
            callerAgentId: AgentId.From("farnsworth"));

        var result = await tool.ExecuteAsync("t1", Args(("id", "farnsworth"), ("summary", "")));

        result.Content[0].Value.ShouldNotContain("error");
        writer.Verify(w => w.SaveAsync(
            It.Is<AgentDescriptor>(d => d.Summary == null), It.IsAny<CancellationToken>()), Times.Once);
    }

    // ------------------------------------------------------------------
    // AC6 - an agent with no summary renders exactly as it does today.
    // ------------------------------------------------------------------

    [Fact]
    public async Task ListAgents_AgentWithoutSummary_EmitsNoSummaryKeyAtAll()
    {
        var registry = new Mock<IAgentRegistry>();
        registry.Setup(r => r.GetAll()).Returns([MakeDescriptor("plain")]);
        registry.Setup(r => r.Get(AgentId.From("caller"))).Returns((AgentDescriptor?)null);
        var tool = new ListAgentsTool(registry.Object, AgentId.From("caller"));

        var result = await tool.ExecuteAsync("t1", Args());
        using var doc = JsonDocument.Parse(result.Content[0].Value!);
        var entry = doc.RootElement[0];

        entry.TryGetProperty("summary", out _).ShouldBeFalse(
            "An agent that has never written a summary must render exactly as before - no empty " +
            "field and no placeholder in every peer's discovery payload.");
    }

    [Fact]
    public async Task ListAgents_AgentWithSummary_ProjectsItAlongsideDescription()
    {
        var descriptor = MakeDescriptor("busy", "Currently running the maintenance loop.") with
        {
            Description = "Static human-written description."
        };
        var registry = new Mock<IAgentRegistry>();
        registry.Setup(r => r.GetAll()).Returns([descriptor]);
        registry.Setup(r => r.Get(AgentId.From("caller"))).Returns((AgentDescriptor?)null);
        var tool = new ListAgentsTool(registry.Object, AgentId.From("caller"));

        var result = await tool.ExecuteAsync("t1", Args());
        using var doc = JsonDocument.Parse(result.Content[0].Value!);
        var entry = doc.RootElement[0];

        entry.GetProperty("summary").GetString().ShouldBe("Currently running the maintenance loop.");
        entry.GetProperty("description").GetString().ShouldBe("Static human-written description.");
    }
}
