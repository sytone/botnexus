using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Configuration;

namespace BotNexus.Integration.ConfigDiskE2E.Tests;

/// <summary>
/// The agent-management mutation path (<c>update_agent</c> tool / <c>/api/agents</c> REST) driven
/// end to end against a physical config file: <see cref="PlatformConfigAgentWriter"/> -&gt;
/// <c>config.json</c> -&gt; JSON provider -&gt; <see cref="PlatformConfig"/> consumer.
/// </summary>
/// <remarks>
/// This is the entry point most likely to cause collateral damage in practice, because it writes
/// a typed descriptor into a shared document that also holds providers, channels and unmodelled
/// vendor JSON. The delta assertions below are the point of the test: an agent upsert may change
/// agent keys and nothing else.
/// </remarks>
public sealed class AgentConfigWriterDiskTests
{
    private static AgentDescriptor Descriptor(string id, string displayName) => new()
    {
        AgentId = AgentId.From(id),
        DisplayName = displayName,
        ModelId = "gpt-4.1",
        ApiProvider = "github-copilot",
    };

    /// <summary>
    /// Upserting an existing agent must persist the edited fields to disk and confine the delta to
    /// that agent's subtree, leaving providers, channels, defaults and vendor JSON untouched.
    /// </summary>
    [Fact]
    public async Task SaveAgent_OnPhysicalFile_ConfinesDeltaToThatAgent()
    {
        using var home = new ConfigHomeFixture(MaximalConfig.Json);
        var before = home.ReadFromDisk();
        var writer = new PlatformConfigAgentWriter(home.Writer, new BotNexusHome(home.FileSystem, home.RootPath));

        await writer.SaveAsync(Descriptor("builder", "Builder Renamed"));

        var after = home.ReadFromDisk();
        after["agents"]!["builder"]!["displayName"]!.GetValue<string>().ShouldBe("Builder Renamed");

        JsonDelta.Compute(before, after)
            .ShouldAllBe(path => path.StartsWith("agents.builder.", StringComparison.Ordinal));

        after["providers"]!["github-copilot"]!["apiKey"]!.GetValue<string>().ShouldBe("sk-copilot-REAL-secret");
        after["channels"]!["telegram"]!["bots"]!.AsObject().Count.ShouldBe(2);
        after["customVendorBlock"]!["nested"]!["deep"]!["value"]!.GetValue<string>().ShouldBe("preserve-me");
        after["agents"]!["defaults"]!["toolIds"]!.AsArray().Count.ShouldBe(2);
    }

    /// <summary>
    /// Creating a brand-new agent must add exactly one agent key on disk and physically scaffold
    /// its workspace directory under the temporary home - the side effect the writer documents and
    /// that a mock filesystem could only pretend to perform.
    /// </summary>
    [Fact]
    public async Task SaveNewAgent_AddsAgentAndScaffoldsWorkspaceOnDisk()
    {
        using var home = new ConfigHomeFixture(MaximalConfig.Json);
        var botNexusHome = new BotNexusHome(home.FileSystem, home.RootPath);
        var writer = new PlatformConfigAgentWriter(home.Writer, botNexusHome);

        await writer.SaveAsync(Descriptor("reviewer", "Reviewer"));

        var after = home.ReadFromDisk();
        after["agents"]!.AsObject().KeyNames()
            .ShouldBe(["assistant", "builder", "defaults", "reviewer"]);
        after["agents"]!["reviewer"]!["provider"]!.GetValue<string>().ShouldBe("github-copilot");

        Directory.Exists(Path.Combine(botNexusHome.AgentsPath, "reviewer", "workspace")).ShouldBeTrue();
    }

    /// <summary>
    /// Deleting an agent must remove only that agent from the physical document. In particular the
    /// reserved <c>defaults</c> pseudo-agent and the remaining agents must be untouched.
    /// </summary>
    [Fact]
    public async Task DeleteAgent_RemovesOnlyThatAgentFromDisk()
    {
        using var home = new ConfigHomeFixture(MaximalConfig.Json);
        var writer = new PlatformConfigAgentWriter(home.Writer, new BotNexusHome(home.FileSystem, home.RootPath));

        await writer.DeleteAsync("builder");

        var after = home.ReadFromDisk();
        after["agents"]!.AsObject().KeyNames()
            .ShouldBe(["assistant", "defaults"]);
        after["agents"]!["assistant"]!["extensions"]!["botnexus-skills"]!["allow"]!
            .AsArray().Count.ShouldBe(2);
    }

    /// <summary>
    /// An agent written through the production writer must be visible to the runtime consumer
    /// after reload - the full tool -&gt; disk -&gt; provider -&gt; options chain for the agent path.
    /// </summary>
    [Fact]
    public async Task SavedAgent_IsVisibleToTheRuntimeConsumerAfterReload()
    {
        using var home = new ConfigHomeFixture(MaximalConfig.Json);
        using var consumer = home.BuildRuntimeConsumer();
        consumer.Monitor.CurrentValue.Agents!.ContainsKey("reviewer").ShouldBeFalse();

        var writer = new PlatformConfigAgentWriter(home.Writer, new BotNexusHome(home.FileSystem, home.RootPath));
        await writer.SaveAsync(Descriptor("reviewer", "Reviewer"));

        var reloaded = consumer.ReloadNow();
        var agents = reloaded.Agents.ShouldNotBeNull();
        agents.ShouldContainKey("reviewer");
        agents["reviewer"].DisplayName.ShouldBe("Reviewer");
        agents.ContainsKey("defaults").ShouldBeFalse();
    }
}
