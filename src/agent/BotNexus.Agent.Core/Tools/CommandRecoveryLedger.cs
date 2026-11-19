using System.Collections.Concurrent;

namespace BotNexus.Agent.Core.Tools;

/// <summary>Retains unresolved command launches for a trusted owner across tool/handle recreation.</summary>
/// <remarks>This is host-lifetime evidence, not durable OS process discovery. Owner strings come from
/// trusted tool construction, never model arguments. Unconfirmed launches are intentionally retained;
/// later successful calls cannot erase them. A gateway restart still requires separate reconciliation.</remarks>
public static class CommandRecoveryLedger
{
    private static readonly object Gate = new();
    private static readonly ConcurrentDictionary<string, int> Pending = new(StringComparer.Ordinal);
    private const int MaximumOwners = 10000;

    /// <summary>Records before launch. The returned token may be confirmed exactly once only when
    /// this invocation provably did not start or its tracked lifecycle completed.</summary>
    public static CommandRecoveryTicket Begin(string owner)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        lock (Gate)
        {
            if (!Pending.ContainsKey(owner) && Pending.Count >= MaximumOwners)
                throw new InvalidOperationException("Command recovery owner capacity exceeded.");
            Pending[owner] = checked(Pending.GetValueOrDefault(owner) + 1);
        }
        return new CommandRecoveryTicket(owner);
    }

    /// <summary>Safe means no unresolved launch recorded in this process, not proof about arbitrary children.</summary>
    public static string GetStatus(string owner)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        return Pending.GetValueOrDefault(owner) == 0 ? "safe" : "commands-unresolved";
    }

    /// <summary>One invocation's completion token. Dropping it preserves uncertainty, never clears it.</summary>
    public sealed class CommandRecoveryTicket
    {
        private readonly string _owner;
        private int _confirmed;
        internal CommandRecoveryTicket(string owner) => _owner = owner;

        /// <summary>Confirms this exact tracked invocation once. Caller owns the termination evidence.</summary>
        public void ConfirmCompleted()
        {
            if (Interlocked.Exchange(ref _confirmed, 1) != 0) return;
            lock (Gate)
            {
                var remaining = Pending.GetValueOrDefault(_owner) - 1;
                if (remaining <= 0) Pending.TryRemove(_owner, out _);
                else Pending[_owner] = remaining;
            }
        }
    }
}
