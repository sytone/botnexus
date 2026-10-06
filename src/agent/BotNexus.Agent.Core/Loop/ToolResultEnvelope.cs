using System.Buffers;
using System.Text;
using System.Text.Json;

namespace BotNexus.Agent.Core.Loop;

/// <summary>Whether a tool-result envelope carries a value directly or refers to retained bytes.</summary>
public enum ToolResultDisposition
{
    /// <summary>The complete small value is present in the envelope.</summary>
    Inline,

    /// <summary>The complete value is retained by a result store and represented by a receipt.</summary>
    Stored
}

/// <summary>
/// The single typed tool-result transport shape. Small scalar or action values remain inline;
/// retained values carry a compact receipt and an optional bounded preview.
/// </summary>
public sealed record ToolResultEnvelope
{
    /// <summary>Creates an envelope and rejects ambiguous inline-plus-stored states.</summary>
    public ToolResultEnvelope(
        ToolResultDisposition disposition,
        ToolResultKind kind,
        JsonElement? inlineValue,
        ToolResultReceipt? receipt,
        string? preview)
    {
        if (disposition == ToolResultDisposition.Inline && (inlineValue is null || receipt is not null || preview is not null))
        {
            throw new ArgumentException("An inline result requires exactly one inline value.");
        }

        if (disposition == ToolResultDisposition.Stored && (inlineValue is not null || receipt is null))
        {
            throw new ArgumentException("A stored result requires exactly one receipt and no inline value.");
        }

        if (receipt is not null && receipt.Kind != kind)
        {
            throw new ArgumentException("Envelope kind must match the stored-result receipt.");
        }

        Disposition = disposition;
        Kind = kind;
        InlineValue = inlineValue?.Clone();
        Receipt = receipt;
        Preview = preview;
    }

    /// <summary>Whether this result is inline or stored.</summary>
    public ToolResultDisposition Disposition { get; }

    /// <summary>Logical value shape.</summary>
    public ToolResultKind Kind { get; }

    /// <summary>The complete inline value, when <see cref="Disposition"/> is inline.</summary>
    public JsonElement? InlineValue { get; }

    /// <summary>Identity and safe metadata, when <see cref="Disposition"/> is stored.</summary>
    public ToolResultReceipt? Receipt { get; }

    /// <summary>A bounded, non-authoritative preview of stored bytes.</summary>
    public string? Preview { get; }

    /// <summary>Creates an inline scalar envelope without changing the scalar's JSON shape.</summary>
    public static ToolResultEnvelope InlineScalar(JsonElement value) =>
        new(ToolResultDisposition.Inline, ToolResultKind.Scalar, value, null, null);

    /// <summary>Creates a receipt envelope with a kind-aware preview bounded in UTF-8 bytes.</summary>
    public static ToolResultEnvelope Stored(
        ToolResultReceipt receipt,
        ReadOnlySpan<byte> payload,
        int maxPreviewBytes = 1024)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        if (maxPreviewBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxPreviewBytes));
        }

        return new ToolResultEnvelope(
            ToolResultDisposition.Stored,
            receipt.Kind,
            null,
            receipt,
            ToolResultPreview.Create(receipt.Kind, receipt.MediaType, payload, maxPreviewBytes));
    }

    /// <summary>Serializes the model-visible inline value or compact receipt projection.</summary>
    public string ToModelProjection()
    {
        if (Disposition == ToolResultDisposition.Inline)
        {
            return InlineValue!.Value.GetRawText();
        }

        var receipt = Receipt!;
        return JsonSerializer.Serialize(new
        {
            disposition = "stored",
            receipt = new
            {
                result_id = receipt.ResultId.Value,
                revision = receipt.Revision,
                kind = receipt.Kind.ToString().ToLowerInvariant(),
                media_type = receipt.MediaType,
                schema = receipt.Schema,
                source_tool = receipt.SourceTool,
                source_call = receipt.SourceCallId,
                count = receipt.Count,
                size_bytes = receipt.SizeBytes,
                completeness = receipt.Completeness.ToString().ToLowerInvariant(),
                created_at = receipt.CreatedAt,
                expires_at = receipt.ExpiresAt,
                retention = receipt.Retention.ToString().ToLowerInvariant(),
                provenance = receipt.Provenance.ToString().ToLowerInvariant(),
                integrity_sha256 = receipt.IntegritySha256,
                terminal_status = receipt.TerminalStatus.ToString().ToLowerInvariant(),
                operations = receipt.SupportedOperations.Select(operation => operation.ToString().ToLowerInvariant())
            },
            preview = Preview
        });
    }
}

internal static class ToolResultPreview
{
    public static string? Create(
        ToolResultKind kind,
        string mediaType,
        ReadOnlySpan<byte> payload,
        int maxBytes)
    {
        if (maxBytes == 0)
        {
            return null;
        }

        if (kind == ToolResultKind.Blob || !IsTextual(mediaType))
        {
            return BoundUtf8($"[binary {payload.Length} bytes; preview omitted]", maxBytes);
        }

        var text = DecodeUtf8Prefix(payload, maxBytes);
        return kind switch
        {
            ToolResultKind.Object => NormalizeJson(text),
            ToolResultKind.Table => NormalizeJson(text),
            _ => text
        };
    }

    private static bool IsTextual(string mediaType) =>
        mediaType.StartsWith("text/", StringComparison.OrdinalIgnoreCase) ||
        mediaType.Contains("json", StringComparison.OrdinalIgnoreCase) ||
        mediaType.Contains("xml", StringComparison.OrdinalIgnoreCase);

    private static string DecodeUtf8Prefix(ReadOnlySpan<byte> payload, int maxBytes)
    {
        var candidate = payload[..Math.Min(payload.Length, maxBytes)];
        var length = RuneSafeLength(candidate);
        return Encoding.UTF8.GetString(candidate[..length]);
    }

    private static string NormalizeJson(string text)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
            return document.RootElement.GetRawText();
        }
        catch (JsonException)
        {
            // A bounded prefix is commonly incomplete JSON. Returning its rune-safe textual prefix
            // is more useful than discarding it and never fabricates structure.
            return text;
        }
    }

    private static string BoundUtf8(string value, int maxBytes)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        var candidate = bytes.AsSpan(0, Math.Min(bytes.Length, maxBytes));
        return Encoding.UTF8.GetString(candidate[..RuneSafeLength(candidate)]);
    }

    private static int RuneSafeLength(ReadOnlySpan<byte> span)
    {
        var consumed = 0;
        while (consumed < span.Length)
        {
            var status = Rune.DecodeFromUtf8(span[consumed..], out _, out var bytesConsumed);
            if (status != OperationStatus.Done)
            {
                break;
            }

            consumed += bytesConsumed;
        }

        return consumed;
    }
}
