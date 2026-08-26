using System.Text.RegularExpressions;
using Shouldly;

namespace BotNexus.Architecture.Tests;

/// <summary>
/// Build-failing fitness functions pinning the portal's SINGLE load path and the SCOPED
/// <c>ConversationChanged</c> fan-out (#2541).
/// </summary>
/// <remarks>
/// <para>
/// Both rules here are structural on purpose, because neither property is reachable by a
/// behavioural assertion at the unit level. AC1 is about which of two code paths WRITES the store:
/// a test that observes the store after a load sees the correct roster either way, since the
/// duplicate writer wrote the SAME rows from a slightly different instant. AC2/AC3's addressing
/// choice is likewise invisible end-to-end whenever the only connected client happens to observe
/// every agent -- which is the normal single-tab dev case, and precisely why the defect survived.
/// </para>
/// <list type="number">
///   <item>
///     <b>Rule 1 - one load path.</b> No client code may write the <c>SubscribeAll</c> result's
///     session payload into the client store. Portal session/conversation LOAD is REST-owned
///     (Jon's 2026-07-29 decision); the hub verb is retained only for its group-joining side
///     effect. Re-adding the write restores two unordered writers into one store.
///   </item>
///   <item>
///     <b>Rule 2 - scoped conversation notifications.</b>
///     <c>SignalRConversationChangeNotifier</c> may not address <c>Clients.All</c>. An unscoped
///     broadcast makes every connected client re-fetch its conversation list on every other
///     client's activity on an unrelated agent.
///   </item>
/// </list>
/// </remarks>
public sealed class PortalLoadPathArchitectureTests : ArchitectureTest
{
    /// <summary>
    /// Rule 1. Pins that no client file pairs a <c>SubscribeAll</c> result with a
    /// <c>RegisterSession</c> call — the exact shape of the removed duplicate writer.
    /// </summary>
    [Fact]
    public void SubscribeAllPayload_IsNeverWrittenIntoTheClientStore()
    {
        var files = ClientCoreFiles();
        files.Count.ShouldBeGreaterThan(10, "the client Core service scan found suspiciously few files; the fence would be vacuous");

        // Any file that mentions SubscribeAll is a candidate; it violates the rule only if it also
        // registers sessions from that result. Matching the two tokens within one statement-ish
        // window keeps the rule specific to the duplicate-writer shape rather than banning
        // RegisterSession outright (the REST roster walk legitimately calls it).
        var violations = new List<string>();
        foreach (var file in files)
        {
            var text = StripComments(File.ReadAllText(file));
            foreach (Match match in Regex.Matches(text, @"SubscribeAllAsync\(\)", RegexOptions.None))
            {
                var window = text.Substring(match.Index, Math.Min(400, text.Length - match.Index));
                if (window.Contains("RegisterSession", StringComparison.Ordinal))
                    violations.Add(Path.GetFileName(file));
            }
        }

        violations.ShouldBeEmpty(
            "The SubscribeAll session payload must not be written into the client store (#2541 AC1). "
            + "Portal session state is loaded over REST only; writing the hub payload as well creates a "
            + "second, unordered writer into the same store. Offending file(s): "
            + string.Join(", ", violations.Distinct()));
    }

    /// <summary>
    /// Rule 1 non-vacuity: the scanned set must actually contain the file the rule is about, so a
    /// path typo can never turn this fence green by finding nothing to check.
    /// </summary>
    [Fact]
    public void LoadPathFence_ScansThePortalLoadService()
    {
        ClientCoreFiles()
            .Select(Path.GetFileName)
            .ShouldContain("PortalLoadService.cs");
    }

    /// <summary>
    /// Rule 2. The conversation-change notifier must address a group, never every client.
    /// </summary>
    [Fact]
    public void ConversationChangeNotifier_DoesNotBroadcastToAllClients()
    {
        var file = Path.Combine(
            Repository.Root,
            "src",
            "extensions",
            "BotNexus.Extensions.Channels.SignalR",
            "SignalRConversationChangeNotifier.cs");

        File.Exists(file).ShouldBeTrue($"Expected the conversation change notifier at {file}.");

        var text = StripComments(File.ReadAllText(file));

        text.ShouldNotContain(
            "Clients.All",
            Case.Sensitive,
            "ConversationChanged must be scoped to the affected agent's group, not broadcast to every "
            + "connected client (#2541 AC2/AC3). An unscoped broadcast makes every client re-fetch its "
            + "conversation list on unrelated activity.");

        text.ShouldContain(
            "GetAgentGroup",
            Case.Sensitive,
            "The notifier must address the per-agent group so the scope is derived from the change itself.");
    }

    /// <summary>
    /// Removes comments before scanning so a rule can never fire on prose that merely DESCRIBES the
    /// banned shape — both files under these rules carry explanatory comments naming
    /// <c>Clients.All</c> and <c>RegisterSession</c> as the thing not to do.
    /// </summary>
    private static string StripComments(string source)
    {
        var withoutBlock = Regex.Replace(source, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);
        return Regex.Replace(withoutBlock, @"//[^\r\n]*", string.Empty);
    }

    private List<string> ClientCoreFiles()
    {
        var dir = Path.Combine(
            Repository.Root,
            "src",
            "extensions",
            "BotNexus.Extensions.Channels.SignalR.BlazorClient.Core",
            "Services");

        Directory.Exists(dir).ShouldBeTrue($"Expected the client Core services directory at {dir}.");
        return [.. Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories)];
    }

}
