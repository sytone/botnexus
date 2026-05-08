using System.Diagnostics;
using BotNexus.Agent.Core.Diagnostics;
using BotNexus.Gateway.Diagnostics;
using BotNexus.Agent.Providers.Core.Diagnostics;

namespace BotNexus.Gateway.Tests;

public sealed class DiagnosticsTests
{
    [Fact]
    public void GatewayDiagnostics_Source_HasCorrectName()
    {
        GatewayDiagnostics.Source.Name.ShouldBe("BotNexus.Gateway");
    }

    [Fact]
    public void GatewayDiagnostics_StartActivity_CreatesActivityWithTags()
    {
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == GatewayDiagnostics.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData
        };
        ActivitySource.AddActivityListener(listener);

        using var activity = GatewayDiagnostics.Source.StartActivity("test.operation");
        activity.ShouldNotBeNull();

        activity!.SetTag("botnexus.session.id", "test-session");
        activity.GetTagItem("botnexus.session.id").ShouldBe("test-session");
    }

    [Fact]
    public void ProviderDiagnostics_Source_HasCorrectName()
    {
        ProviderDiagnostics.Source.Name.ShouldBe("BotNexus.Providers");
    }

    [Fact]
    public void AgentDiagnostics_Source_HasCorrectName()
    {
        AgentDiagnostics.Source.Name.ShouldBe("BotNexus.Agents");
    }

    [Fact]
    public void ActivitySources_ProduceChildSpans_WhenParentExists()
    {
        using var listener = new ActivityListener
        {
            ShouldListenTo = _ => true,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData
        };
        ActivitySource.AddActivityListener(listener);

        using var parent = GatewayDiagnostics.Source.StartActivity("gateway.dispatch");
        parent.ShouldNotBeNull();

        using var child = ProviderDiagnostics.Source.StartActivity("llm.stream");
        child.ShouldNotBeNull();
        child!.ParentId.ShouldNotBeNullOrEmpty();
        child.TraceId.ShouldBe(parent!.TraceId);
    }
}
