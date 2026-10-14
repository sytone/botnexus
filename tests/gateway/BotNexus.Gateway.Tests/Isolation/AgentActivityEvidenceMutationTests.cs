using System.IO.Abstractions.TestingHelpers;
using System.Text;
using BotNexus.Agent.Core.Types;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Agents;
using BotNexus.Gateway.Isolation;
using BotNexus.Tools;

namespace BotNexus.Gateway.Tests.Isolation;

public sealed class AgentActivityEvidenceMutationTests
{
    private const int TurnWindow = 3;

    [Fact]
    public async Task Project_ActualWriteAppendDifferentEqualByteRecords_ClassifiesProgressUnknown()
    {
        var observations = await AppendDifferentRecordsAsync();

        observations.ShouldAllBe(value => value.Kind == AgentActivityKind.ToolProgressUnknown);
    }

    [Fact]
    public async Task Observe_ActualWriteAppendIdenticalConfirmations_NeverStopsProductiveTurns()
    {
        var observations = await AppendDifferentRecordsAsync();
        var detector = new SubAgentNoProgressDetector(TurnWindow, TurnWindow);

        foreach (var observation in observations)
        {
            detector.Observe(observation).ShouldBeNull();
            detector.Observe(new(AgentActivityKind.TurnCompleted, 0, 0)).ShouldBeNull();
        }
    }

    [Theory]
    [InlineData("exec")]
    [InlineData("write")]
    [InlineData("edit")]
    [InlineData("extension_mutator")]
    [InlineData("read_extension")]
    public void Project_UnverifiedSuccessfulTool_ClassifiesProgressUnknown(string toolName)
    {
        Project("call", toolName, Text("accepted"), error: false).Kind
            .ShouldBe(AgentActivityKind.ToolProgressUnknown);
    }

    [Theory]
    [InlineData("exec")]
    [InlineData("extension_mutator")]
    public void Observe_UnverifiedSuccessWithIdenticalConfirmation_NeverStops(string toolName)
    {
        var detector = new SubAgentNoProgressDetector(TurnWindow, TurnWindow);
        for (var turn = 0; turn < TurnWindow * 4; turn++)
        {
            detector.Observe(Project($"call-{turn}", toolName, Text("accepted"), error: false)).ShouldBeNull();
            detector.Observe(new(AgentActivityKind.TurnCompleted, 0, 0)).ShouldBeNull();
        }
    }

    [Fact]
    public async Task Observe_ActualReadRepeatedUnchangedEvidence_StillStops()
    {
        var workspace = Path.Combine(Path.GetTempPath(), "activity-read", Guid.NewGuid().ToString("N"));
        var fileSystem = new MockFileSystem();
        fileSystem.Directory.CreateDirectory(workspace);
        await fileSystem.File.WriteAllTextAsync(Path.Combine(workspace, "data.txt"), "unchanged body\n");
        var tool = new ReadTool(workspace, fileSystem);
        var detector = new SubAgentNoProgressDetector(TurnWindow, TurnWindow);
        var decisions = new List<SubAgentNoProgressDecision>();
        // ReadTool can replace the first full body with a compact unchanged confirmation.
        // Allow that new evidence once, then require the repeated real result to stop.
        for (var turn = 0; turn < TurnWindow + 2; turn++)
        {
            var callId = $"read-{turn}";
            var result = await tool.ExecuteAsync(callId, new Dictionary<string, object?> { ["path"] = "data.txt" });
            var observation = Project(callId, tool.Name, result, error: false);
            observation.Kind.ShouldBe(AgentActivityKind.ToolSucceeded);
            detector.Observe(observation).ShouldBeNull();
            var decision = detector.Observe(new(AgentActivityKind.TurnCompleted, 0, 0));
            if (decision is not null) decisions.Add(decision);
        }

        decisions.ShouldNotBeEmpty();
        decisions.ShouldAllBe(value => value.Reason == "unchanged-successful-evidence" && value.MatchedTurns == TurnWindow);
        (await fileSystem.File.ReadAllTextAsync(Path.Combine(workspace, "data.txt"))).ShouldBe("unchanged body\n");
    }

    [Theory]
    [InlineData("exec")]
    [InlineData("write")]
    [InlineData("extension_mutator")]
    public void Observe_UnverifiedToolRepeatedFailure_StillHashesAndStops(string toolName)
    {
        var detector = new SubAgentNoProgressDetector(TurnWindow, TurnWindow);
        var first = Project("first", toolName, Text("same failure"), error: true);
        first.Kind.ShouldBe(AgentActivityKind.ToolFailed);
        Project("changed", toolName, Text("different failure"), error: true).ShouldNotBe(first);
        for (var turn = 1; turn <= TurnWindow; turn++)
        {
            var observation = Project($"call-{turn}", toolName, Text("same failure"), error: true);
            observation.ShouldBe(first);
            detector.Observe(observation).ShouldBeNull();
            var decision = detector.Observe(new(AgentActivityKind.TurnCompleted, 0, 0));
            if (turn < TurnWindow) decision.ShouldBeNull();
            else decision.ShouldNotBeNull().Reason.ShouldBe("repeated-failing-turn-sequence");
        }
    }

    private static async Task<IReadOnlyList<AgentActivityObservation>> AppendDifferentRecordsAsync()
    {
        const string firstRecord = "record-a\n";
        const string secondRecord = "record-b\n";
        firstRecord.ShouldNotBe(secondRecord);
        Encoding.UTF8.GetByteCount(firstRecord).ShouldBe(Encoding.UTF8.GetByteCount(secondRecord));
        var workspace = Path.Combine(Path.GetTempPath(), "activity-write", Guid.NewGuid().ToString("N"));
        var fileSystem = new MockFileSystem();
        fileSystem.Directory.CreateDirectory(workspace);
        var tool = new WriteTool(workspace, fileSystem);
        var observations = new List<AgentActivityObservation>();
        var persisted = new StringBuilder();
        string? confirmation = null;
        for (var turn = 0; turn < TurnWindow * 4; turn++)
        {
            var record = turn % 2 == 0 ? firstRecord : secondRecord;
            var callId = $"append-{turn}";
            var result = await tool.ExecuteAsync(callId, new Dictionary<string, object?>
            {
                ["path"] = "records.log",
                ["content"] = record,
                ["append"] = true
            });
            var message = result.Content.ShouldHaveSingleItem().Value;
            message.ShouldContain("Appended");
            confirmation ??= message;
            message.ShouldBe(confirmation);
            persisted.Append(record);
            (await fileSystem.File.ReadAllTextAsync(Path.Combine(workspace, "records.log"))).ShouldBe(persisted.ToString());
            observations.Add(Project(callId, tool.Name, result, error: false));
        }
        observations.Count.ShouldBeGreaterThan(TurnWindow);
        return observations;
    }

    private static AgentToolResult Text(string text) => new([new(AgentToolContentType.Text, text)]);

    private static AgentActivityObservation Project(string callId, string toolName, AgentToolResult result, bool error)
        => AgentActivityEvidence.Project(new ToolExecutionEndEvent(callId, toolName, result, error, DateTimeOffset.UnixEpoch));
}
