using System.Reflection;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Configuration;
using FluentAssertions;

namespace BotNexus.Gateway.Tests.Agents;

public sealed class SubAgentModelsTests
{
    [Fact]
    public void SubAgentSpawnRequest_RequiredProperties_AreMarkedRequired()
    {
        var requiredProperties = typeof(SubAgentSpawnRequest)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.GetCustomAttributes().Any(attribute => attribute.GetType().Name == "RequiredMemberAttribute"))
            .Select(property => property.Name)
            .ToArray();

        requiredProperties.Should().BeEquivalentTo(["ParentAgentId", "ParentSessionId", "Task"]);
    }

    [Fact]
    public void SubAgentSpawnRequest_Defaults_AreApplied()
    {
        var request = new SubAgentSpawnRequest
        {
            ParentAgentId = "parent-agent",
            ParentSessionId = "parent-session",
            Task = "Analyze issue"
        };

        request.MaxTurns.Should().Be(30);
        request.TimeoutSeconds.Should().Be(600);
        request.Name.Should().BeNull();
        request.ModelOverride.Should().BeNull();
        request.ApiProviderOverride.Should().BeNull();
        request.ToolIds.Should().BeNull();
        request.SystemPromptOverride.Should().BeNull();
        request.Archetype.Should().Be(SubAgentArchetype.General);
    }

    [Fact]
    public void SubAgentSpawnRequest_RecordEquality_Works()
    {
        var baseline = new SubAgentSpawnRequest
        {
            ParentAgentId = "parent-agent",
            ParentSessionId = "parent-session",
            Task = "Analyze issue",
            Name = "researcher"
        };

        var equalCopy = baseline with { };
        var modified = baseline with { Task = "Different task" };

        equalCopy.Should().Be(baseline);
        modified.Should().NotBe(baseline);
    }

    [Fact]
    public void SubAgentInfo_RequiredProperties_AreMarkedRequired()
    {
        var requiredProperties = typeof(SubAgentInfo)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.GetCustomAttributes().Any(attribute => attribute.GetType().Name == "RequiredMemberAttribute"))
            .Select(property => property.Name)
            .ToArray();

        requiredProperties.Should().BeEquivalentTo(["SubAgentId", "ParentSessionId", "ChildSessionId", "Task"]);
    }

    [Fact]
    public void SubAgentInfo_Defaults_AreApplied()
    {
        var info = new SubAgentInfo
        {
            SubAgentId = "sub-123",
            ParentSessionId = "parent-session",
            ChildSessionId = "parent-session::sub::sub-123",
            Task = "Analyze issue"
        };

        info.Status.Should().Be(SubAgentStatus.Running);
        info.Archetype.Should().Be(SubAgentArchetype.General);
        info.StartedAt.Should().Be(default);
        info.CompletedAt.Should().BeNull();
    }

    [Fact]
    public void SubAgentInfo_RecordWith_CanRepresentStatusTransition()
    {
        var running = new SubAgentInfo
        {
            SubAgentId = "sub-123",
            ParentSessionId = "parent-session",
            ChildSessionId = "parent-session::sub::sub-123",
            Task = "Analyze issue",
            Status = SubAgentStatus.Running,
            StartedAt = DateTimeOffset.UtcNow
        };

        var completedAt = DateTimeOffset.UtcNow;
        var completed = running with
        {
            Status = SubAgentStatus.Completed,
            CompletedAt = completedAt,
            ResultSummary = "Done"
        };

        completed.Status.Should().Be(SubAgentStatus.Completed);
        completed.CompletedAt.Should().Be(completedAt);
        completed.ResultSummary.Should().Be("Done");
        running.Status.Should().Be(SubAgentStatus.Running);
    }

    [Fact]
    public void SubAgentStatus_ContainsExpectedValues_InExpectedOrder()
    {
        var statuses = Enum.GetValues<SubAgentStatus>();

        statuses.Should().Equal(
            SubAgentStatus.Running,
            SubAgentStatus.Completed,
            SubAgentStatus.Failed,
            SubAgentStatus.Killed,
            SubAgentStatus.TimedOut);
    }

    [Fact]
    public void SubAgentStatus_UnderlyingValues_AreStable()
    {
        ((int)SubAgentStatus.Running).Should().Be(0);
        ((int)SubAgentStatus.Completed).Should().Be(1);
        ((int)SubAgentStatus.Failed).Should().Be(2);
        ((int)SubAgentStatus.Killed).Should().Be(3);
        ((int)SubAgentStatus.TimedOut).Should().Be(4);
    }

    [Fact]
    public void SubAgentOptions_Defaults_AreApplied()
    {
        var options = new SubAgentOptions();

        options.MaxConcurrentPerSession.Should().Be(5);
        options.DefaultMaxTurns.Should().Be(30);
        options.DefaultTimeoutSeconds.Should().Be(600);
        options.MaxDepth.Should().Be(1);
        options.DefaultModel.Should().BeEmpty();
    }

    [Fact]
    public void SubAgentOptions_Values_CanBeModified()
    {
        var options = new SubAgentOptions
        {
            MaxConcurrentPerSession = 2,
            DefaultMaxTurns = 15,
            DefaultTimeoutSeconds = 120,
            MaxDepth = 3,
            DefaultModel = "gpt-5-mini"
        };

        options.MaxConcurrentPerSession.Should().Be(2);
        options.DefaultMaxTurns.Should().Be(15);
        options.DefaultTimeoutSeconds.Should().Be(120);
        options.MaxDepth.Should().Be(3);
        options.DefaultModel.Should().Be("gpt-5-mini");
    }
}
