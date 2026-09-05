using System.Text.RegularExpressions;
using BotNexus.Extensions.Channels.SignalR;

namespace BotNexus.Gateway.Tests;

/// <summary>
/// Pins the Blazor client's hand-written <c>On&lt;T&gt;(...)</c> registration block against the
/// generated hub event inventory (#3430).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this is a fence and not a generator (#3430 AC1).</b> <c>HubEvents.All</c> is generated
/// from <see cref="IGatewayHubClient"/> (#3318), but the registration block in
/// <c>GatewayHubConnection.cs</c> cannot be generated the same way, and the measurement that
/// decided it is recorded here so nobody re-litigates it from intuition:
/// </para>
/// <list type="number">
///   <item><description>
///     <b>The dependency edge runs the wrong way, deliberately.</b> The registration block lives in
///     <c>BlazorClient.Core</c>, whose only permitted project reference is
///     <c>BotNexus.Domain.Wire</c> - enforced by <c>WasmPayloadDependencyArchitectureTests</c>
///     because every assembly reachable from a WASM entry point is downloaded by the browser
///     (#2329). <see cref="IGatewayHubClient"/> lives in an assembly that transitively drags
///     <c>BotNexus.Domain</c> and Vogen. A source generator needs the annotated interface's symbol
///     in the CONSUMING compilation, so generating the block would mean breaching that fence and
///     regressing first-load time for every user in order to delete 24 lines.
///   </description></item>
///   <item><description>
///     <b>The payload types are not the interface's payload types.</b> The client registers its own
///     mirror records from <c>Services/HubContracts.cs</c>. Most pointedly,
///     <c>IGatewayHubClient.ContentDelta</c> declares <c>object</c> while the client registers
///     <c>AgentStreamEvent</c>; emitting the interface's type verbatim would change what the portal
///     deserialises. The emission is therefore not a true projection of the interface, which is
///     exactly the condition #2770 AC4 uses to rule generation out.
///   </description></item>
/// </list>
/// <para>
/// What remains genuinely projectable is the SET OF NAMES, and that is what these assertions pin.
/// They compare the event-name literals actually present in the registration block against the
/// generated inventory, so a member added to <see cref="IGatewayHubClient"/> without a matching
/// registration fails <b>naming the missing event</b> rather than reporting a count mismatch
/// (#3430 AC2). No production code changes, so the hub contract and portal behaviour are unchanged
/// by construction (#3430 AC3, AC4).
/// </para>
/// </remarks>
public class HubRegistrationExhaustivenessTests
{
    /// <summary>
    /// Matches the event-name literal of a SignalR client-handler registration, for any arity of
    /// <c>On&lt;...&gt;</c>. The generic argument list is skipped deliberately: the client's payload
    /// types are its own mirror records and are NOT expected to equal the interface's, so only the
    /// wire name is comparable across the two declarations.
    /// </summary>
    private const string RegistrationPattern = """\.On<[^>]*>\(\s*"(?<name>[A-Za-z0-9_]+)"\s*,""";

    /// <summary>
    /// Repository-relative path of the hand-written registration block this fence guards.
    /// </summary>
    private const string RegistrationFileRelativePath =
        "src/extensions/BotNexus.Extensions.Channels.SignalR.BlazorClient.Core/Services/GatewayHubConnection.cs";

    /// <summary>
    /// Extracts the registered hub event names from C# source text.
    /// </summary>
    /// <param name="source">The contents of the file carrying the registration block.</param>
    /// <returns>Each registered event name, in source order, without duplicates removed.</returns>
    public static IReadOnlyList<string> RegisteredEventNames(string source) =>
        Regex.Matches(source, RegistrationPattern)
            .Select(match => match.Groups["name"].Value)
            .ToArray();

    private static string RegistrationSource()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "Directory.Packages.props")))
        {
            current = current.Parent;
        }

        var root = current?.FullName
            ?? throw new DirectoryNotFoundException(
                $"Could not locate the repository root from {AppContext.BaseDirectory}.");

        var path = Path.Combine(root, RegistrationFileRelativePath.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(
            File.Exists(path),
            $"The Blazor hub registration block was not found at {path}. If GatewayHubConnection.cs "
            + "moved or was renamed, update RegistrationFileRelativePath - do not let this fence "
            + "silently stop guarding a live contract (#3430).");

        return File.ReadAllText(path);
    }

    [Fact]
    public void Registration_CoversEveryDeclaredHubEvent()
    {
        // The load-bearing assertion (#3430 AC2). Adding a method to IGatewayHubClient without
        // adding the matching On<T> registration fails HERE, naming the event that is missing.
        var missing = HubEvents.All
            .Except(RegisteredEventNames(RegistrationSource()))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            missing.Length == 0,
            $"IGatewayHubClient declares hub events the Blazor client never registers: {string.Join(", ", missing)}. "
            + "A handler that is never registered means the portal silently ignores that event - visible "
            + "for events the UI renders, completely quiet for diagnostic ones. Add the missing "
            + $"_connection.On<...>(\"Name\", ...) line to {RegistrationFileRelativePath} (#3430).");
    }

    [Fact]
    public void Registration_ContainsNoEventTheContractDoesNotDeclare()
    {
        // The other direction is silent rather than failing: a stale registration subscribes the
        // portal to an event the server can never send, and nothing ever reports it.
        var extra = RegisteredEventNames(RegistrationSource())
            .Except(HubEvents.All)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            extra.Length == 0,
            $"The Blazor client registers handlers for events IGatewayHubClient does not declare: {string.Join(", ", extra)}. "
            + "Either the event was removed from the contract and the registration was left behind, or "
            + "the name is misspelled - both leave a handler that can never fire (#3430).");
    }

    [Fact]
    public void Registration_IsNotEmptyAndRegistersEachEventExactlyOnce()
    {
        // Guards the degenerate pass: two empty sets satisfy both Except checks above. Also catches
        // a duplicated registration, which in SignalR means the handler runs twice per event.
        var registered = RegisteredEventNames(RegistrationSource());

        Assert.NotEmpty(registered);

        var duplicates = registered
            .GroupBy(name => name, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => $"{group.Key} x{group.Count()}")
            .OrderBy(entry => entry, StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            duplicates.Length == 0,
            $"The registration block registers the same event more than once: {string.Join(", ", duplicates)}. "
            + "SignalR invokes every registered handler, so a duplicate raises the client event twice (#3430).");

        Assert.Equal(HubEvents.All.Length, registered.Count);
    }

    [Fact]
    public void Fence_IsNotVacuous_ParserFindsRegistrationsAndRejectsNonRegistrations()
    {
        // The whole fence rests on the parser. If it silently matched nothing, every Except check
        // above would pass vacuously, so its behaviour is pinned on both sides against literal
        // source shapes copied from the real file.
        const string singleArg = """_connection.On<AgentStreamEvent>("RunStarted", e => OnRunStarted?.Invoke(e));""";
        const string multiArg = """_connection.On<string, string, object?>("CanvasStateChanged", (c, k, v) => OnCanvasStateChanged?.Invoke(c, k, v));""";

        Assert.Equal(["RunStarted"], RegisteredEventNames(singleArg));
        Assert.Equal(["CanvasStateChanged"], RegisteredEventNames(multiArg));

        // A server-bound invocation names a HUB METHOD, not a client event. Matching one would let
        // "SubscribeAll" masquerade as a registered event and mask a genuinely missing handler.
        const string invocation = """await _connection!.InvokeAsync<SubscribeAllResult>("SubscribeAll");""";
        Assert.Empty(RegisteredEventNames(invocation));
    }

    [Fact]
    public void Fence_IsNotVacuous_DetectsAnEventDroppedFromTheRegistrationBlock()
    {
        // The mutation this fence exists to catch, applied to the REAL source: delete one
        // registration and the exhaustiveness check must fail NAMING that event (#3430 AC2). This
        // proves the failure mode without needing a mutation run against production code.
        var source = RegistrationSource();
        var mutated = Regex.Replace(
            source,
            """\.On<[^>]*>\(\s*"TodoUpdated"\s*,""",
            ".On<string, string, string?>(\"__removed__\",");

        Assert.NotEqual(source, mutated);

        var missing = HubEvents.All.Except(RegisteredEventNames(mutated)).ToArray();
        Assert.Contains("TodoUpdated", missing);
    }
}
