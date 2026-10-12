using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;

namespace BotNexus.Gateway.Tests;

// No stand-in query, statistics engine, or store: reflection only bridges not-yet-added product types.
internal static class RunMeasurement4796Fixture
{
    internal const string ContractName = "BotNexus.Gateway.Abstractions.Sessions.IAgentRunEvidenceStore";
    internal static readonly DateTimeOffset Epoch = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
    internal static readonly string[] EvidenceFields = ["AgentRunId", "StartedAt", "CompletedAt", "Outcome", "CompletedResultCount", "GuardObservations"];
    internal static readonly string[] PageFields = ["Runs", "SampleMeasuredRuns", "SemanticStops", "FuseStops", "UnknownRuns", "ResultCountP50", "ResultCountP95", "ResultCountP99", "LegacySampleCount", "UnknownLegacyCount", "LegacySampleTruncated", "HasMore", "NextCursor"];
    internal static Type Capability(object store)
    {
        var type = typeof(ISessionStore).Assembly.GetType(ContractName)
            .ShouldNotBeNull("#4796 requires a public product evidence/query capability, not test SQL statistics");
        type.IsPublic.ShouldBeTrue();
        type.IsInterface.ShouldBeTrue();
        type.IsInstanceOfType(store).ShouldBeTrue("SqliteSessionStore must implement the durable run capability");
        return type;
    }

    internal static Type EvidenceType(object store)
    {
        var contract = Capability(store);
        var record = contract.GetMethod("RecordAgentRunAsync").ShouldNotBeNull();
        var parameters = record.GetParameters();
        parameters.Length.ShouldBe(3);
        parameters[0].ParameterType.ShouldBe(typeof(SessionId));
        parameters[2].ParameterType.ShouldBe(typeof(CancellationToken));
        record.ReturnType.ShouldBe(typeof(Task));
        var evidence = parameters[1].ParameterType;
        evidence.FullName.ShouldBe("BotNexus.Gateway.Abstractions.Models.AgentRunEvidence");
        evidence.Assembly.ShouldBe(typeof(GuardObservation).Assembly, "pure data belongs in Domain, not Gateway or Contracts");
        evidence.GetProperties().Select(p => p.Name).Order().ShouldBe(EvidenceFields.Order());
        evidence.GetProperty("AgentRunId").ShouldNotBeNull().PropertyType.ShouldBe(typeof(AgentRunId));
        evidence.GetProperty("StartedAt").ShouldNotBeNull().PropertyType.ShouldBe(typeof(DateTimeOffset));
        evidence.GetProperty("CompletedAt").ShouldNotBeNull().PropertyType.ShouldBe(typeof(DateTimeOffset?));
        evidence.GetProperty("CompletedResultCount").ShouldNotBeNull().PropertyType.ShouldBe(typeof(int?));
        evidence.GetProperty("GuardObservations").ShouldNotBeNull().PropertyType.ShouldBe(typeof(IReadOnlyList<GuardObservation>));
        return evidence;
    }

    internal static object Evidence(object store, string id, int? count = null, string outcome = "Running",
        IReadOnlyList<GuardObservation>? guards = null, bool terminal = false, int second = 0)
    {
        var json = JsonSerializer.Serialize(new
        {
            AgentRunId = id, StartedAt = Epoch.AddSeconds(second),
            CompletedAt = terminal ? (DateTimeOffset?)Epoch.AddSeconds(second + 1) : null,
            Outcome = outcome, CompletedResultCount = count, GuardObservations = guards ?? []
        });
        return JsonSerializer.Deserialize(json, EvidenceType(store)).ShouldNotBeNull();
    }

    internal static async Task Record(object store, SessionId session, object evidence)
    {
        var method = Capability(store).GetMethod("RecordAgentRunAsync").ShouldNotBeNull();
        await Invoke(method, store, [session, evidence, CancellationToken.None]).ShouldBeAssignableTo<Task>();
    }

    internal static async Task<JsonElement> Query(object store, SessionId session, int limit = 1000, long after = 0)
    {
        var method = Capability(store).GetMethod("QueryAgentRunsAsync", [typeof(SessionId), typeof(int), typeof(long), typeof(CancellationToken)])
            .ShouldNotBeNull("#4796 query must require session ownership and a bounded sequence cursor");
        var result = await AwaitResult(Invoke(method, store, [session, limit, after, CancellationToken.None]));
        var page = JsonSerializer.SerializeToElement(result);
        Fields(page, PageFields);
        foreach (var row in page.GetProperty("Runs").EnumerateArray())
            Fields(row, EvidenceFields.Append("Sequence"));
        return page;
    }

    private static object Invoke(MethodInfo method, object store, object?[] arguments)
    {
        try { return method.Invoke(store, arguments).ShouldNotBeNull(); }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
            throw;
        }
    }

    private static async Task<object> AwaitResult(dynamic task) => (object)await task;
    internal static void Fields(JsonElement value, IEnumerable<string> fields)
        => value.EnumerateObject().Select(p => p.Name).Order().ShouldBe(fields.Order(), "measurement/export is an exact payload-free allowlist");
    internal static JsonElement[] Rows(JsonElement page) => page.GetProperty("Runs").EnumerateArray().ToArray();
    internal static string RunId(JsonElement row) => row.GetProperty("AgentRunId").GetString().ShouldNotBeNull();
    internal static long Number(JsonElement value, string field) => value.GetProperty(field).GetInt64();
    internal static void Summary(JsonElement page, int measured, int semantic, int fuse, int unknown, int? p50, int? p95, int? p99)
    {
        Number(page, "SampleMeasuredRuns").ShouldBe(measured);
        Number(page, "SemanticStops").ShouldBe(semantic);
        Number(page, "FuseStops").ShouldBe(fuse);
        Number(page, "UnknownRuns").ShouldBe(unknown);
        foreach (var (name, expected) in new[] { ("ResultCountP50", p50), ("ResultCountP95", p95), ("ResultCountP99", p99) })
        {
            var property = page.GetProperty(name);
            if (expected is null) property.ValueKind.ShouldBe(JsonValueKind.Null);
            else property.GetInt32().ShouldBe(expected.Value);
        }
    }

    internal static GuardObservation Guard(string disposition = "terminal", bool fuse = false, string kind = "unchanged-read", int total = 6)
        => new(fuse ? "absolute-tool-result-limit" : kind, fuse ? 0 : 6, total, 3, fuse ? 128 : 6, fuse, disposition, [Guid.NewGuid().ToString("N")]);
}
