using System.Text.RegularExpressions;
using Shouldly;

namespace BotNexus.Architecture.Tests;

/// <summary>
/// Architecture fitness functions enforcing the <c>#2246/#2249</c> single-writer contract for the
/// portal's active view: the view-selection backing state - the <c>_selection</c>
/// (<c>ViewSelection</c>) field, the <c>SelectView(...)</c> method body, and the selection setters
/// that construct a <c>new ViewSelection(...)</c> or assign <c>ViewSelection.None</c> - is mutated in
/// exactly ONE class, <c>ClientStateStore</c>. Every other read of the active agent / conversation is
/// a projection of that single value (see <c>ActiveAgentId</c> / <c>ActiveConversationId</c>).
///
/// This is the durable guardrail against regressing into ad-hoc active-agent mutation: before #2246
/// an inbound gateway event could assign the active view out from under the user. The seam has been
/// collapsed to a single writer (<c>ClientStateStore.SelectView</c>); these tests fail the build the
/// instant any other type re-introduces a view-selection assignment, or a gateway event handler /
/// SignalR bridge references <c>SelectView</c> or the selection setters.
/// </summary>
/// <remarks>
/// These are structural source-text smoke checks, mirroring the pattern established by
/// <c>SessionWriteLockArchitectureTests</c>. They confirm the shape a regression would have to defeat;
/// the behavioural pins live in <c>ClientStateStoreTests</c> (SelectView guard) and the seam harness
/// in <c>GatewayEventHandlerViewSelectionSeamTests</c>. Vacuity and false-positive guards below prove
/// the fences are not trivially green.
/// </remarks>
public sealed class SingleWriterViewSelectionArchitectureTests
{
    // The one class permitted to assign the view-selection backing state.
    private const string SoleWriterFile = "ClientStateStore.cs";

    // Any construction of a ViewSelection value, or an assignment of the sentinel ViewSelection.None,
    // is a view-selection mutation. The single-writer invariant is that these appear only in the sole
    // writer file.
    private static readonly Regex ViewSelectionMutationRegex = new(
        @"new\s+ViewSelection\s*\(|\bViewSelection\s*\.\s*None\b|_selection\s*=",
        RegexOptions.Compiled);

    // Handlers / bridges must not reach for the view-selection API at all.
    private static readonly Regex SelectionApiRegex = new(
        @"\.\s*SelectView\s*\(|new\s+ViewSelection\s*\(|\bViewSelection\s*\.\s*None\b|_selection\s*=",
        RegexOptions.Compiled);

    [Fact]
    public void ViewSelectionBackingState_IsAssigned_InExactlyOneClass()
    {
        var portalRoot = LocatePortalClientCoreServicesDir();

        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(portalRoot, "*.cs", SearchOption.AllDirectories))
        {
            if (string.Equals(Path.GetFileName(file), SoleWriterFile, StringComparison.Ordinal))
                continue;

            var source = StripComments(File.ReadAllText(file));
            var matches = ViewSelectionMutationRegex.Matches(source);
            if (matches.Count > 0)
            {
                offenders.Add($"  {Path.GetFileName(file)}: {matches.Count} view-selection assignment(s) - " +
                    $"first '{Snippet(source, matches[0].Index)}'");
            }
        }

        offenders.ShouldBeEmpty(
            "The view-selection backing state (the _selection / ViewSelection field, constructed via " +
            "'new ViewSelection(...)' or reset to 'ViewSelection.None') must be assigned in exactly ONE " +
            $"class ({SoleWriterFile}). Another type now assigns it, re-opening the ad-hoc active-agent " +
            "mutation hole #2246 closed: an inbound event could assign the active view out from under the " +
            "user. Route the change through ClientStateStore.SelectView(...) / MarkSelectionInvalid() " +
            "instead.\nOffenders:\n" + string.Join("\n", offenders));
    }

    [Fact]
    public void GatewayEventHandler_DoesNotReference_SelectViewOrSelectionSetters()
    {
        var path = Path.Combine(LocatePortalClientCoreServicesDir(), "GatewayEventHandler.cs");
        File.Exists(path).ShouldBeTrue("Expected GatewayEventHandler.cs at " + path);

        var source = StripComments(File.ReadAllText(path));
        var matches = SelectionApiRegex.Matches(source);

        matches.Count.ShouldBe(0,
            "GatewayEventHandler must NOT reference SelectView(...) or the view-selection setters. " +
            "Inbound gateway events are data-only: they may append messages / flag the selection invalid " +
            "(MarkSelectionInvalid), but they must never mutate the active view (#2246/#2249). A reference " +
            "here re-opens the hijack window where a SubAgentSpawned / streaming event switches the active " +
            "agent out from under the user.\nMatches:\n" +
            string.Join("\n", matches.Select(m => "  " + Snippet(source, m.Index))));
    }

    [Fact]
    public void SignalRBridges_DoNotReference_SelectViewOrSelectionSetters()
    {
        var bridgeFiles = LocateSignalRBridgeFiles();
        bridgeFiles.ShouldNotBeEmpty("Expected to locate *SignalRBridge.cs consumer files.");

        var offenders = new List<string>();
        foreach (var path in bridgeFiles)
        {
            var source = StripComments(File.ReadAllText(path));
            var matches = SelectionApiRegex.Matches(source);
            if (matches.Count > 0)
            {
                offenders.Add($"  {Path.GetFileName(path)}: {matches.Count} reference(s) - " +
                    $"first '{Snippet(source, matches[0].Index)}'");
            }
        }

        offenders.ShouldBeEmpty(
            "SignalR bridge event consumers (*SignalRBridge.cs) must NOT reference SelectView(...) or the " +
            "view-selection setters. They translate hub events into store mutations that are data-only; " +
            "the active view is owned solely by ClientStateStore.SelectView (#2246/#2249).\nOffenders:\n" +
            string.Join("\n", offenders));
    }

    // ── Vacuity / false-positive guards ──────────────────────────────────────

    [Fact]
    public void Fence_IsNotVacuous_DetectsSyntheticViewSelectionAssignment()
    {
        const string violating = """
            public void HijackView(string agentId)
            {
                _selection = new ViewSelection(agentId, string.Empty, SelectionSource.Bootstrap);
            }
            """;
        ViewSelectionMutationRegex.IsMatch(StripComments(violating)).ShouldBeTrue(
            "Vacuity guard: the single-writer fence must catch a 'new ViewSelection(...)' / '_selection =' " +
            "assignment. If this fails the fence can no longer catch the #2246 regression class.");
    }

    [Fact]
    public void Fence_IsNotVacuous_DetectsSyntheticSelectViewCall()
    {
        const string violating = """
            public void HandleSubAgentSpawned(SubAgentEventPayload payload)
            {
                _store.SelectView(payload.SubAgentId, string.Empty, SelectionSource.SubAgentView);
            }
            """;
        SelectionApiRegex.IsMatch(StripComments(violating)).ShouldBeTrue(
            "Vacuity guard: the handler/bridge fence must catch a '.SelectView(...)' call. If this fails " +
            "an inbound event could switch the active view and the fence would not notice.");
    }

    [Fact]
    public void Fence_DoesNotFalsePositive_OnDataOnlyHandlerShape()
    {
        // The canonical data-only inbound-event shape: append a message, flag the selection invalid,
        // notify. No view mutation. The fence must stay green on this.
        const string clean = """
            public void HandleAgentRemoved(string agentId)
            {
                _store.RemoveAgent(agentId);
                _store.MarkSelectionInvalid();
                _store.NotifyChanged();
            }
            """;
        SelectionApiRegex.IsMatch(StripComments(clean)).ShouldBeFalse(
            "False-positive guard: the fence must NOT flag the legitimate data-only handler shape " +
            "(RemoveAgent + MarkSelectionInvalid + NotifyChanged). An over-broad fence gets disabled " +
            "instead of used.");
    }

    [Fact]
    public void Fence_DoesNotFalsePositive_OnReadOnlyProjectionAccess()
    {
        // Reading the derived projections is always allowed - only ASSIGNING the backing state is the
        // single-writer concern. The mutation regex must not trip on a read of ActiveAgentId.
        const string clean = """
            public void HandleMessageEnd(AgentStreamEvent evt)
            {
                if (agent.AgentId != _store.ActiveAgentId)
                    agent.UnreadCount++;
            }
            """;
        ViewSelectionMutationRegex.IsMatch(StripComments(clean)).ShouldBeFalse(
            "False-positive guard: reading the derived ActiveAgentId projection is not a view-selection " +
            "assignment and must not be flagged by the single-writer fence.");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Removes line and block comments plus string/char/verbatim literals so the fences match real code
    /// tokens, not commentary (the source files carry extensive #2246 rationale comments that name
    /// SelectView / ViewSelection) or string literals.
    /// </summary>
    private static string StripComments(string source)
    {
        // Order matters: strings/chars first (they can contain // or /*), then comments.
        // Verbatim and interpolated strings are approximated well enough for token detection.
        var noBlock = Regex.Replace(source, @"/\*.*?\*/", " ", RegexOptions.Singleline);
        var noLine = Regex.Replace(noBlock, @"//[^\n]*", " ");
        var noVerbatim = Regex.Replace(noLine, @"@""(?:""""|[^""])*""", "\"\"", RegexOptions.Singleline);
        var noStrings = Regex.Replace(noVerbatim, @"""(?:\\.|[^""\\])*""", "\"\"");
        var noChars = Regex.Replace(noStrings, @"'(?:\\.|[^'\\])'", "' '");
        return noChars;
    }

    private static string Snippet(string source, int idx)
    {
        var start = Math.Max(0, idx - 8);
        var end = Math.Min(source.Length, idx + 50);
        return source[start..end].Replace("\r", string.Empty).Replace("\n", "\\n");
    }

    private static string LocatePortalClientCoreServicesDir()
    {
        var srcRoot = FindSourceRoot();
        var path = Path.Combine(srcRoot, "extensions",
            "BotNexus.Extensions.Channels.SignalR.BlazorClient.Core", "Services");
        Directory.Exists(path).ShouldBeTrue("Expected portal client Core Services dir at " + path);
        return path;
    }

    private static List<string> LocateSignalRBridgeFiles()
    {
        var srcRoot = FindSourceRoot();
        var signalRExt = Path.Combine(srcRoot, "extensions", "BotNexus.Extensions.Channels.SignalR");
        Directory.Exists(signalRExt).ShouldBeTrue("Expected SignalR extension dir at " + signalRExt);
        return Directory
            .EnumerateFiles(signalRExt, "*SignalRBridge.cs", SearchOption.AllDirectories)
            .ToList();
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
