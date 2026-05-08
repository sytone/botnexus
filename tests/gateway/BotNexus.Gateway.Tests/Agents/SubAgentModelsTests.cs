using System.Reflection;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Configuration;

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

        requiredProperties.ShouldBe(new[] { "ParentAgentId", "ParentSessionId", "Task" });
    }

    [Fact]
    public void SubAgentSpawnRequest_Defaults_AreApplied()
    {
        var request = new SubAgentSpawnRequest
        {
            ParentAgentId = BotNexus.Domain.Primitives.AgentId.From("parent-agent"),
            ParentSessionId = BotNexus.Domain.Primitives.SessionId.From("parent-session"),
            Task = "Analyze issue"
        };

        request.MaxTurns.ShouldBe(30);
        request.TimeoutSeconds.ShouldBe(600);
        request.Name.ShouldBeNull();
        request.ModelOverride.ShouldBeNull();
        request.ApiProviderOverride.ShouldBeNull();
        request.ToolIds.ShouldBeNull();
        request.SystemPromptOverride.ShouldBeNull();
        request.Archetype.ShouldBe(SubAgentArchetype.General);
    }

    [Fact]
    public void SubAgentSpawnRequest_RecordEquality_Works()
    {
        var baseline = new SubAgentSpawnRequest
        {
            ParentAgentId = BotNexus.Domain.Primitives.AgentId.From("parent-agent"),
            ParentSessionId = BotNexus.Domain.Primitives.SessionId.From("parent-session"),
            Task = "Analyze issue",
            Name = "researcher"
        };

        var equalCopy = baseline with { };
        var modified = baseline with { Task = "Different task" };

        equalCopy.ShouldBe(baseline);
        modified.ShouldNotBe(baseline);
    }

    [Fact]
    public void SubAgentInfo_RequiredProperties_AreMarkedRequired()
    {
        var requiredProperties = typeof(SubAgentInfo)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.GetCustomAttributes().Any(attribute => attribute.GetType().Name == "RequiredMemberAttribute"))
            .Select(property => property.Name)
            .ToArray();

        requiredProperties.ShouldBe(new[] { "SubAgentId", "ParentSessionId", "ChildSessionId", "Task" });
    }

    [Fact]
    public void SubAgentInfo_Defaults_AreApplied()
    {
        var info = new SubAgentInfo
        {
            SubAgentId = BotNexus.Domain.Primitives.AgentId.From("sub-123"),
            ParentSessionId = BotNexus.Domain.Primitives.SessionId.From("parent-session"),
            ChildSessionId = BotNexus.Domain.Primitives.SessionId.From("parent-session::sub::sub-123"),
            Task = "Analyze issue"
        };

        info.Status.ShouldBe(SubAgentStatus.Running);
        info.Archetype.ShouldBe(SubAgentArchetype.General);
        info.StartedAt.ShouldBe(default);
        info.CompletedAt.ShouldBeNull();
    }

    [Fact]
    public void SubAgentInfo_ExposesArchetypeField()
    {
        var property = typeof(SubAgentInfo).GetProperty(nameof(SubAgentInfo.Archetype));

        property.ShouldNotBeNull();
        property!.PropertyType.ShouldBe(typeof(SubAgentArchetype));
    }

    [Fact]
    public void SubAgentInfo_RecordWith_CanRepresentStatusTransition()
    {
        var running = new SubAgentInfo
        {
            SubAgentId = BotNexus.Domain.Primitives.AgentId.From("sub-123"),
            ParentSessionId = BotNexus.Domain.Primitives.SessionId.From("parent-session"),
            ChildSessionId = BotNexus.Domain.Primitives.SessionId.From("parent-session::sub::sub-123"),
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

        completed.Status.ShouldBe(SubAgentStatus.Completed);
        completed.CompletedAt.ShouldBe(completedAt);
        completed.ResultSummary.ShouldBe("Done");
        running.Status.ShouldBe(SubAgentStatus.Running);
    }

    [Fact]
    public void SubAgentStatus_ContainsExpectedValues_InExpectedOrder()
    {
        var statuses = Enum.GetValues<SubAgentStatus>();

        statuses.ShouldBe(new[] {
            SubAgentStatus.Running,
            SubAgentStatus.Completed,
            SubAgentStatus.Failed,
            SubAgentStatus.Killed,
            SubAgentStatus.TimedOut });
    }

    [Fact]
    public void SubAgentStatus_UnderlyingValues_AreStable()
    {
        ((int)SubAgentStatus.Running).ShouldBe(0);
        ((int)SubAgentStatus.Completed).ShouldBe(1);
        ((int)SubAgentStatus.Failed).ShouldBe(2);
        ((int)SubAgentStatus.Killed).ShouldBe(3);
        ((int)SubAgentStatus.TimedOut).ShouldBe(4);
    }

    [Fact]
    public void SubAgentOptions_Defaults_AreApplied()
    {
        var options = new SubAgentOptions();

        options.MaxConcurrentPerSession.ShouldBe(5);
        options.DefaultMaxTurns.ShouldBe(30);
        options.DefaultTimeoutSeconds.ShouldBe(600);
        options.MaxDepth.ShouldBe(1);
        options.DefaultModel.ShouldBeEmpty();
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

        options.MaxConcurrentPerSession.ShouldBe(2);
        options.DefaultMaxTurns.ShouldBe(15);
        options.DefaultTimeoutSeconds.ShouldBe(120);
        options.MaxDepth.ShouldBe(3);
        options.DefaultModel.ShouldBe("gpt-5-mini");
    }
}
