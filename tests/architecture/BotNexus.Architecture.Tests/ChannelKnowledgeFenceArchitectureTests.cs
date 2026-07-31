using System.Text;
using System.Text.RegularExpressions;

namespace BotNexus.Architecture.Tests;

/// <summary>
/// #2086 (epic #2084): fitness functions that make concrete channel knowledge inside generic
/// gateway orchestration a build failure.
///
/// <para>This fence lands BEFORE the migration slices (#2087/#2088/#2089/#2090/#2091) so those
/// slices cannot recreate the coupling they are removing. Existing violations are captured in a
/// narrow, per-file, per-rule baseline below. Each baseline entry names a specific file (never a
/// namespace or folder) and is linked to the child issue that will delete it.</para>
///
/// <para><b>The baseline shrinks monotonically by construction.</b>
/// <see cref="Baseline_ContainsNoStaleEntries"/> FAILS if a baseline entry no longer corresponds to
/// a real violation, so an entry cannot silently outlive the coupling it was meant to phase out.
/// <see cref="Rule1"/>..<see cref="Rule7"/> FAIL on any violation not in the baseline.</para>
/// </summary>
public sealed class ChannelKnowledgeFenceArchitectureTests
{
    // ---------------------------------------------------------------------------------------
    // Scope
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Generic gateway orchestration projects. This is a SCOPE definition (which code is generic
    /// orchestration), not an exemption list: projects outside it are not orchestration at all.
    /// Notably excluded: BotNexus.Gateway.Api (composition root / HTTP host - it legitimately wires
    /// AddSignalR), BotNexus.Gateway.Telemetry (exporter names), BotNexus.Cli, BotNexus.Cron.
    /// </summary>
    private static readonly string[] s_orchestrationProjects =
    [
        Path.Combine("gateway", "BotNexus.Gateway"),
        Path.Combine("gateway", "BotNexus.Gateway.Abstractions"),
        Path.Combine("gateway", "BotNexus.Gateway.Channels"),
        Path.Combine("gateway", "BotNexus.Gateway.Contracts"),
        Path.Combine("gateway", "BotNexus.Gateway.Conversations"),
        Path.Combine("gateway", "BotNexus.Gateway.Dispatching"),
        Path.Combine("gateway", "BotNexus.Gateway.Sessions"),
        Path.Combine("gateway", "BotNexus.Gateway.Webhooks"),
    ];

    /// <summary>Rule 1 scope: generic/core source trees that must not know channel extensions exist.</summary>
    private static readonly string[] s_genericSourceTrees = ["gateway", "domain", "agent", "persistence"];

    /// <summary>
    /// Rule 4 scope: the conversation/session model surfaces. Extension-local recipient concepts
    /// (SignalR connection/group ids, Service Bus pending reply queues) must never appear here.
    /// Deliberately does NOT include Gateway/Satellites - satellite connection ids are a separate
    /// transport concern outside epic #2084.
    /// </summary>
    private static readonly string[] s_conversationModelScopes =
    [
        Path.Combine("domain", "BotNexus.Domain", "Gateway", "Models"),
        Path.Combine("domain", "BotNexus.Domain", "World"),
        Path.Combine("gateway", "BotNexus.Gateway.Contracts", "Conversations"),
        Path.Combine("gateway", "BotNexus.Gateway.Contracts", "Events"),
        Path.Combine("gateway", "BotNexus.Gateway.Contracts", "Sessions"),
        Path.Combine("gateway", "BotNexus.Gateway.Conversations"),
    ];

    // ---------------------------------------------------------------------------------------
    // Baseline: temporary, per-file, per-rule exemptions. NO namespace or folder entries (AC3).
    // Each entry MUST cite the child issue that deletes it. Removing the last entry for a rule is
    // the definition of that migration slice being complete.
    // ---------------------------------------------------------------------------------------

    private sealed record BaselineEntry(string Rule, string RelativePath, string Reason, string Issue);

    private static readonly BaselineEntry[] s_baseline =
    [
        // ---- Rule 2: concrete channel key literals inside generic orchestration ----

        // GatewayHost hard-codes "signalr" three times to decide whether the primary terminal
        // channel is SignalR and to filter observer bindings down to SignalR ones (#332 legacy).
        // Deleted when SignalR becomes an ordinary conversation event projection.
        new("R2", Path.Combine("gateway", "BotNexus.Gateway", "GatewayHost.cs"),
            "hard-coded \"signalr\" terminal-channel comparison and observer-binding filter", "#2089"),

        // WorkspaceContextBuilder stamps Channel = "signalr" onto the synthetic workspace context.
        // Deleted when SignalR context is projected from the generic event seam.
        new("R2", Path.Combine("gateway", "BotNexus.Gateway", "Agents", "WorkspaceContextBuilder.cs"),
            "stamps Channel = \"signalr\" on the synthetic workspace context", "#2089"),

        // InternalChannelAdapter falls back to the "signalr" adapter when no target resolves.
        // Deleted with legacy direct delivery.
        new("R2", Path.Combine("gateway", "BotNexus.Gateway", "Channels", "InternalChannelAdapter.cs"),
            "ChannelKey.From(\"signalr\") delivery fallback (x2)", "#2091"),

        // DefaultConversationRouter keeps a "signalr" conversation-first routing key set.
        // Deleted when outbound binding fan-out is retired.
        new("R2", Path.Combine("gateway", "BotNexus.Gateway.Conversations", "DefaultConversationRouter.cs"),
            "conversation-first routing key set containing \"signalr\"", "#2091"),

        // ---- Rule 3: concrete channel resolved for observer/fan-out behaviour ----

        // `signalRObservers` is literally the coupling epic #2084 exists to remove: generic
        // orchestration resolving a SignalR-specific observer set for cross-channel live update.
        new("R3", Path.Combine("gateway", "BotNexus.Gateway", "GatewayHost.cs"),
            "resolves a `signalRObservers` set for stream fan-out (#332 legacy)", "#2089"),

        // ---- Rule 5: direct IChannelAdapter send calls outside channel extensions ----

        // Agent-loop streaming deltas/events and turn replies pushed straight at adapters.
        new("R5", Path.Combine("gateway", "BotNexus.Gateway", "GatewayHost.cs"),
            "direct SendStreamDeltaAsync/SendStreamEventAsync/SendAsync from the agent loop", "#2087"),

        // Session lifecycle notifications delivered by direct adapter send.
        new("R5", Path.Combine("gateway", "BotNexus.Gateway", "Sessions", "InterruptedTurnNotificationService.cs"),
            "direct adapter SendAsync for the interrupted-turn notice", "#2088"),
        new("R5", Path.Combine("gateway", "BotNexus.Gateway", "Sessions", "SessionCompactionCoordinator.cs"),
            "direct adapter SendAsync for the compaction notice", "#2088"),

        // Legacy direct delivery / outbound binding fan-out.
        new("R5", Path.Combine("gateway", "BotNexus.Gateway", "OutboundResponseDeliverer.cs"),
            "direct adapter SendAsync in the outbound binding fan-out loop", "#2091"),
        new("R5", Path.Combine("gateway", "BotNexus.Gateway", "Channels", "InternalChannelAdapter.cs"),
            "re-dispatches directly onto the resolved target adapter", "#2091"),
        new("R5", Path.Combine("gateway", "BotNexus.Gateway.Dispatching", "DefaultInboundMessageOrchestrator.cs"),
            "direct adapter SendAsync for the inbound rejection reply", "#2091"),
    ];

    // ---------------------------------------------------------------------------------------
    // Rules
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void Rule1_GenericProjects_DoNotReferenceConcreteChannelExtensions()
        => AssertRule("R1", FindRule1Violations());

    [Fact]
    public void Rule2_Orchestration_ContainsNoConcreteChannelKeyLiterals()
        => AssertRule("R2", FindRule2Violations());

    [Fact]
    public void Rule3_Orchestration_DoesNotResolveConcreteChannelForObserverFanOut()
        => AssertRule("R3", FindRule3Violations());

    [Fact]
    public void Rule4_ConversationModels_ContainNoExtensionLocalRecipientConcepts()
        => AssertRule("R4", FindRule4Violations());

    [Fact]
    public void Rule5_Orchestration_DoesNotCallChannelAdapterSendDirectly()
        => AssertRule("R5", FindRule5Violations());

    [Fact]
    public void Rule6_Orchestration_DoesNotDependOnSignalRSpecificNotifiersOrBridges()
        => AssertRule("R6", FindRule6Violations());

    [Fact]
    public void Rule7_ChannelExtensions_CanReachTheGenericEventSinkContract()
        => AssertRule("R7", FindRule7Violations());

    // ---------------------------------------------------------------------------------------
    // Baseline integrity
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// The baseline must shrink monotonically. A stale entry silently re-permits the coupling it
    /// was meant to phase out, which is exactly how exemption lists rot - so a stale entry is a
    /// FAILURE, not a warning. Delete the entry in the same PR that removes the violation.
    /// </summary>
    [Fact]
    public void Baseline_ContainsNoStaleEntries()
    {
        var live = AllViolations()
            .Select(v => v.Rule + "|" + v.RelativePath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var stale = s_baseline
            .Where(b => !live.Contains(b.Rule + "|" + b.RelativePath))
            .Select(b => $"{b.Rule} {b.RelativePath} (was: {b.Reason}; {b.Issue})")
            .ToList();

        stale.ShouldBeEmpty(
            "#2086: the channel-knowledge baseline must shrink monotonically. These entries no longer " +
            "correspond to a real violation - delete them from s_baseline:\n  " +
            string.Join("\n  ", stale));
    }

    /// <summary>Every baseline entry must name a real file, and must cite a child issue (AC2/AC3).</summary>
    [Fact]
    public void Baseline_EntriesAreSpecificFilesLinkedToChildIssues()
    {
        var src = SourceRoot();
        var problems = new List<string>();
        foreach (var b in s_baseline)
        {
            if (b.RelativePath.EndsWith(Path.DirectorySeparatorChar) || b.RelativePath.EndsWith('/'))
                problems.Add($"{b.Rule} {b.RelativePath}: folder exemptions are forbidden (AC3)");
            else if (!Path.GetExtension(b.RelativePath).Equals(".cs", StringComparison.OrdinalIgnoreCase)
                     && !Path.GetExtension(b.RelativePath).Equals(".csproj", StringComparison.OrdinalIgnoreCase))
                problems.Add($"{b.Rule} {b.RelativePath}: must be a specific .cs/.csproj file, not a namespace or folder (AC3)");
            else if (!File.Exists(Path.Combine(src, b.RelativePath)))
                problems.Add($"{b.Rule} {b.RelativePath}: file does not exist");

            if (!Regex.IsMatch(b.Issue, @"#20(8[7-9]|9[01])\b"))
                problems.Add($"{b.Rule} {b.RelativePath}: must link a child issue (#2087-#2091), got '{b.Issue}'");
            if (string.IsNullOrWhiteSpace(b.Reason))
                problems.Add($"{b.Rule} {b.RelativePath}: must carry a reason");
        }

        problems.ShouldBeEmpty(
            "#2086 AC2/AC3: exemptions must be explicit, per-file, commented and linked to child issues.\n  "
            + string.Join("\n  ", problems));
    }

    // ---------------------------------------------------------------------------------------
    // Anti-vacuity: a misrooted scan must not be able to pass green (#2349).
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void Scan_ActuallyReachesTheSourceTree()
    {
        var src = SourceRoot();
        Directory.Exists(src).ShouldBeTrue($"source root not found: {src}");

        var orchestrationFiles = s_orchestrationProjects.SelectMany(p => CsFiles(Path.Combine(src, p))).Count();
        orchestrationFiles.ShouldBeGreaterThan(
            200, $"expected the 8 orchestration projects to contain many .cs files, scanned {orchestrationFiles}");

        var orchestrationProjectsFound = s_orchestrationProjects.Count(p => Directory.Exists(Path.Combine(src, p)));
        orchestrationProjectsFound.ShouldBe(s_orchestrationProjects.Length,
            "every declared orchestration project directory must exist (rename? update the scope list)");

        var modelFilesFound = s_conversationModelScopes.SelectMany(p => CsFiles(Path.Combine(src, p))).Count();
        modelFilesFound.ShouldBeGreaterThan(10, $"rule 4 scope scanned only {modelFilesFound} files");

        var genericCsprojs = s_genericSourceTrees
            .SelectMany(t => SafeFiles(Path.Combine(src, t), "*.csproj")).Count();
        genericCsprojs.ShouldBeGreaterThan(20, $"rule 1 scanned only {genericCsprojs} project files");

        ChannelExtensionProjects().Count.ShouldBeGreaterThan(4,
            "rule 7 must find the channel extension host projects");
    }

    // ---------------------------------------------------------------------------------------
    // Synthetic fixtures (AC5): each detector is proven to catch a forbidden shape, in-memory,
    // independent of whether the repo currently happens to contain one.
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void Fixture_Rule1Detector_CatchesSyntheticForbiddenProjectReference()
    {
        HasChannelExtensionReference(
            """<ProjectReference Include="..\..\extensions\BotNexus.Extensions.Channels.SignalR\BotNexus.Extensions.Channels.SignalR.csproj" />""")
            .ShouldBeTrue();
        HasChannelExtensionReference(
            """<ProjectReference Include="..\..\domain\BotNexus.Domain\BotNexus.Domain.csproj" />""")
            .ShouldBeFalse();
    }

    [Fact]
    public void Fixture_Rule2Detector_CatchesSyntheticChannelKeyLiteral()
    {
        ChannelKeyLiterals("""var x = ChannelKey.From("signalr");""").ShouldNotBeEmpty();
        ChannelKeyLiterals("""if (t == "telegram") { }""").ShouldNotBeEmpty();
        ChannelKeyLiterals("""// the signalr hub does "telegram" things""").ShouldBeEmpty();
        ChannelKeyLiterals("""/// <summary>e.g. "signalr", "telegram"</summary>""").ShouldBeEmpty();
        ChannelKeyLiterals("""var x = "conversation";""").ShouldBeEmpty();
    }

    [Fact]
    public void Fixture_Rule3Detector_CatchesSyntheticConcreteChannelObserver()
    {
        ChannelSpecificObserverSymbols("var signalRObservers = bindings.ToList();").ShouldNotBeEmpty();
        ChannelSpecificObserverSymbols("var telegramFanOut = x;").ShouldNotBeEmpty();
        ChannelSpecificObserverSymbols("var observers = bindings.ToList();").ShouldBeEmpty();
    }

    [Fact]
    public void Fixture_Rule4Detector_CatchesSyntheticExtensionLocalRecipientConcept()
    {
        ExtensionLocalRecipientSymbols("public string? ConnectionId { get; init; }").ShouldNotBeEmpty();
        ExtensionLocalRecipientSymbols("public string PendingReplyQueue { get; init; }").ShouldNotBeEmpty();
        ExtensionLocalRecipientSymbols("public string ChannelAddress { get; init; }").ShouldBeEmpty();
        // Config-UI grouping is not a channel recipient concept.
        ExtensionLocalRecipientSymbols("GroupName = \"Agent\",").ShouldBeEmpty();
    }

    [Fact]
    public void Fixture_Rule5Detector_CatchesSyntheticDirectAdapterSend()
    {
        DirectAdapterSends("await adapter.SendAsync(new OutboundMessage { });").ShouldNotBeEmpty();
        DirectAdapterSends("await ch.SendStreamDeltaAsync(target, delta, ct);").ShouldNotBeEmpty();
        DirectAdapterSends("await a.SendStreamEventAsync(target, evt, ct);").ShouldNotBeEmpty();
        // HttpClient.SendAsync must not be mistaken for a channel adapter send.
        DirectAdapterSends("var r = await _httpClient.SendAsync(request, ct);").ShouldBeEmpty();
    }

    [Fact]
    public void Fixture_Rule6Detector_CatchesSyntheticSignalRNotifierDependency()
    {
        SignalRSpecificTypeSymbols("public Host(SignalRCanvasNotifier notifier) { }").ShouldNotBeEmpty();
        SignalRSpecificTypeSymbols("private readonly IHubContext<GatewayHub> _hub;").ShouldNotBeEmpty();
        SignalRSpecificTypeSymbols("// SignalR clients reconnect on their own").ShouldBeEmpty();
        SignalRSpecificTypeSymbols("public Host(IConversationChangeNotifier notifier) { }").ShouldBeEmpty();
    }

    [Fact]
    public void Fixture_Rule7Detector_CatchesSyntheticUnwiredChannelExtension()
    {
        ReachesEventSinkContract("""<ProjectReference Include="..\..\gateway\BotNexus.Gateway.Contracts\x.csproj" />""").ShouldBeTrue();
        ReachesEventSinkContract("""<ProjectReference Include="..\..\gateway\BotNexus.Gateway.Channels\x.csproj" />""").ShouldBeTrue();
        ReachesEventSinkContract("""<ProjectReference Include="..\..\domain\BotNexus.Domain\x.csproj" />""").ShouldBeFalse();
    }

    /// <summary>
    /// The event sink contract from #2085 must still exist; rule 7 is meaningless without it.
    /// </summary>
    [Fact]
    public void EventSinkContract_FromIssue2085_StillExists()
    {
        var src = SourceRoot();
        File.Exists(Path.Combine(src, "gateway", "BotNexus.Gateway.Contracts", "Events", "IConversationEventSink.cs"))
            .ShouldBeTrue("#2085 IConversationEventSink is the generic seam rule 7 points channel extensions at");
    }

    // ---------------------------------------------------------------------------------------
    // Detectors (pure functions over text, so the fixtures above exercise the real logic)
    // ---------------------------------------------------------------------------------------

    private sealed record Violation(string Rule, string RelativePath, string Evidence);

    private static readonly Regex s_channelExtensionRef =
        new(@"BotNexus\.Extensions\.Channels\.", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static bool HasChannelExtensionReference(string projectXml)
        => s_channelExtensionRef.IsMatch(projectXml);

    private static readonly string[] s_channelKeys =
        ["signalr", "telegram", "servicebus", "service-bus", "agent365", "tui", "discord", "slack"];

    private static readonly Regex s_stringLiteral =
        new("\"((?:[^\"\\\\\\r\\n]|\\\\.)*)\"", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static IReadOnlyList<string> ChannelKeyLiterals(string source)
    {
        var code = StripComments(source);
        var hits = new List<string>();
        foreach (Match m in s_stringLiteral.Matches(code))
        {
            var value = m.Groups[1].Value;
            if (s_channelKeys.Contains(value, StringComparer.OrdinalIgnoreCase))
                hits.Add($"\"{value}\"");
        }
        return hits;
    }

    private static readonly Regex s_channelSpecificObserver = new(
        @"\b(signalR|signalr|telegram|serviceBus|servicebus|agent365|tui|discord|slack)[A-Za-z0-9]*(Observer|Observers|FanOut|Fanout|Bindings|Targets)\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static IReadOnlyList<string> ChannelSpecificObserverSymbols(string source)
        => s_channelSpecificObserver.Matches(StripComments(source)).Select(m => m.Value).Distinct().ToList();

    // NOTE: `GroupName` is deliberately NOT listed. It is the config-UI grouping attribute used by
    // AgentDescriptor/DateTimeInjectionConfig and has nothing to do with a SignalR group - including
    // it produced two false positives, and a noisy fence gets disabled.
    private static readonly Regex s_extensionLocalRecipient = new(
        @"\b(ConnectionId|GroupId|PendingReplyQueue|PendingReplyQueueName|HubConnectionId|SignalRGroup)\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static IReadOnlyList<string> ExtensionLocalRecipientSymbols(string source)
        => s_extensionLocalRecipient.Matches(StripComments(source)).Select(m => m.Value).Distinct().ToList();

    // SendStream*Async exist only on the channel adapter contracts, so their names alone are
    // unambiguous. Bare SendAsync is overloaded with HttpClient/WebSocket, so it is only counted
    // when it carries an OutboundMessage - the channel adapter's payload type.
    private static readonly Regex s_directAdapterSend = new(
        @"\.(SendStreamDeltaAsync|SendStreamEventAsync)\s*\(|\.SendAsync\s*\(\s*(new\s+OutboundMessage|remapped\b)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static IReadOnlyList<string> DirectAdapterSends(string source)
        => s_directAdapterSend.Matches(StripComments(source)).Select(m => m.Value.Trim()).Distinct().ToList();

    private static readonly Regex s_signalRSpecificType = new(
        @"\bSignalR[A-Za-z0-9]*(Notifier|Bridge|Adapter|Broadcaster|Publisher|Client|Hub)\b|\bIHubContext\s*<|\bGatewayHub\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static IReadOnlyList<string> SignalRSpecificTypeSymbols(string source)
        => s_signalRSpecificType.Matches(StripComments(source)).Select(m => m.Value.Trim()).Distinct().ToList();

    private static bool ReachesEventSinkContract(string projectXml)
        => projectXml.Contains("BotNexus.Gateway.Contracts", StringComparison.Ordinal)
        || projectXml.Contains("BotNexus.Gateway.Channels", StringComparison.Ordinal);

    // ---------------------------------------------------------------------------------------
    // Repo traversal
    // ---------------------------------------------------------------------------------------

    private static IReadOnlyList<Violation> FindRule1Violations()
    {
        var src = SourceRoot();
        var found = new List<Violation>();
        foreach (var tree in s_genericSourceTrees)
        {
            foreach (var proj in SafeFiles(Path.Combine(src, tree), "*.csproj"))
            {
                var text = File.ReadAllText(proj);
                if (HasChannelExtensionReference(text))
                    found.Add(new Violation("R1", Rel(src, proj), "references a BotNexus.Extensions.Channels.* assembly"));
            }
            foreach (var file in SafeFiles(Path.Combine(src, tree), "*.cs"))
            {
                var code = StripComments(File.ReadAllText(file));
                if (Regex.IsMatch(code, @"^\s*using\s+BotNexus\.Extensions\.Channels\.", RegexOptions.Multiline))
                    found.Add(new Violation("R1", Rel(src, file), "using of a concrete channel extension namespace"));
            }
        }
        return found;
    }

    private static IReadOnlyList<Violation> FindRule2Violations() =>
        ScanOrchestration("R2", ChannelKeyLiterals, "concrete channel key literal(s)");

    private static IReadOnlyList<Violation> FindRule3Violations() =>
        ScanOrchestration("R3", ChannelSpecificObserverSymbols, "channel-specific observer/fan-out symbol(s)");

    private static IReadOnlyList<Violation> FindRule5Violations() =>
        ScanOrchestration("R5", DirectAdapterSends, "direct IChannelAdapter send call(s)");

    private static IReadOnlyList<Violation> FindRule6Violations() =>
        ScanOrchestration("R6", SignalRSpecificTypeSymbols, "SignalR-specific notifier/bridge type reference(s)");

    private static IReadOnlyList<Violation> FindRule4Violations()
    {
        var src = SourceRoot();
        var found = new List<Violation>();
        foreach (var scope in s_conversationModelScopes)
        {
            foreach (var file in CsFiles(Path.Combine(src, scope)))
            {
                var hits = ExtensionLocalRecipientSymbols(File.ReadAllText(file));
                if (hits.Count > 0)
                    found.Add(new Violation("R4", Rel(src, file), "extension-local recipient concept(s): " + string.Join(", ", hits)));
            }
        }
        return found;
    }

    private static IReadOnlyList<Violation> FindRule7Violations()
    {
        var src = SourceRoot();
        var found = new List<Violation>();
        foreach (var proj in ChannelExtensionProjects())
        {
            if (!ReachesEventSinkContract(File.ReadAllText(proj)))
                found.Add(new Violation("R7", Rel(src, proj),
                    "channel extension cannot reach the generic IConversationEventSink contract (#2085)"));
        }
        return found;
    }

    private static IReadOnlyList<Violation> AllViolations() =>
    [
        .. FindRule1Violations(), .. FindRule2Violations(), .. FindRule3Violations(),
        .. FindRule4Violations(), .. FindRule5Violations(), .. FindRule6Violations(),
        .. FindRule7Violations(),
    ];

    private static IReadOnlyList<Violation> ScanOrchestration(
        string rule, Func<string, IReadOnlyList<string>> detector, string label)
    {
        var src = SourceRoot();
        var found = new List<Violation>();
        foreach (var project in s_orchestrationProjects)
        {
            foreach (var file in CsFiles(Path.Combine(src, project)))
            {
                var hits = detector(File.ReadAllText(file));
                if (hits.Count > 0)
                    found.Add(new Violation(rule, Rel(src, file), label + ": " + string.Join(", ", hits.Take(6))));
            }
        }
        return found;
    }

    /// <summary>Host-side channel extension projects (client/Blazor sub-projects are not channels).</summary>
    private static IReadOnlyList<string> ChannelExtensionProjects()
    {
        var root = Path.Combine(SourceRoot(), "extensions");
        return SafeFiles(root, "*.csproj")
            .Where(p => Path.GetFileNameWithoutExtension(p).StartsWith("BotNexus.Extensions.Channels.", StringComparison.Ordinal))
            .Where(p => !Path.GetFileNameWithoutExtension(p).Contains("BlazorClient", StringComparison.Ordinal))
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();
    }

    private static void AssertRule(string rule, IReadOnlyList<Violation> violations)
    {
        var baselined = s_baseline
            .Where(b => b.Rule == rule)
            .Select(b => b.RelativePath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var unexpected = violations
            .Where(v => !baselined.Contains(v.RelativePath))
            .Select(v => $"{v.RelativePath} - {v.Evidence}")
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();

        unexpected.ShouldBeEmpty(
            $"#2086 {rule}: generic gateway orchestration must not carry concrete channel knowledge. " +
            "If this is pre-existing code being migrated, add a specific per-file baseline entry linked " +
            "to its child issue; do NOT widen the rule.\nOffending files:\n  " +
            string.Join("\n  ", unexpected));
    }

    // ---------------------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------------------

    /// <summary>Removes // and /* */ comments so prose about SignalR is never a violation.</summary>
    internal static string StripComments(string source)
    {
        var sb = new StringBuilder(source.Length);
        var i = 0;
        while (i < source.Length)
        {
            var c = source[i];
            if (c == '"' && (i == 0 || source[i - 1] != '\\'))
            {
                var start = i++;
                while (i < source.Length && !(source[i] == '"' && source[i - 1] != '\\'))
                {
                    if (source[i] == '\n') break;
                    i++;
                }
                if (i < source.Length && source[i] == '"') i++;
                sb.Append(source, start, i - start);
                continue;
            }
            if (c == '/' && i + 1 < source.Length && source[i + 1] == '/')
            {
                while (i < source.Length && source[i] != '\n') i++;
                continue;
            }
            if (c == '/' && i + 1 < source.Length && source[i + 1] == '*')
            {
                i += 2;
                while (i + 1 < source.Length && !(source[i] == '*' && source[i + 1] == '/')) i++;
                i = Math.Min(source.Length, i + 2);
                continue;
            }
            sb.Append(c);
            i++;
        }
        return sb.ToString();
    }

    private static IEnumerable<string> CsFiles(string root) => SafeFiles(root, "*.cs");

    private static IEnumerable<string> SafeFiles(string root, string pattern)
    {
        if (!Directory.Exists(root)) return [];
        return Directory.EnumerateFiles(root, pattern, SearchOption.AllDirectories)
            .Where(p =>
                !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) &&
                !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase));
    }

    private static string Rel(string src, string full)
    {
        var f = Path.GetFullPath(full);
        var r = Path.GetFullPath(src).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return f.StartsWith(r, StringComparison.OrdinalIgnoreCase) ? f[r.Length..] : f;
    }

    private static string SourceRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "BotNexus.slnx")))
            current = current.Parent;
        current.ShouldNotBeNull("Could not locate repo root from " + AppContext.BaseDirectory);
        return Path.Combine(current!.FullName, "src");
    }
}
