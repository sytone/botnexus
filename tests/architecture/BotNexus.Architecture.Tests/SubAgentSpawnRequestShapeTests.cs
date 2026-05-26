using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
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

    private static readonly Regex s_modeSwitchPattern = new(
        @"switch\s*\(\s*request\.Mode\s*\)",
        RegexOptions.Compiled);

    private static readonly Regex s_embodyPattern = new(
        @"\b(is|case)\s+Embody\b",
        RegexOptions.Compiled);

    private static readonly Regex s_mirrorPattern = new(
        @"\b(is|case)\s+Mirror\b",
        RegexOptions.Compiled);

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

        var hasModeSwitch = s_modeSwitchPattern.IsMatch(source);
        var hasEmbodyPattern = s_embodyPattern.IsMatch(source);
        var hasMirrorPattern = s_mirrorPattern.IsMatch(source);

        (hasModeSwitch || (hasEmbodyPattern && hasMirrorPattern)).ShouldBeTrue(
            "DefaultSubAgentManager.cs must drive spawn behaviour by " +
            "pattern-matching on request.Mode (either `switch (request.Mode)` " +
            "or `is Embody` / `case Embody` plus `is Mirror` / `case Mirror`). " +
            "The discriminated-union contract from #562 step 5 requires Mode " +
            "to be the single source of truth.\n" +
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
    /// Vacuity guard for the legacy-field scan in
    /// <see cref="DefaultSubAgentManager_PatternMatchesOnMode_NotLegacyFields"/>:
    /// the synthetic shape must be caught. Without this test, a broken regex
    /// would silently pass on real violations.
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

    /// <summary>
    /// Vacuity guard for the switch / pattern detection clause in
    /// <see cref="DefaultSubAgentManager_PatternMatchesOnMode_NotLegacyFields"/>.
    /// Three synthetic shapes must be matched by their respective regexes
    /// (`switch (request.Mode)`, `is Embody`, `case Mirror`); a clean shape
    /// that contains none of them must be rejected. Without this guard a
    /// regex regression could silently downgrade the fence to a no-op while
    /// the legacy-field scan continues to pass on its own.
    /// </summary>
    [Fact]
    public void Fence_DetectsSyntheticModeSwitchShapes()
    {
        // Realistic mutations: missing space after `switch`, padded parens —
        // shapes an IDE or human might produce. `request . Mode` with spaces
        // inside the dotted access is not a realistic mutation (no formatter
        // emits it) and is deliberately not pinned.
        const string syntheticSwitchNoSpace = "var x = switch(request.Mode) { _ => 0 };";
        s_modeSwitchPattern.IsMatch(syntheticSwitchNoSpace).ShouldBeTrue(
            "The mode-switch regex failed to match `switch(request.Mode)` " +
            "(missing space after switch). Real code in this shape would " +
            "slip past the fence.");

        const string syntheticSwitchPadded = "var x = switch ( request.Mode ) { _ => 0 };";
        s_modeSwitchPattern.IsMatch(syntheticSwitchPadded).ShouldBeTrue(
            "The mode-switch regex failed to match `switch ( request.Mode )` " +
            "(padded parens). Real code in this shape would slip past the fence.");

        const string syntheticIsEmbody = "if (request.Mode is Embody e) { return; }";
        s_embodyPattern.IsMatch(syntheticIsEmbody).ShouldBeTrue(
            "The Embody-pattern regex failed to match `is Embody e`. " +
            "Pattern-match regressions would slip past the fence.");

        const string syntheticCaseMirror = "switch (m) { case Mirror x: break; }";
        s_mirrorPattern.IsMatch(syntheticCaseMirror).ShouldBeTrue(
            "The Mirror-pattern regex failed to match `case Mirror x`. " +
            "Pattern-match regressions would slip past the fence.");

        const string cleanShape = "var p = parentDescriptor.ModelId;";
        s_modeSwitchPattern.IsMatch(cleanShape).ShouldBeFalse(
            "The mode-switch regex falsely matched clean source. The fence " +
            "would emit spurious failures on unrelated edits.");
        s_embodyPattern.IsMatch(cleanShape).ShouldBeFalse(
            "The Embody-pattern regex falsely matched clean source.");
        s_mirrorPattern.IsMatch(cleanShape).ShouldBeFalse(
            "The Mirror-pattern regex falsely matched clean source.");
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
