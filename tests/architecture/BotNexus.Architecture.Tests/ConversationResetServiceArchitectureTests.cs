using Shouldly;

namespace BotNexus.Architecture.Tests;

/// <summary>
/// Architecture fitness functions enforcing the Phase 3c invariant (#536):
/// the canonical "seal active session + flush memory + cancel ask-user" sequence
/// is owned by <c>DefaultConversationResetService</c>, and callers that need to
/// reset a conversation session must go through <c>IConversationResetService</c>
/// rather than invoking <c>ISessionEndMemoryFlusher.FlushAsync</c> directly.
/// </summary>
/// <remarks>
/// <para>
/// F-2c — the original defect this phase closes — was that the REST and SignalR
/// reset paths each rolled their own seal sequence and only one of them
/// remembered to call the memory flusher. Centralising the sequence in one
/// service only helps if new callers cannot quietly reach past it to invoke the
/// flusher themselves. This fence makes that drift a compile-time-discovered
/// architecture failure.
/// </para>
/// </remarks>
public sealed class ConversationResetServiceArchitectureTests
{
    /// <summary>
    /// No file in <c>src/gateway/</c> outside the allowlist may invoke
    /// <c>FlushAsync(</c> while also referencing <c>ISessionEndMemoryFlusher</c>.
    /// The combination is the strongest grep-style signal of a direct flusher
    /// invocation that bypasses the canonical reset service.
    /// </summary>
    [Fact]
    public void NoDirect_ISessionEndMemoryFlusher_FlushAsync_OutsideAllowlist()
    {
        var gatewayRoot = FindGatewaySourceRoot();
        var allowlist = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            // The canonical orchestrator — the only legitimate caller of
            // ISessionEndMemoryFlusher.FlushAsync in production code.
            "DefaultConversationResetService.cs",
            // The flusher implementation itself, where the method body lives.
            "SessionEndMemoryFlusher.cs",
            // The interface declaration — contains the FlushAsync( method signature,
            // not an invocation.
            "ISessionEndMemoryFlusher.cs",
        };

        var offenders = Directory
            .EnumerateFiles(gatewayRoot, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(path => !allowlist.Contains(Path.GetFileName(path)))
            .Where(path =>
            {
                var text = File.ReadAllText(path);
                return text.Contains("ISessionEndMemoryFlusher", StringComparison.Ordinal)
                    && text.Contains("FlushAsync(", StringComparison.Ordinal);
            })
            .Select(path => Path.GetRelativePath(gatewayRoot, path))
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToArray();

        offenders.ShouldBeEmpty(
            "Files outside the canonical reset-service allowlist reference both " +
            "ISessionEndMemoryFlusher and FlushAsync(, indicating a direct memory-flush " +
            "invocation that bypasses IConversationResetService. Route the call through " +
            "IConversationResetService.ResetActiveSessionAsync instead — it owns the full " +
            "canonical sequence (stop supervisor, flush memory, cancel ask-user, seal session, " +
            "clear ActiveSessionId).\n" +
            "Offenders:\n  " + string.Join("\n  ", offenders));
    }

    private static string FindGatewaySourceRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "BotNexus.slnx")))
        {
            current = current.Parent;
        }

        current.ShouldNotBeNull("Could not locate repo root from " + AppContext.BaseDirectory);
        var gatewayRoot = Path.Combine(current.FullName, "src", "gateway");
        Directory.Exists(gatewayRoot).ShouldBeTrue("Expected src/gateway under " + current.FullName);
        return gatewayRoot;
    }
}
