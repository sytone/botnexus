using System.Diagnostics;
using BotNexus.Agent.Core.Diagnostics;
using BotNexus.Gateway.Diagnostics;
using BotNexus.Agent.Providers.Core.Diagnostics;
using FluentAssertions;

namespace BotNexus.Gateway.Tests;

public sealed class DiagnosticsTests
{
    [Fact]
    public void GatewayDiagnostics_Source_HasCorrectName()
    {
        GatewayDiagnostics.Source.Name.Should().Be("BotNexus.Gateway");
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
        activity.Should().NotBeNull();

        activity!.SetTag("botnexus.session.id", "test-session");
        activity.GetTagItem("botnexus.session.id").Should().Be("test-session");
    }

    [Fact]
    public void ProviderDiagnostics_Source_HasCorrectName()
    {
        ProviderDiagnostics.Source.Name.Should().Be("BotNexus.Providers");
    }

    [Fact]
    public void AgentDiagnostics_Source_HasCorrectName()
    {
        AgentDiagnostics.Source.Name.Should().Be("BotNexus.Agents");
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
        parent.Should().NotBeNull();

        using var child = ProviderDiagnostics.Source.StartActivity("llm.stream");
        child.Should().NotBeNull();
        child!.ParentId.Should().NotBeNullOrEmpty();
        child.TraceId.Should().Be(parent!.TraceId);
    }
}
