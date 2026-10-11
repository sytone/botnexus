using System.Buffers.Binary;
using System.Security.Cryptography;
using BotNexus.Agent.Core.Types;
using BotNexus.Gateway.Abstractions.Agents;

namespace BotNexus.Gateway.Isolation;

/// <summary>Payload-free identity projection. Invocation IDs, arguments and metadata never participate.</summary>
internal static class AgentActivityEvidence
{
    internal static AgentActivityObservation Project(ToolExecutionEndEvent completed)
    {
        // Only the known read result represents observed source evidence. Write/edit/exec and
        // extension success receipts can remain identical while the underlying work progresses.
        // Their success is authoritative, but its progress is unknown: never deduplicate it.
        if (!completed.IsError && !string.Equals(completed.ToolName.Trim(), "read", StringComparison.OrdinalIgnoreCase))
            return new(AgentActivityKind.ToolProgressUnknown, 0, 0);

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendNormalized(hash, completed.ToolName, foldCase: true);
        // A separator outside UTF-16 code units prevents tool/result concatenation ambiguity.
        hash.AppendData([0xFF]);
        // Single text blocks use the shared extractor without allocating. Multiple blocks are
        // streamed with the extractor's newline semantics, avoiding an unbounded joined copy.
        if (completed.Result.Content.All(block => block.Type == AgentToolContentType.Text))
        {
            if (completed.Result.Content.Count <= 1)
                AppendNormalized(hash, AgentToolResultText.Extract(completed.Result), foldCase: false);
            else
                AppendNormalizedBlocks(hash, completed.Result.Content);
        }
        else
        {
            // Non-text evidence must not collapse to empty/unchanged text: image-only research
            // and mixed results can progress without changing any textual block. Hash every
            // block with its kind and boundary, retaining no payload beyond this callback.
            foreach (var block in completed.Result.Content)
            {
                hash.AppendData([0xFE]);
                AppendNormalized(hash, block.Type.ToString(), foldCase: false);
                hash.AppendData([0xFF]);
                AppendNormalized(hash, block.Value, foldCase: false);
            }
        }
        Span<byte> digest = stackalloc byte[32];
        hash.GetHashAndReset(digest);
        return new(completed.IsError ? AgentActivityKind.ToolFailed : AgentActivityKind.ToolSucceeded,
            BinaryPrimitives.ReadUInt64BigEndian(digest), BinaryPrimitives.ReadUInt64BigEndian(digest[8..]));
    }

    private static void AppendNormalized(IncrementalHash hash, string? text, bool foldCase)
    {
        var started = false;
        var whitespace = false;
        AppendText(hash, text, foldCase, ref started, ref whitespace);
    }

    private static void AppendNormalizedBlocks(IncrementalHash hash, IReadOnlyList<AgentToolContent> blocks)
    {
        var started = false;
        var whitespace = false;
        foreach (var block in blocks)
        {
            if (block.Type != AgentToolContentType.Text) continue;
            if (started) whitespace = true;
            AppendText(hash, block.Value, false, ref started, ref whitespace);
        }
    }

    private static void AppendText(IncrementalHash hash, string? text, bool foldCase,
        ref bool started, ref bool whitespace)
    {
        if (text is null) return;
        Span<byte> buffer = stackalloc byte[1024];
        var used = 0;
        foreach (var character in text)
        {
            if (char.IsWhiteSpace(character))
            {
                whitespace = started;
                continue;
            }
            if (used > buffer.Length - 4)
            {
                hash.AppendData(buffer[..used]);
                used = 0;
            }
            if (whitespace)
            {
                BinaryPrimitives.WriteUInt16BigEndian(buffer[used..], ' ');
                used += 2;
                whitespace = false;
            }
            BinaryPrimitives.WriteUInt16BigEndian(buffer[used..], foldCase ? char.ToLowerInvariant(character) : character);
            used += 2;
            started = true;
        }
        if (used > 0) hash.AppendData(buffer[..used]);
    }
}
