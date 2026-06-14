using System.Reflection;
using BotNexus.Gateway.Sessions;
using Shouldly;

namespace BotNexus.Architecture.Tests;

/// <summary>
/// Architecture fitness function (#1386): every persistence-backed
/// <see cref="SessionStoreBase"/> implementation must <em>override</em>
/// <c>ListByConversationAsync</c> with an indexed/sidecar-scoped lookup rather than inherit
/// the base default.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="SessionStoreBase.ListByConversationAsync"/> is a deliberately naive default:
/// it calls <c>EnumerateSessionsAsync()</c> (loads <em>every</em> session, with full
/// per-session file/DB hydration) and filters in memory — O(total sessions). That is fine
/// for the in-memory backend (no I/O, the whole store is already in RAM) but is a real
/// asymptotic cliff for any backend with per-session I/O.
/// </para>
/// <para>
/// The hazard is a hidden Liskov violation: callers program against <c>ISessionStore</c>
/// assuming the lookup is cheap (it is on the SQLite backend, which overrides it with an
/// indexed query). Swapping in a backend that inherits the base default silently changes
/// the asymptotic complexity of every caller — invisible at compile time, and invisible to
/// the result-based parity tests (they assert the same sessions come back, not the cost).
/// </para>
/// <para>
/// This fence makes the override part of the contract: a new persistence backend cannot
/// regress into the slow default without either overriding the method or being explicitly
/// allow-listed (with a rationale) as a genuinely in-memory store.
/// </para>
/// </remarks>
public sealed class SessionStoreListByConversationOverrideArchitectureTests
{
    // Backends that legitimately hold the whole store in memory pay no I/O for the base
    // default, so inheriting it is acceptable. Add here ONLY after confirming the backend
    // has no per-session I/O cost for enumeration.
    private static readonly HashSet<string> AllowedToInheritBaseDefault = new(StringComparer.Ordinal)
    {
        nameof(InMemorySessionStore),
    };

    [Fact]
    public void EveryPersistenceBackedSessionStore_OverridesListByConversationAsync()
    {
        var baseType = typeof(SessionStoreBase);

        var concreteStores = baseType.Assembly
            .GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false } && baseType.IsAssignableFrom(t))
            .ToList();

        // Sanity: the assembly must actually contain the known stores, otherwise a future
        // refactor that moves them out of this assembly would make the fence silently vacuous.
        concreteStores.Select(t => t.Name).ShouldContain(nameof(FileSessionStore));
        concreteStores.Select(t => t.Name).ShouldContain(nameof(SqliteSessionStore));

        var offenders = new List<string>();
        foreach (var store in concreteStores)
        {
            if (AllowedToInheritBaseDefault.Contains(store.Name))
                continue;

            var method = store.GetMethod(
                nameof(SessionStoreBase.ListByConversationAsync),
                BindingFlags.Public | BindingFlags.Instance);

            // DeclaringType == baseType means the concrete store did NOT provide its own
            // override and is inheriting the O(total sessions) default.
            if (method is null || method.DeclaringType == baseType)
                offenders.Add(store.Name);
        }

        offenders.Sort(StringComparer.Ordinal);
        offenders.ShouldBeEmpty(
            "These persistence-backed ISessionStore implementations inherit the O(total " +
            "sessions) SessionStoreBase.ListByConversationAsync default instead of providing " +
            "an indexed/sidecar-scoped override. That is a hidden Liskov/perf hazard the " +
            "result-based parity tests cannot catch. Override ListByConversationAsync with a " +
            "lookup that does not enumerate the entire store (see SqliteSessionStore's indexed " +
            "query and FileSessionStore's sidecar scan), or — only if the backend is genuinely " +
            "in-memory with no per-session I/O — add it to AllowedToInheritBaseDefault with a " +
            "rationale.\nOffenders:\n  " + string.Join("\n  ", offenders));
    }
}
