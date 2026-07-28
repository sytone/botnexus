using System.Text.Json.Nodes;
using BotNexus.Gateway.Configuration;
using Microsoft.Extensions.Options;

namespace BotNexus.Integration.ConfigDiskE2E.Tests;

/// <summary>
/// The half of the pipeline a mock filesystem structurally cannot reach: a production write to a
/// physical <c>config.json</c> propagating through the real JSON configuration provider into the
/// <see cref="IOptionsMonitor{TOptions}"/> that gateway services actually read at runtime.
/// </summary>
public sealed class ConfigReloadConsumerDiskTests
{
    /// <summary>
    /// A write through the production writer must reach the runtime consumer's bound object graph,
    /// not merely the file. This is the end of the chain named in #2066:
    /// writer -&gt; config.json -&gt; JSON provider -&gt; IOptionsMonitor -&gt; consumer.
    /// </summary>
    [Fact]
    public async Task WriteThroughProductionWriter_ReachesTheRuntimeOptionsConsumer()
    {
        using var home = new ConfigHomeFixture(MaximalConfig.Json);
        using var consumer = home.BuildRuntimeConsumer();

        consumer.Monitor.CurrentValue.Gateway!.LogLevel.ShouldBe("Information");

        await home.Writer.UpdateSectionAsync(
            "gateway",
            new JsonObject { ["logLevel"] = "Warning" });

        var reloaded = consumer.ReloadNow();
        reloaded.Gateway!.LogLevel.ShouldBe("Warning");
        consumer.ReadKey("gateway:logLevel").ShouldBe("Warning");
    }

    /// <summary>
    /// The provider is registered with <c>reloadOnChange: true</c>, so an on-disk replacement must
    /// raise a change notification without anyone asking it to. Timing is OS-dependent, hence the
    /// generous bounded wait; a failure here means hot reload is genuinely broken, which is the
    /// class of defect that only a real file watcher over a real file can detect.
    /// </summary>
    [Fact]
    public async Task PhysicalFileReplacement_RaisesReloadAcknowledgement()
    {
        using var home = new ConfigHomeFixture(MaximalConfig.Json);
        using var consumer = home.BuildRuntimeConsumer();

        await home.Writer.UpdateSectionAsync(
            "gateway",
            new JsonObject { ["listenUrl"] = "http://localhost:7007" });

        var observed = await consumer.WaitForReloadAsync();
        observed.ShouldBeTrue(
            "the JSON configuration provider watches config.json and must acknowledge an atomic replacement");
        consumer.ReloadCount.ShouldBeGreaterThan(0);
    }

    /// <summary>
    /// A no-op write must not trigger a reload. Combined with the durability test that asserts the
    /// file is untouched, this pins the #2114 reload-storm fix at the consumer end where the cost
    /// was actually paid.
    /// </summary>
    [Fact]
    public async Task NoOpWrite_DoesNotRaiseReloadAcknowledgement()
    {
        using var home = new ConfigHomeFixture(MaximalConfig.Json);
        using var consumer = home.BuildRuntimeConsumer();

        await home.Writer.MutateAsync(_ => { }, "test-noop-reload");

        var observed = await consumer.WaitForReloadAsync(TimeSpan.FromSeconds(2));
        observed.ShouldBeFalse("a write that changes nothing must not touch the file or wake consumers");
        consumer.ReloadCount.ShouldBe(0);
    }

    /// <summary>
    /// <c>agents.defaults</c> must be extracted into <see cref="PlatformConfig.AgentDefaults"/> and
    /// removed from the named-agent dictionary by the production post-configure step when the
    /// consumer binds from a real file. Getting this wrong materialises a phantom agent called
    /// "defaults" in the registry.
    /// </summary>
    [Fact]
    public void RuntimeConsumer_ExtractsAgentDefaultsFromThePhysicalFile()
    {
        using var home = new ConfigHomeFixture(MaximalConfig.Json);
        using var consumer = home.BuildRuntimeConsumer();

        var config = consumer.Monitor.CurrentValue;

        config.AgentDefaults.ShouldNotBeNull();
        config.AgentDefaults!.ToolIds.ShouldNotBeNull();
        config.Agents.ShouldNotBeNull();
        config.Agents!.ContainsKey("defaults").ShouldBeFalse();
        config.Agents.Keys.Order(StringComparer.Ordinal).ShouldBe(["assistant", "builder"]);
    }

    /// <summary>
    /// Unmodelled per-agent extension JSON must survive the physical-file bind and be visible to
    /// the runtime consumer, since extensions read their own config out of that bag.
    /// </summary>
    [Fact]
    public void RuntimeConsumer_SeesExtensionJsonFromThePhysicalFile()
    {
        using var home = new ConfigHomeFixture(MaximalConfig.Json);
        using var consumer = home.BuildRuntimeConsumer();

        var assistant = consumer.Monitor.CurrentValue.Agents!["assistant"];
        assistant.Extensions.ShouldNotBeNull();
        assistant.Extensions!.ShouldContainKey("botnexus-skills");
    }

    /// <summary>
    /// Secrets are stored in clear text on disk by design (the file is the source of truth), so
    /// the runtime consumer must receive the real values - a redaction leak into the write path
    /// would surface here as a placeholder reaching live services.
    /// </summary>
    [Fact]
    public async Task RuntimeConsumer_ReceivesRealSecretsAfterARedactedRoundTrip()
    {
        using var home = new ConfigHomeFixture(MaximalConfig.Json);
        using var consumer = home.BuildRuntimeConsumer();

        var wrapper = new JsonObject { ["providers"] = home.ReadFromDisk()["providers"]!.DeepClone() };
        ConfigSecretMerge.Redact(wrapper);
        var redacted = wrapper["providers"]!.AsObject();
        redacted["github-copilot"]!["defaultModel"] = "gpt-4.1";

        await home.Writer.UpdateSectionAsync("providers", redacted.DeepClone());

        var reloaded = consumer.ReloadNow();
        reloaded.Providers!["github-copilot"].ApiKey.ShouldBe("sk-copilot-REAL-secret");
        reloaded.Providers["github-copilot"].DefaultModel.ShouldBe("gpt-4.1");
    }
}
