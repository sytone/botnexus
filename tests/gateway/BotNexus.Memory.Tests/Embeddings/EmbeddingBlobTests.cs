using System.Text;
using BotNexus.Memory.Embeddings;

namespace BotNexus.Memory.Tests.Embeddings;

/// <summary>
/// Wire-format tests for <see cref="EmbeddingBlob"/>. The BLOB is the only place vector
/// identity travels with the vector, so a decode that silently succeeds on a foreign or
/// truncated payload would let us compare vectors across model identities.
/// </summary>
public sealed class EmbeddingBlobTests
{
    private static readonly EmbeddingIdentity Identity = new("nomic-embed-text-v2", "sha256:abc123", 4);

    [Fact]
    public void RoundTrip_PreservesIdentityAndVector()
    {
        float[] vector = [0.1f, -0.2f, 0.3f, 0.4f];

        var blob = EmbeddingBlob.Encode(Identity, vector);
        var decoded = EmbeddingBlob.TryDecode(blob, out var identity, out var values);

        Assert.True(decoded);
        Assert.Equal(Identity, identity);
        Assert.Equal(vector, values!.ToArray());
    }

    [Fact]
    public void Encode_RejectsDimensionMismatch()
    {
        float[] vector = [0.1f, 0.2f];

        var ex = Assert.Throws<ArgumentException>(() => EmbeddingBlob.Encode(Identity, vector));
        Assert.Contains("dimension", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryDecode_ReturnsFalse_ForNullOrEmpty()
    {
        Assert.False(EmbeddingBlob.TryDecode(null, out _, out _));
        Assert.False(EmbeddingBlob.TryDecode([], out _, out _));
    }

    [Fact]
    public void TryDecode_ReturnsFalse_ForForeignMagic()
    {
        var foreign = Encoding.UTF8.GetBytes("this is not an embedding blob at all");

        Assert.False(EmbeddingBlob.TryDecode(foreign, out _, out _));
    }

    [Fact]
    public void TryDecode_ReturnsFalse_ForTruncatedPayload()
    {
        var blob = EmbeddingBlob.Encode(Identity, [0.1f, 0.2f, 0.3f, 0.4f]);
        var truncated = blob[..(blob.Length - 5)];

        Assert.False(EmbeddingBlob.TryDecode(truncated, out _, out _));
    }

    [Fact]
    public void TryDecode_ReturnsFalse_WhenDeclaredDimensionsDisagreeWithPayload()
    {
        var blob = EmbeddingBlob.Encode(Identity, [0.1f, 0.2f, 0.3f, 0.4f]);
        // Append a stray float so the payload no longer matches the declared dimension count.
        var corrupted = blob.Concat(new byte[4]).ToArray();

        Assert.False(EmbeddingBlob.TryDecode(corrupted, out _, out _));
    }
}
