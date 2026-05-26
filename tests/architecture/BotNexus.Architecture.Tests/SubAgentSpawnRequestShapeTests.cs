using System.Reflection;
using System.Runtime.CompilerServices;
using BotNexus.Gateway.Abstractions.Models;
using Shouldly;

namespace BotNexus.Architecture.Tests;

/// <summary>
/// Architecture fitness functions enforcing the Phase 5 / F-6 (#562 step 5)
/// final shape of <see cref="SubAgentSpawnRequest"/>: the bag of optional
/// top-level fields (<c>TargetAgentId</c>, <c>Name</c>, <c>ModelOverride</c>,
/// <c>ApiProviderOverride</c>, <c>ToolIds</c>, <c>SystemPromptOverride</c>,
/// <c>Archetype</c>) has been collapsed into a required <c>Mode</c> property of
/// type <see cref="SubAgentSpawnMode"/> — a discriminated union of
/// <see cref="Embody"/> and <see cref="Mirror"/>.
/// </summary>
/// <remarks>
/// The old shape allowed callers to populate any subset of the legacy fields
/// and the runtime path silently picked an interpretation. The new shape
/// forces every spawn to pick exactly one of the two modes at construction
/// time. These fences make the regression structurally impossible:
/// 1. The seven deleted properties must not exist on the type.
/// 2. <c>Mode</c> must exist, be <c>required</c>, typed as
///    <see cref="SubAgentSpawnMode"/>, and non-nullable.
/// 3. <c>DefaultSubAgentManager.SpawnAsync</c> must pattern-match on
///    <c>request.Mode</c> rather than reading any legacy field.
/// </remarks>
public sealed class SubAgentSpawnRequestShapeTests
{
    private static readonly string[] s_deletedLegacyProperties =
    {
        "TargetAgentId",
        "Name",
        "ModelOverride",
        "ApiProviderOverride",
        "ToolIds",
        "SystemPromptOverride",
        "Archetype",
    };

    /// <summary>
    /// Asserts each legacy property removed in #562 step 5 no longer exists
    /// on <see cref="SubAgentSpawnRequest"/>. If any one reappears, the
    /// mode-bag bug class is re-opened.
    /// </summary>
    [Fact]
    public void SubAgentSpawnRequest_DoesNotExpose_LegacyFields()
    {
        var requestType = typeof(SubAgentSpawnRequest);
        var resurrected = s_deletedLegacyProperties
            .Where(name => requestType.GetProperty(name, BindingFlags.Public | BindingFlags.Instance) is not null)
            .ToArray();

        resurrected.ShouldBeEmpty(
            "SubAgentSpawnRequest has resurrected one or more legacy " +
            "properties that were deleted in #562 step 5. The Mode discriminated " +
            "union (Embody | Mirror) is now the single source of truth for spawn " +
            "intent; reintroducing any of these fields reopens the mode-bag bug " +
            "class (callers populating an inconsistent subset that the runtime " +
            "silently interprets).\n" +
            "Resurrected: " + string.Join(", ", resurrected));
    }

    /// <summary>
    /// Asserts <c>Mode</c> is declared as a <c>required</c> non-nullable
    /// <see cref="SubAgentSpawnMode"/>. The required modifier prevents pinless
    /// construction at compile time; the non-nullable type makes the null
    /// case structurally unreachable inside <c>DefaultSubAgentManager.SpawnAsync</c>.
    /// </summary>
    [Fact]
    public void SubAgentSpawnRequest_Mode_IsRequired_AndTypedAsSubAgentSpawnMode()
    {
        var modeProperty = typeof(SubAgentSpawnRequest)
            .GetProperty("Mode", BindingFlags.Public | BindingFlags.Instance);

        modeProperty.ShouldNotBeNull(
            "SubAgentSpawnRequest must expose a public instance property named " +
            "'Mode'. It carries the Embody | Mirror discriminated union and is " +
            "the single source of truth for spawn intent.");

        modeProperty!.PropertyType.ShouldBe(typeof(SubAgentSpawnMode),
            "SubAgentSpawnRequest.Mode must be typed as SubAgentSpawnMode (the " +
            "discriminated-union root), not a concrete subclass or an interface. " +
            "Pattern-matching on Mode in DefaultSubAgentManager.SpawnAsync " +
            "depends on the root type being exposed.");

        modeProperty.CustomAttributes
            .Any(attr => attr.AttributeType == typeof(RequiredMemberAttribute))
            .ShouldBeTrue(
                "SubAgentSpawnRequest.Mode must be marked with the 'required' " +
                "modifier (RequiredMemberAttribute). Without it, callers can " +
                "construct a request with Mode = null and the manager's switch " +
                "loses its compile-time guarantee.");

        var nullabilityContext = new NullabilityInfoContext();
        var nullability = nullabilityContext.Create(modeProperty);
        nullability.WriteState.ShouldBe(NullabilityState.NotNull,
            "SubAgentSpawnRequest.Mode must be a non-nullable reference. A " +
            "nullable Mode forces the manager to keep a legacy fallback arm and " +
            "defeats the discriminated-union contract.");
    }

    /// <summary>
    /// Source-scan on <c>DefaultSubAgentManager.cs</c>: the spawn path must
    /// pattern-match on <c>request.Mode</c> and must not read any of the
    /// deleted legacy fields. Catches a regression where someone re-adds a
    /// property to <see cref="SubAgentSpawnRequest"/> and starts reading it
    /// in the manager.
    /// </summary>
    [Fact]
    public void DefaultSubAgentManager_PatternMatchesOnMode_NotLegacyFields()
    {
        var managerPath = LocateManagerFile();
        var source = File.ReadAllText(managerPath);

        var hasModeSwitch = source.Contains("switch (request.Mode)", StringComparison.Ordinal);
        var hasEmbodyPattern = source.Contains("Embody embody", StringComparison.Ordinal);
        var hasMirrorPattern = source.Contains("Mirror mirror", StringComparison.Ordinal);

        (hasModeSwitch || (hasEmbodyPattern && hasMirrorPattern)).ShouldBeTrue(
            "DefaultSubAgentManager.cs must drive spawn behaviour by " +
            "pattern-matching on request.Mode (either `switch (request.Mode)` " +
            "or `is Embody` plus `is Mirror`). The discriminated-union contract " +
            "from #562 step 5 requires Mode to be the single source of truth.\n" +
            "File: " + managerPath);

        var leakedReads = s_deletedLegacyProperties
            .Where(name => source.Contains("request." + name, StringComparison.Ordinal))
            .ToArray();

        leakedReads.ShouldBeEmpty(
            "DefaultSubAgentManager.cs reads at least one legacy spawn-request " +
            "field that was deleted in #562 step 5. These reads must go through " +
            "the typed Mode arms (Embody / Mirror) — re-adding them resurrects " +
            "the bag-of-optionals shape and silently widens the runtime contract.\n" +
            "Leaked reads: " + string.Join(", ", leakedReads.Select(name => "request." + name)) + "\n" +
            "File: " + managerPath);
    }

    /// <summary>
    /// Vacuity guard: the legacy-property scan in
    /// <see cref="DefaultSubAgentManager_PatternMatchesOnMode_NotLegacyFields"/>
    /// must actually catch a violation when one is present. This synthetic
    /// shape would slip through silently if the fence regex were ever broken.
    /// </summary>
    [Fact]
    public void Fence_DetectsSyntheticLegacyFieldRead()
    {
        var syntheticSource = """
            namespace Synthetic;
            public sealed class Probe
            {
                public void Spawn(object request)
                {
                    var x = request.TargetAgentId; // synthetic violation
                }
            }
            """;

        var leaks = s_deletedLegacyProperties
            .Where(name => syntheticSource.Contains("request." + name, StringComparison.Ordinal))
            .ToArray();

        leaks.ShouldContain("TargetAgentId",
            "The legacy-field scan failed to detect a synthetic `request.TargetAgentId` " +
            "read. The fence in DefaultSubAgentManager_PatternMatchesOnMode_NotLegacyFields " +
            "would also silently miss real violations.");
    }

    private static string LocateManagerFile()
    {
        var srcRoot = FindSourceRoot();
        var path = Path.Combine(srcRoot, "gateway", "BotNexus.Gateway", "Agents", "DefaultSubAgentManager.cs");
        File.Exists(path).ShouldBeTrue("Expected DefaultSubAgentManager.cs at " + path);
        return path;
    }

    private static string FindSourceRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "BotNexus.slnx")))
        {
            current = current.Parent;
        }

        current.ShouldNotBeNull("Could not locate repo root from " + AppContext.BaseDirectory);
        var srcRoot = Path.Combine(current!.FullName, "src");
        Directory.Exists(srcRoot).ShouldBeTrue("Expected src/ under " + current.FullName);
        return srcRoot;
    }
}
