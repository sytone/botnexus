using System.Text.Json.Nodes;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;
using Shouldly;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

/// <summary>
/// Unit coverage for the portal-side dirty-path bookkeeping that turns "what the operator edited"
/// into the minimal atomic patch to send (issue #2059).
/// </summary>
/// <remarks>
/// The disk-level acceptance tests prove the WRITE is narrow and atomic. These prove the CLIENT
/// asks for a narrow write in the first place - which is where the original defect actually lived:
/// the pages had no notion of a dirty path at all, so the only save they could express was "write
/// back every section I loaded".
/// </remarks>
public sealed class ConfigDirtyPathTrackerTests
{
    private static JsonObject SampleConfig() => new()
    {
        ["gateway"] = new JsonObject
        {
            ["logLevel"] = "Information",
            ["listenUrl"] = "http://localhost:5005",
            ["cors"] = new JsonObject { ["allowedOrigins"] = new JsonArray("http://a") },
        },
        ["providers"] = new JsonObject
        {
            ["copilot"] = new JsonObject { ["enabled"] = true, ["apiKey"] = "***" },
        },
    };

    /// <summary>
    /// Nothing edited means nothing dirty and nothing to send. A save that fires here would be the
    /// whole defect: writing sections nobody touched.
    /// </summary>
    [Fact]
    public void NoEdits_ProducesNoOperations()
    {
        var tracker = new ConfigDirtyPathTracker();

        tracker.IsDirty.ShouldBeFalse();
        tracker.BuildOperations(SampleConfig()).ShouldBeEmpty();
    }

    /// <summary>
    /// One edited field produces exactly one operation addressing exactly that field - not its
    /// parent section.
    /// </summary>
    [Fact]
    public void SingleEditedField_ProducesOneExactPathOperation()
    {
        var config = SampleConfig();
        config["gateway"]!["logLevel"] = "Debug";

        var tracker = new ConfigDirtyPathTracker();
        tracker.Mark("gateway.logLevel");

        var operations = tracker.BuildOperations(config);

        operations.Count.ShouldBe(1);
        operations[0].Path.ShouldBe("gateway.logLevel");
        operations[0].Remove.ShouldBeFalse();
        operations[0].Value!.GetValue<string>().ShouldBe("Debug");
    }

    /// <summary>
    /// Editing one section must not enlist any other section in the save. This is the assertion
    /// that would have failed against the old implementation.
    /// </summary>
    [Fact]
    public void EditingOneSection_DoesNotEnlistOtherSections()
    {
        var config = SampleConfig();
        var tracker = new ConfigDirtyPathTracker();
        tracker.Mark("gateway.logLevel");

        var paths = tracker.BuildOperations(config).Select(o => o.Path).ToList();

        paths.ShouldBe(["gateway.logLevel"]);
        paths.ShouldNotContain(p => p.StartsWith("providers", StringComparison.Ordinal));
    }

    /// <summary>
    /// Two edits in different sections both travel in ONE batch, so the save is atomic across
    /// them rather than two independent writes that can half-commit.
    /// </summary>
    [Fact]
    public void EditsInTwoSections_TravelInOneBatch()
    {
        var config = SampleConfig();
        var tracker = new ConfigDirtyPathTracker();
        tracker.Mark("gateway.logLevel");
        tracker.Mark("providers.copilot.enabled");

        tracker.BuildOperations(config).Select(o => o.Path)
            .ShouldBe(["gateway.logLevel", "providers.copilot.enabled"]);
    }

    /// <summary>
    /// When a container and one of its members are both dirty, only the container is sent. Two
    /// overlapping operations in one batch would let the member write fight the container write
    /// depending on ordering.
    /// </summary>
    [Fact]
    public void AncestorAndDescendantDirty_SendsOnlyTheAncestor()
    {
        var config = SampleConfig();
        var tracker = new ConfigDirtyPathTracker();
        tracker.Mark("providers");
        tracker.Mark("providers.copilot.enabled");

        tracker.BuildOperations(config).Select(o => o.Path).ShouldBe(["providers"]);
    }

    /// <summary>
    /// A prefix that is not a path ancestor must not swallow a sibling. "gateway" is an ancestor
    /// of "gateway.logLevel" but NOT of a hypothetical "gatewayExtra".
    /// </summary>
    [Fact]
    public void SharedTextPrefixThatIsNotAnAncestor_IsNotSuppressed()
    {
        var config = SampleConfig();
        config["gatewayExtra"] = new JsonObject { ["x"] = 1 };

        var tracker = new ConfigDirtyPathTracker();
        tracker.Mark("gateway");
        tracker.Mark("gatewayExtra");

        tracker.BuildOperations(config).Select(o => o.Path).ShouldBe(["gateway", "gatewayExtra"]);
    }

    /// <summary>
    /// A dirty path whose node no longer exists is sent as a removal. Omitting it would leave the
    /// old value on disk while telling the operator the save succeeded.
    /// </summary>
    [Fact]
    public void DeletedNode_IsSentAsARemoval()
    {
        var config = SampleConfig();
        (config["providers"] as JsonObject)!.Remove("copilot");

        var tracker = new ConfigDirtyPathTracker();
        tracker.Mark("providers.copilot");

        var operations = tracker.BuildOperations(config);

        operations.Count.ShouldBe(1);
        operations[0].Path.ShouldBe("providers.copilot");
        operations[0].Remove.ShouldBeTrue();
    }

    /// <summary>
    /// Array element paths resolve through the index, so an edited list item is addressed as
    /// itself rather than collapsing to the whole array.
    /// </summary>
    [Fact]
    public void ArrayElementPath_ResolvesThroughTheIndex()
    {
        var config = SampleConfig();
        config["gateway"]!["cors"]!["allowedOrigins"]![0] = "http://changed";

        var tracker = new ConfigDirtyPathTracker();
        tracker.Mark("gateway.cors.allowedOrigins[0]");

        var operations = tracker.BuildOperations(config);

        operations.Count.ShouldBe(1);
        operations[0].Value!.GetValue<string>().ShouldBe("http://changed");
    }

    /// <summary>
    /// Reset clears the tracked set, so a reload or a committed save cannot leave stale paths that
    /// would be re-sent on the next save.
    /// </summary>
    [Fact]
    public void Reset_ClearsTrackedPaths()
    {
        var tracker = new ConfigDirtyPathTracker();
        tracker.Mark("gateway.logLevel");
        tracker.IsDirty.ShouldBeTrue();

        tracker.Reset();

        tracker.IsDirty.ShouldBeFalse();
        tracker.BuildOperations(SampleConfig()).ShouldBeEmpty();
    }

    /// <summary>
    /// A path under a section that does not exist in the loaded document still produces an
    /// operation. That is what allows a default-only section to be materialised by editing it -
    /// the case the old save path made structurally impossible.
    /// </summary>
    [Fact]
    public void PathUnderAbsentSection_StillProducesAnOperation()
    {
        var config = SampleConfig();
        config["cron"] = new JsonObject { ["tickIntervalSeconds"] = 120 };

        var tracker = new ConfigDirtyPathTracker();
        tracker.Mark("cron.tickIntervalSeconds");

        var operations = tracker.BuildOperations(config);

        operations.Count.ShouldBe(1);
        operations[0].Path.ShouldBe("cron.tickIntervalSeconds");
        operations[0].Value!.GetValue<int>().ShouldBe(120);
    }

    /// <summary>
    /// The emitted value is a clone: a later edit to the form must not retroactively change an
    /// operation already built for an in-flight save.
    /// </summary>
    [Fact]
    public void BuiltOperationValue_IsDetachedFromTheLiveForm()
    {
        var config = SampleConfig();
        var tracker = new ConfigDirtyPathTracker();
        tracker.Mark("providers.copilot");

        var operations = tracker.BuildOperations(config);
        config["providers"]!["copilot"]!["enabled"] = false;

        operations[0].Value!["enabled"]!.GetValue<bool>()
            .ShouldBeTrue("the operation must snapshot the value it was built from");
    }
}
