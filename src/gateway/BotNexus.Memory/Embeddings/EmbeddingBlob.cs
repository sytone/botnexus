using System.Buffers.Binary;
using System.Text;

namespace BotNexus.Memory.Embeddings;

/// <summary>
/// Wire format for the <c>memories.embedding</c> BLOB: a self-describing envelope that
/// carries the vector together with the <see cref="EmbeddingIdentity"/> that produced it.
/// </summary>
/// <remarks>
/// The identity is stored inline rather than in a side table so a row can never become
/// separated from the identity of its vector — a stale or missing join would otherwise
/// silently permit cross-identity comparisons, which is the one failure mode this whole
/// feature must not have. <see cref="TryDecode"/> is deliberately total: any blob that is
/// foreign, truncated, or internally inconsistent is reported as undecodable rather than
/// throwing, because the caller's correct response is always the same — treat the row as
/// having no usable vector and fall back to lexical ranking.
/// </remarks>
public static class EmbeddingBlob
{
    // 'B','N','E','M' - lets us distinguish our envelope from any bytes an older build or
    // an external tool may have written into this nullable column.
    private static readonly byte[] Magic = [0x42, 0x4E, 0x45, 0x4D];
    private const byte FormatVersion = 1;
    private const int MaxTextFieldBytes = 512;

    /// <summary>
    /// Encodes a vector and its identity into the stored BLOB form.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The vector length disagrees with <see cref="EmbeddingIdentity.Dimensions"/>. This is a
    /// programming error, not bad data, so it throws rather than degrading.
    /// </exception>
    public static byte[] Encode(EmbeddingIdentity identity, ReadOnlySpan<float> vector)
    {
        ArgumentNullException.ThrowIfNull(identity);

        if (vector.Length != identity.Dimensions)
        {
            throw new ArgumentException(
                $"Vector length {vector.Length} does not match the declared dimension count {identity.Dimensions} for identity '{identity}'.",
                nameof(vector));
        }

        var modelIdBytes = Encoding.UTF8.GetBytes(identity.ModelId);
        var fingerprintBytes = Encoding.UTF8.GetBytes(identity.ModelFingerprint);

        if (modelIdBytes.Length > MaxTextFieldBytes || fingerprintBytes.Length > MaxTextFieldBytes)
            throw new ArgumentException("Model identity fields exceed the maximum encodable length.", nameof(identity));

        var size = Magic.Length + 1 + 4 + 4 + modelIdBytes.Length + 4 + fingerprintBytes.Length + (vector.Length * 4);
        var buffer = new byte[size];
        var offset = 0;

        Magic.CopyTo(buffer, offset);
        offset += Magic.Length;
        buffer[offset++] = FormatVersion;

        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(offset), identity.Dimensions);
        offset += 4;

        offset += WriteLengthPrefixed(buffer.AsSpan(offset), modelIdBytes);
        offset += WriteLengthPrefixed(buffer.AsSpan(offset), fingerprintBytes);

        foreach (var value in vector)
        {
            BinaryPrimitives.WriteSingleLittleEndian(buffer.AsSpan(offset), value);
            offset += 4;
        }

        return buffer;
    }

    /// <summary>
    /// Attempts to decode a stored BLOB. Returns <see langword="false"/> — never throws — for
    /// null, empty, foreign, truncated, over-long or internally inconsistent payloads so that
    /// unreadable rows degrade to lexical-only ranking instead of failing the search.
    /// </summary>
    public static bool TryDecode(byte[]? blob, out EmbeddingIdentity? identity, out float[]? vector)
    {
        identity = null;
        vector = null;

        if (blob is null || blob.Length < Magic.Length + 1 + 4 + 4 + 4)
            return false;

        var span = blob.AsSpan();
        for (var i = 0; i < Magic.Length; i++)
        {
            if (span[i] != Magic[i])
                return false;
        }

        var offset = Magic.Length;
        if (span[offset++] != FormatVersion)
            return false;

        var dimensions = BinaryPrimitives.ReadInt32LittleEndian(span[offset..]);
        offset += 4;
        if (dimensions <= 0)
            return false;

        if (!TryReadLengthPrefixed(span, ref offset, out var modelId) ||
            !TryReadLengthPrefixed(span, ref offset, out var fingerprint))
        {
            return false;
        }

        // The declared dimension count must account for exactly the remaining bytes; a payload
        // that is short (truncated) or long (corrupted / concatenated) is not trustworthy.
        var remaining = span.Length - offset;
        if (remaining != dimensions * 4)
            return false;

        var values = new float[dimensions];
        for (var i = 0; i < dimensions; i++)
        {
            values[i] = BinaryPrimitives.ReadSingleLittleEndian(span[offset..]);
            offset += 4;
        }

        identity = new EmbeddingIdentity(modelId, fingerprint, dimensions);
        vector = values;
        return true;
    }

    private static int WriteLengthPrefixed(Span<byte> destination, byte[] value)
    {
        BinaryPrimitives.WriteInt32LittleEndian(destination, value.Length);
        value.CopyTo(destination[4..]);
        return 4 + value.Length;
    }

    private static bool TryReadLengthPrefixed(ReadOnlySpan<byte> span, ref int offset, out string value)
    {
        value = string.Empty;
        if (span.Length - offset < 4)
            return false;

        var length = BinaryPrimitives.ReadInt32LittleEndian(span[offset..]);
        offset += 4;
        if (length < 0 || length > MaxTextFieldBytes || span.Length - offset < length)
            return false;

        value = Encoding.UTF8.GetString(span.Slice(offset, length));
        offset += length;
        return true;
    }
}
