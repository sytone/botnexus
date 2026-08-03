using System.Text.Json;
using System.IO.Abstractions.TestingHelpers;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Configuration;
using BotNexus.Gateway.Tools;
using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Agent.Providers.Core.Registry;
using Moq;

namespace BotNexus.Gateway.Tests.Tools;

/// <summary>
/// #2649: <c>create_agent</c> / <c>update_agent</c> must validate <c>apiProvider</c> against the
/// <b>model registry</b> - the same registry <c>InProcessIsolationStrategy</c> resolves the
/// descriptor against at spawn time - not against the API-contract registry. Validating against
/// the contract registry rejects the only values that work (<c>github-copilot</c>) and accepts
/// values that produce a permanently broken agent (<c>github-copilot-messages</c>).
///
/// Every rejection here is asserted to happen <b>before</b> <c>IAgentConfigurationWriter.SaveAsync</c>,
/// because the damage the issue describes is a broken agent surviving in <c>config.json</c> long
/// after the session that created it.
/// </summary>
public sealed class AgentModelPreflightTests
{
    private static readonly string HomePath = Path.Combine(Path.GetTempPath(), "preflight-tests-" + Guid.NewGuid());

    /// <summary>
    /// Mirrors the real runtime shape reported on the issue: the model registry is keyed by
    /// <b>provider instance</b> name (<c>github-copilot</c>), never by API contract name
    /// (<c>github-copilot-messages</c>) - the contract lives on <see cref="LlmModel.Api"/>.
    /// </summary>
    private static ModelRegistry MakeRuntimeRegistry()
    {
        var registry = new ModelRegistry();
        registry.Register("github-copilot", new LlmModel(
            Id: "claude-sonnet-4",
            Name: "Claude Sonnet 4",
            Api: "github-copilot-messages",
            Provider: "github-copilot",
            BaseUrl: "https://example.invalid",
            Reasoning: true,
            Input: ["text"],
            Cost: new ModelCost(0m, 0m, 0m, 0m),
            ContextWindow: 200_000,
            MaxTokens: 64_000));
        registry.Register("github-copilot", new LlmModel(
            Id: "gpt-5",
            Name: "GPT-5",
            Api: "github-copilot-responses",
            Provider: "github-copilot",
            BaseUrl: "https://example.invalid",
            Reasoning: false,
            Input: ["text"],
            Cost: new ModelCost(0m, 0m, 0m, 0m),
            ContextWindow: 128_000,
            MaxTokens: 16_000));
        registry.Register("anthropic", new LlmModel(
            Id: "claude-opus-4",
            Name: "Claude Opus 4",
            Api: "anthropic-messages",
            Provider: "anthropic",
            BaseUrl: "https://example.invalid",
            Reasoning: true,
            Input: ["text"],
            Cost: new ModelCost(0m, 0m, 0m, 0m),
            ContextWindow: 200_000,
            MaxTokens: 32_000));
        return registry;
    }

    private static (Mock<IAgentRegistry> registry, Mock<IAgentConfigurationWriter> writer, BotNexusHome home, Mock<IAgentChangeNotifier> notifier)
        MakeDeps(string? existingId = null, AgentDescriptor? existingDescriptor = null)
    {
        var registry = new Mock<IAgentRegistry>();
        var writer = new Mock<IAgentConfigurationWriter>();
        var notifier = new Mock<IAgentChangeNotifier>();
        var home = new BotNexusHome(new MockFileSystem(), HomePath);

        if (existingId is not null)
        {
            registry.Setup(r => r.Contains(AgentId.From(existingId))).Returns(true);
            registry.Setup(r => r.Get(AgentId.From(existingId))).Returns(existingDescriptor);
            registry.Setup(r => r.Update(It.IsAny<AgentId>(), It.IsAny<AgentDescriptor>())).Returns(true);
        }

        writer.Setup(w => w.SaveAsync(It.IsAny<AgentDescriptor>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        notifier.Setup(n => n.NotifyAgentsChangedAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        return (registry, writer, home, notifier);
    }

    private static IReadOnlyDictionary<string, object?> Args(params (string key, object? value)[] pairs) =>
        pairs.ToDictionary(p => p.key, p => p.value);

    private static AgentDescriptor Existing(string id) => new()
    {
        AgentId = AgentId.From(id),
        DisplayName = "Existing",
        ModelId = "claude-sonnet-4",
        ApiProvider = "github-copilot"
    };

    private static string? ErrorOf(BotNexus.Agent.Core.Types.AgentToolResult result)
    {
        using var doc = JsonDocument.Parse(result.Content[0].Value!);
        return doc.RootElement.TryGetProperty("error", out var error) ? error.GetString() : null;
    }

    // --- Clause 1: the provider instance name the runtime requires is accepted ---

    [Fact]
    public async Task CreateAgent_WithModelRegistryProviderInstance_Succeeds()
    {
        var (registry, writer, home, notifier) = MakeDeps();
        var tool = new CreateAgentTool(registry.Object, writer.Object, [notifier.Object], home, null, MakeRuntimeRegistry());

        var result = await tool.ExecuteAsync("t1", Args(
            ("id", "copilot-agent"),
            ("displayName", "Copilot Agent"),
            ("modelId", "claude-sonnet-4"),
            ("apiProvider", "github-copilot")));

        ErrorOf(result).ShouldBeNull();
        // The persisted descriptor must carry exactly the pair the runtime will resolve at spawn.
        writer.Verify(w => w.SaveAsync(
            It.Is<AgentDescriptor>(d => d.ApiProvider == "github-copilot" && d.ModelId == "claude-sonnet-4"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CreateAgent_WithApiContractNameThatRuntimeCannotResolve_IsRejected()
    {
        // 'github-copilot-messages' is an API contract identifier, present on LlmModel.Api but
        // never a model-registry key. Accepting it is exactly what produces the unspawnable agent.
        var (registry, writer, home, notifier) = MakeDeps();
        var tool = new CreateAgentTool(registry.Object, writer.Object, [notifier.Object], home, null, MakeRuntimeRegistry());

        var result = await tool.ExecuteAsync("t1", Args(
            ("id", "contract-name-agent"),
            ("displayName", "Broken"),
            ("modelId", "claude-sonnet-4"),
            ("apiProvider", "github-copilot-messages")));

        ErrorOf(result).ShouldNotBeNull();
        writer.Verify(w => w.SaveAsync(It.IsAny<AgentDescriptor>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // --- Clause 2: unresolvable provider rejected BEFORE any config write ---

    [Fact]
    public async Task CreateAgent_WithUnresolvableProvider_RejectsBeforeConfigWrite()
    {
        var (registry, writer, home, notifier) = MakeDeps();
        var tool = new CreateAgentTool(registry.Object, writer.Object, [notifier.Object], home, null, MakeRuntimeRegistry());

        var result = await tool.ExecuteAsync("t1", Args(
            ("id", "no-such-provider"),
            ("displayName", "Nope"),
            ("modelId", "claude-sonnet-4"),
            ("apiProvider", "totally-not-a-provider")));

        var error = ErrorOf(result);
        error.ShouldNotBeNull();
        error.ShouldContain("totally-not-a-provider");
        // The whole point of the issue: nothing may reach config.json.
        writer.Verify(w => w.SaveAsync(It.IsAny<AgentDescriptor>(), It.IsAny<CancellationToken>()), Times.Never);
        registry.Verify(r => r.Register(It.IsAny<AgentDescriptor>()), Times.Never);
        notifier.Verify(n => n.NotifyAgentsChangedAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // --- Clause 3: resolvable provider + unregistered model rejected before persistence ---

    [Fact]
    public async Task CreateAgent_WithUnregisteredModelForKnownProvider_RejectsBeforeConfigWriteAndListsModels()
    {
        var (registry, writer, home, notifier) = MakeDeps();
        var tool = new CreateAgentTool(registry.Object, writer.Object, [notifier.Object], home, null, MakeRuntimeRegistry());

        var result = await tool.ExecuteAsync("t1", Args(
            ("id", "ghost-model"),
            ("displayName", "Ghost"),
            ("modelId", "claude-sonnet-9999"),
            ("apiProvider", "github-copilot")));

        var error = ErrorOf(result);
        error.ShouldNotBeNull();
        error.ShouldContain("github-copilot");        // names the provider
        error.ShouldContain("claude-sonnet-9999");    // names the model
        error.ShouldContain("claude-sonnet-4");       // lists what IS available for that provider
        error.ShouldContain("gpt-5");
        writer.Verify(w => w.SaveAsync(It.IsAny<AgentDescriptor>(), It.IsAny<CancellationToken>()), Times.Never);
        registry.Verify(r => r.Register(It.IsAny<AgentDescriptor>()), Times.Never);
    }

    // --- Clause 4: update_agent enforces the identical rule via the same preflight ---

    [Fact]
    public async Task UpdateAgent_WithUnresolvableProvider_RejectsBeforeConfigWrite()
    {
        var (registry, writer, _, notifier) = MakeDeps("upd-provider", Existing("upd-provider"));
        var tool = new UpdateAgentTool(registry.Object, writer.Object, [notifier.Object], MakeRuntimeRegistry());

        var result = await tool.ExecuteAsync("t1", Args(
            ("id", "upd-provider"),
            ("apiProvider", "github-copilot-messages")));

        ErrorOf(result).ShouldNotBeNull();
        writer.Verify(w => w.SaveAsync(It.IsAny<AgentDescriptor>(), It.IsAny<CancellationToken>()), Times.Never);
        registry.Verify(r => r.Update(It.IsAny<AgentId>(), It.IsAny<AgentDescriptor>()), Times.Never);
    }

    [Fact]
    public async Task UpdateAgent_WithUnregisteredModelForKnownProvider_RejectsBeforeConfigWrite()
    {
        var (registry, writer, _, notifier) = MakeDeps("upd-model", Existing("upd-model"));
        var tool = new UpdateAgentTool(registry.Object, writer.Object, [notifier.Object], MakeRuntimeRegistry());

        var result = await tool.ExecuteAsync("t1", Args(
            ("id", "upd-model"),
            ("modelId", "claude-sonnet-9999")));

        var error = ErrorOf(result);
        error.ShouldNotBeNull();
        error.ShouldContain("claude-sonnet-9999");
        writer.Verify(w => w.SaveAsync(It.IsAny<AgentDescriptor>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task UpdateAgent_SwitchingProviderAndModelTogether_Succeeds()
    {
        var (registry, writer, _, notifier) = MakeDeps("upd-both", Existing("upd-both"));
        var tool = new UpdateAgentTool(registry.Object, writer.Object, [notifier.Object], MakeRuntimeRegistry());

        var result = await tool.ExecuteAsync("t1", Args(
            ("id", "upd-both"),
            ("apiProvider", "anthropic"),
            ("modelId", "claude-opus-4")));

        ErrorOf(result).ShouldBeNull();
        writer.Verify(w => w.SaveAsync(
            It.Is<AgentDescriptor>(d => d.ApiProvider == "anthropic" && d.ModelId == "claude-opus-4"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// Clause 4 as a drift guard: both tools must produce byte-identical rejection text for the
    /// identical (provider, model) pair. Two independently maintained copies of the check would
    /// diverge here the first time either message is reworded.
    /// </summary>
    [Fact]
    public async Task CreateAndUpdate_ProduceIdenticalRejectionText_ForTheSameBadProvider()
    {
        var (createRegistry, createWriter, home, createNotifier) = MakeDeps();
        var createTool = new CreateAgentTool(createRegistry.Object, createWriter.Object, [createNotifier.Object], home, null, MakeRuntimeRegistry());
        var createResult = await createTool.ExecuteAsync("t1", Args(
            ("id", "drift-create"),
            ("displayName", "Drift"),
            ("modelId", "claude-sonnet-4"),
            ("apiProvider", "bogus-provider")));

        var (updRegistry, updWriter, _, updNotifier) = MakeDeps("drift-update", Existing("drift-update"));
        var updateTool = new UpdateAgentTool(updRegistry.Object, updWriter.Object, [updNotifier.Object], MakeRuntimeRegistry());
        var updateResult = await updateTool.ExecuteAsync("t1", Args(
            ("id", "drift-update"),
            ("apiProvider", "bogus-provider")));

        ErrorOf(createResult).ShouldNotBeNull();
        ErrorOf(updateResult).ShouldBe(ErrorOf(createResult));
    }

    // --- Clause 5: the error message's own "available" list is self-consistent ---

    [Fact]
    public async Task CreateAgent_ProviderDrawnFromTheErrorMessagesOwnAvailableList_IsAccepted()
    {
        var (registry, writer, home, notifier) = MakeDeps();
        var tool = new CreateAgentTool(registry.Object, writer.Object, [notifier.Object], home, null, MakeRuntimeRegistry());

        var rejection = await tool.ExecuteAsync("t1", Args(
            ("id", "self-consistency-probe"),
            ("displayName", "Probe"),
            ("modelId", "claude-sonnet-4"),
            ("apiProvider", "definitely-unknown")));

        var error = ErrorOf(rejection);
        error.ShouldNotBeNull();

        // Parse the tool's OWN advertised provider list out of its own error message.
        var marker = error!.IndexOf("Available providers:", StringComparison.Ordinal);
        marker.ShouldBeGreaterThanOrEqualTo(0, $"error message must advertise available providers; was: {error}");
        var advertised = error[(marker + "Available providers:".Length)..]
            .TrimEnd('.', ' ')
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(v => !v.StartsWith("...", StringComparison.Ordinal))
            .ToList();
        advertised.ShouldNotBeEmpty();
        advertised.ShouldContain("github-copilot");

        // Every advertised provider must actually be usable with a model registered under it.
        foreach (var provider in advertised)
        {
            var (r2, w2, home2, n2) = MakeDeps();
            var modelRegistry = MakeRuntimeRegistry();
            var model = modelRegistry.GetModels(provider).First().Id;
            var tool2 = new CreateAgentTool(r2.Object, w2.Object, [n2.Object], home2, null, modelRegistry);

            var accepted = await tool2.ExecuteAsync("t2", Args(
                ("id", "probe-" + provider),
                ("displayName", "Probe"),
                ("modelId", model),
                ("apiProvider", provider)));

            ErrorOf(accepted).ShouldBeNull($"provider '{provider}' was advertised as available but rejected");
        }
    }

    // --- Registry-unavailable must stay permissive (minimal hosts / early startup) ---

    [Fact]
    public async Task CreateAgent_WithNoModelRegistry_SkipsPreflight()
    {
        var (registry, writer, home, notifier) = MakeDeps();
        var tool = new CreateAgentTool(registry.Object, writer.Object, [notifier.Object], home, null, null);

        var result = await tool.ExecuteAsync("t1", Args(
            ("id", "no-registry-host"),
            ("displayName", "No Registry"),
            ("modelId", "anything"),
            ("apiProvider", "anything-goes")));

        ErrorOf(result).ShouldBeNull();
    }
}
