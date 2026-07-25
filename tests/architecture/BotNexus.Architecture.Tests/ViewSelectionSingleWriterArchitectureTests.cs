using System.Text.RegularExpressions;
using Shouldly;

namespace BotNexus.Architecture.Tests;

/// <summary>
/// Architecture fitness functions enforcing the epic #2245 single-writer view-selection contract
/// (final PBI #2249). Two durable guardrails:
/// </summary>
/// <remarks>
/// <para>
/// <b>Rule 1 — single writer.</b> The view-selection backing state (the <c>_selection</c> field of
/// type <c>ViewSelection</c> and the <c>SelectView(...)</c> mutation) is assigned in EXACTLY ONE
/// class, <c>ClientStateStore</c>. Any other production type that assigns <c>_selection = ...</c> or
/// declares a settable selection property / a public <c>SelectView</c> setter-style member fails the
/// build. This is the structural expression of #2246: the store is the sole writer of the active view.
/// </para>
/// <para>
/// <b>Rule 2 — event handlers are data-only.</b> Gateway inbound-event consumers
/// (<c>GatewayEventHandler</c> and any <c>*SignalRBridge</c>) must NOT reference <c>SelectView</c> or a
/// selection setter. Inbound events mutate data + raise notifications only; they may call
/// <c>MarkSelectionInvalid()</c> / <c>NotifyChanged()</c> but never reassign the active view (#2249).
/// </para>
/// <para>
/// Fence shape mirrors <see cref="AgentKindArchitectureTests"/>: (a) the real-source scan,
/// (b) a vacuity self-test proving the regex catches the canonical violation, (c) a false-positive
/// self-test proving it ignores the legitimate data-only shape.
/// </para>
/// </remarks>
public sealed class ViewSelectionSingleWriterArchitectureTests
{
    /// <summary>The one class permitted to assign the view-selection backing field / expose SelectView.</summary>
    private const string SoleWriterFileName = "ClientStateStore.cs";

    /// <summary>
    /// Repo-relative directory holding the portal's client-state services. Both the sole writer
    /// (<c>ClientStateStore</c>) and the inbound-event consumers live here.
    /// </summary>
    private static readonly string s_servicesRelativeDir = Path.Combine(
        "extensions",
        "BotNexus.Extensions.Channels.SignalR.BlazorClient.Core",
        "Services");

    // ── Rule 1: single writer of _selection / SelectView ──────────────────────

    [Fact]
    public void ViewSelectionBackingState_IsAssignedInExactlyOneClass()
    {
        var servicesDir = ServicesDir();

        var violations = new List<string>();
        foreach (var file in Directory.EnumerateFiles(servicesDir, "*.cs", SearchOption.AllDirectories))
        {
            if (string.Equals(Path.GetFileName(file), SoleWriterFileName, StringComparison.OrdinalIgnoreCase))
                continue;

            var stripped = StripComments(File.ReadAllText(file));
            if (AssignsSelectionBackingState(stripped))
                violations.Add($"  {Path.GetFileName(file)}");
        }

        violations.ShouldBeEmpty(
            "The view-selection backing state (_selection / a settable selection member) may only be " +
            $"assigned in {SoleWriterFileName}. Every read of the active view is a projection of that " +
            "single field, and SelectView(...) is its sole mutation path (#2246/#2249). If another type " +
            "assigns it or exposes a selection setter, the single-writer contract is broken.\n" +
            "Violations:\n" + string.Join("\n", violations));
    }

    [Fact]
    public void SoleWriter_ActuallyAssignsTheBackingState()
    {
        // Anti-vacuity: the sole-writer file MUST itself contain the assignment, otherwise the
        // Rule-1 scan above would pass trivially because nobody assigns it anywhere.
        var soleWriter = Path.Combine(ServicesDir(), SoleWriterFileName);
        File.Exists(soleWriter).ShouldBeTrue($"Expected the sole writer at {soleWriter}");
        AssignsSelectionBackingState(StripComments(File.ReadAllText(soleWriter)))
            .ShouldBeTrue($"{SoleWriterFileName} must assign _selection — it is the sole view-selection writer (#2246).");
    }

    // ── Rule 2: inbound-event handlers must not reference SelectView ───────────

    [Fact]
    public void InboundEventHandlers_DoNotReferenceSelectViewOrSelectionSetter()
    {
        var servicesDir = ServicesDir();

        var violations = new List<string>();
        foreach (var file in Directory.EnumerateFiles(servicesDir, "*.cs", SearchOption.AllDirectories))
        {
            var name = Path.GetFileName(file);
            if (!IsInboundEventConsumerFile(name))
                continue;

            var stripped = StripComments(File.ReadAllText(file));
            if (ReferencesSelectView(stripped))
                violations.Add($"  {name}");
        }

        violations.ShouldBeEmpty(
            "Gateway inbound-event consumers (GatewayEventHandler, *SignalRBridge) must not reference " +
            "SelectView / a selection setter. Inbound events are data-only: they mutate agent / " +
            "conversation / message state and may call MarkSelectionInvalid() or NotifyChanged(), but " +
            "must never reassign the active view out from under the user (#2249). Reintroducing a " +
            "SelectView call in an event handler is exactly the bug class this fence exists to stop.\n" +
            "Violations:\n" + string.Join("\n", violations));
    }

    [Fact]
    public void InboundEventConsumer_FileMatcher_MatchesTheKnownHandler()
    {
        // Anti-vacuity: prove the file matcher actually selects the real GatewayEventHandler, so the
        // Rule-2 scan is not silently examining zero files.
        IsInboundEventConsumerFile("GatewayEventHandler.cs").ShouldBeTrue();
        IsInboundEventConsumerFile("SubAgentSignalRBridge.cs").ShouldBeTrue();
        IsInboundEventConsumerFile("ClientStateStore.cs").ShouldBeFalse();
    }

    // ── Regex self-tests (vacuity + false-positive guards) ────────────────────

    [Fact]
    public void Rule1Regex_IsNotVacuous_AgainstSyntheticViolation()
    {
        const string violation = """
            public void Hijack(ViewSelection sel)
            {
                _selection = sel;
            }
            """;
        AssignsSelectionBackingState(violation).ShouldBeTrue(
            "Vacuity guard: the Rule-1 regex must match a raw `_selection = ...` assignment.");
    }

    [Fact]
    public void Rule1Regex_DoesNotFalsePositive_OnSelectionReads()
    {
        const string clean = """
            public string? Read() => _selection.AgentId;
            public SelectionSource Source => _selection.Source;
            if (string.Equals(_selection.AgentId, agentId)) { }
            """;
        AssignsSelectionBackingState(clean).ShouldBeFalse(
            "False-positive guard: reading _selection (projection) must not trip the single-writer fence.");
    }

    [Fact]
    public void Rule2Regex_IsNotVacuous_AgainstSyntheticViolation()
    {
        const string violation = """
            public void HandleSubAgentSpawned(SubAgentEventPayload payload)
            {
                _store.SelectView(payload.SubAgentId, string.Empty, SelectionSource.SubAgentView);
            }
            """;
        ReferencesSelectView(violation).ShouldBeTrue(
            "Vacuity guard: the Rule-2 regex must match a SelectView call inside an event handler.");
    }

    [Fact]
    public void Rule2Regex_DoesNotFalsePositive_OnDataOnlyHandler()
    {
        const string clean = """
            public void HandleSubAgentSpawned(SubAgentEventPayload payload)
            {
                agent.SubAgents[payload.SubAgentId] = new SubAgentInfo();
                _store.MarkSubAgent(payload.SubAgentId);
                _store.MarkSelectionInvalid();
                _store.NotifyChanged();
            }
            """;
        ReferencesSelectView(clean).ShouldBeFalse(
            "False-positive guard: a data-only handler that calls MarkSelectionInvalid/NotifyChanged " +
            "must not trip the Rule-2 fence.");
    }

    // ── Predicates ────────────────────────────────────────────────────────────

    /// <summary>
    /// True when the source ASSIGNS the view-selection backing state: a bare <c>_selection = ...</c>
    /// (but not <c>==</c> comparison), or declares a settable <c>ViewSelection</c> auto-property with
    /// a <c>set</c>/<c>init</c> accessor.
    /// </summary>
    private static bool AssignsSelectionBackingState(string source)
    {
        // `_selection =` but not `_selection ==` (comparison). Negative lookahead on the second '='.
        if (Regex.IsMatch(source, @"\b_selection\s*=(?!=)"))
            return true;

        // A publicly settable ViewSelection property would be an alternate write surface.
        if (Regex.IsMatch(source, @"\bViewSelection\s+\w+\s*\{[^}]*\bset\b", RegexOptions.Singleline))
            return true;

        return false;
    }

    /// <summary>True when the source references the <c>SelectView</c> seam by name (a call or member access).</summary>
    private static bool ReferencesSelectView(string source)
        => Regex.IsMatch(source, @"\bSelectView\s*\(");

    /// <summary>
    /// Files considered inbound-event consumers: the GatewayEventHandler and any *SignalRBridge.
    /// </summary>
    private static bool IsInboundEventConsumerFile(string fileName)
        => fileName.Equals("GatewayEventHandler.cs", StringComparison.OrdinalIgnoreCase)
        || fileName.EndsWith("SignalRBridge.cs", StringComparison.OrdinalIgnoreCase);

    // ── Helpers ────────────────────────────────────────────────────────────────

    private static string StripComments(string source)
    {
        var noBlock = Regex.Replace(source, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);
        return Regex.Replace(noBlock, @"//[^\r\n]*", string.Empty);
    }

    private static string ServicesDir()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "BotNexus.slnx")))
            current = current.Parent;
        current.ShouldNotBeNull("Could not locate repo root from " + AppContext.BaseDirectory);

        var dir = Path.Combine(current.FullName, "src", s_servicesRelativeDir);
        Directory.Exists(dir).ShouldBeTrue("Expected portal client-state services dir at " + dir);
        return dir;
    }
}
