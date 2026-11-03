using System.Text.Json;
using BotNexus.Cli.Commands;
using Xunit;

namespace BotNexus.Cli.Tests.Commands;

public sealed class UpdaterContractTests
{
    internal static string Contract(string minimum = "0.50.2", int schema = 1, string required = "planned-shutdown-v1", string provided = "planned-shutdown-v1")
        => JsonSerializer.Serialize(new { schemaVersion = schema, minimumUpdaterVersion = minimum, requiredUpdaterCapabilities = new[] { required }, providedTargetCapabilities = new[] { provided } });

    [Theory]
    [InlineData("0.50.2", "0.50.2", true)]
    [InlineData("0.51.0", "0.50.2", true)]
    [InlineData("0.50.2", "0.51.0", false)]
    public void Evaluate_PackageBoundary_IsIndependentOfTargetPackage(string running, string minimum, bool compatible)
    {
        var cli = new UpdaterDescriptor(running, "running-sha", [1], ["planned-shutdown-v1"], ["planned-shutdown-v1"]);
        (UpdaterCompatibility.Evaluate(cli, Contract(minimum)) is null).ShouldBe(compatible);
    }

    [Theory]
    [InlineData(null, "missing")]
    [InlineData("not-json", "Malformed")]
    [InlineData("{}", "Malformed")]
    [InlineData("null", "Malformed")]
    [InlineData("{\"schemaVersion\":2}", "schema")]
    [InlineData("{\"schemaVersion\":1,\"schemaVersion\":1}", "duplicate")]
    [InlineData("{\"schemaVersion\":null}", "Malformed")]
    public void Evaluate_UnknownOrLegacyContract_FailsClosed(string? json, string reason)
        => (UpdaterCompatibility.Evaluate(UpdaterCompatibility.Running, json) ?? "unexpected compatibility").ShouldContain(reason, Case.Insensitive);

    [Theory]
    [InlineData("supportedContractSchemas", "[1]")]
    [InlineData("supportedUpdaterCapabilities", "[\"planned-shutdown-v1\"]")]
    [InlineData("requiredTargetCapabilities", "[\"planned-shutdown-v1\"]")]
    [InlineData("unknown", "true")]
    public void Evaluate_SupportOnlyOrUnknownProperties_RejectsSource(string property, string value)
    {
        var json = Contract()[..^1] + ",\"" + property + "\":" + value + "}";
        UpdaterCompatibility.Evaluate(new UpdaterDescriptor("0.50.2", "sha", [1], ["planned-shutdown-v1"], ["planned-shutdown-v1"]), json).ShouldNotBeNull();
    }

    [Theory]
    [InlineData("null")]
    [InlineData("\"1\"")]
    [InlineData("[]")]
    [InlineData("true")]
    public void Evaluate_MalformedSchema_FailsClosedWithoutThrowing(string schema)
    {
        var json = Contract().Replace("\"schemaVersion\":1", "\"schemaVersion\":" + schema, StringComparison.Ordinal);
        UpdaterCompatibility.Evaluate(UpdaterCompatibility.Running, json).ShouldNotBeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not-json")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"supportedContractSchemas\":1}")]
    [InlineData("{\"supportedContractSchemas\":[null]}")]
    [InlineData("{\"supportedContractSchemas\":[1],\"supportedContractSchemas\":[1]}")]
    public void ReadSupport_MalformedEmbeddedDeclaration_FailsClosedWithoutThrowing(string? json)
    {
        var descriptor = UpdaterCompatibility.ReadSupport(json, "0.50.2", "sha");
        descriptor.SupportedSchemas.ShouldBeEmpty();
        UpdaterCompatibility.Evaluate(descriptor, Contract()).ShouldNotBeNull();
    }

    [Theory]
    [InlineData("schemaVersion", "1")]
    [InlineData("minimumUpdaterVersion", "\"0.50.2\"")]
    [InlineData("requiredUpdaterCapabilities", "[\"planned-shutdown-v1\"]")]
    [InlineData("providedTargetCapabilities", "[\"planned-shutdown-v1\"]")]
    [InlineData("unknown", "true")]
    [InlineData("supportedContractSchemas", "[1,1]")]
    [InlineData("supportedUpdaterCapabilities", "\"planned-shutdown-v1\"")]
    public void ReadSupport_SourceOnlyOrInvalidProperties_FailsClosed(string property, string value)
    {
        var json = System.Text.Json.Nodes.JsonNode.Parse("{\"supportedContractSchemas\":[1],\"supportedUpdaterCapabilities\":[\"planned-shutdown-v1\"],\"requiredTargetCapabilities\":[\"planned-shutdown-v1\"]}")?.AsObject()
            ?? throw new InvalidOperationException("Missing fixture.");
        json[property] = System.Text.Json.Nodes.JsonNode.Parse(value);
        UpdaterCompatibility.ReadSupport(json.ToJsonString(), "0.50.2", "sha").SupportedSchemas.ShouldBeEmpty();
    }

    [Fact]
    public void Evaluate_OlderCliCannotPerformNewSourceBehavior_RejectsCapability()
        => (UpdaterCompatibility.Evaluate(new UpdaterDescriptor("0.50.2", "older-sha", [1], ["planned-shutdown-v1"], ["planned-shutdown-v1"]), Contract(required: "future-shutdown-v2")) ?? "unexpected compatibility").ShouldContain("future-shutdown-v2");

    [Fact]
    public void Evaluate_NewerCliRequiresBehaviorAbsentFromOlderSource_RejectsBackwardCombination()
    {
        var cli = new UpdaterDescriptor("9.0.0", "newer-sha", [1], ["planned-shutdown-v1"], ["future-target-v2"]);
        (UpdaterCompatibility.Evaluate(cli, Contract()) ?? "unexpected compatibility").ShouldContain("future-target-v2");
    }

    [Theory]
    [InlineData(true, "Q:/source with 'quote'/$dollar;[x]", "'Q:/source with ''quote''/$dollar;[x]'")]
    [InlineData(false, "/source with 'quote'/$dollar;[x]", "'/source with '\"'\"'quote'\"'\"'/$dollar;[x]'")]
    public void QuoteSource_ShellMetacharacters_AreLiteral(bool powershell, string path, string expected)
        => UpdaterCompatibility.QuoteSource(path, powershell).ShouldBe(expected);
}
