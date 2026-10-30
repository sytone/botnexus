using System.Security.Cryptography;
using System.Text.Json;

namespace BotNexus.Cli.Commands;

// A source checkout and an existing DLL are not evidence of a completed update. This receipt is
// invalidated before any build and published only after build, deployment and restart succeed.
// It is local reconciliation evidence, not an attestation of the revision loaded by a process.
internal static class UpdateDeploymentIdentity
{
    internal static string ReceiptPath(string gatewayBinary) => gatewayBinary + ".update-identity.json";

    internal static void Invalidate(string gatewayBinary)
    {
        try
        {
            File.Delete(ReceiptPath(gatewayBinary));
        }
        catch (DirectoryNotFoundException)
        {
            // No output directory means no prior receipt exists to invalidate.
        }
    }

    internal static async Task<bool> MatchesAsync(string gatewayBinary, string sourceCommit, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsCommit(sourceCommit))
            return false;

        try
        {
            await using var receiptStream = File.OpenRead(ReceiptPath(gatewayBinary));
            var receipt = await JsonSerializer.DeserializeAsync<Receipt>(receiptStream, cancellationToken: cancellationToken);
            if (receipt is null || receipt.Schema != 1 ||
                !string.Equals(receipt.SourceCommit, sourceCommit, StringComparison.OrdinalIgnoreCase))
                return false;

            var hash = await HashAsync(gatewayBinary, cancellationToken);
            return string.Equals(receipt.BinarySha256, hash, StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return false;
        }
    }

    internal static async Task RecordAsync(string gatewayBinary, string sourceCommit, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsCommit(sourceCommit))
            return;

        await using var stream = File.OpenRead(gatewayBinary);
        await RecordAsync(gatewayBinary, sourceCommit, stream, cancellationToken);
    }

    // Keep the actual asynchronous hashing/publication path exercisable at its I/O boundary.
    // The caller owns the stream; production supplies the on-disk gateway assembly above.
    internal static async Task RecordAsync(string gatewayBinary, string sourceCommit, Stream binary, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsCommit(sourceCommit))
            return;

        var receipt = new Receipt(1, sourceCommit, await HashAsync(binary, cancellationToken));
        var path = ReceiptPath(gatewayBinary);
        var temporary = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(receipt), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    private static bool IsCommit(string value)
        => value.Length == 40 && value.All(char.IsAsciiHexDigit);

    private static async Task<string> HashAsync(string binary, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(binary);
        return await HashAsync(stream, cancellationToken);
    }

    private static async Task<string> HashAsync(Stream binary, CancellationToken cancellationToken)
        => Convert.ToHexString(await SHA256.HashDataAsync(binary, cancellationToken));

    private sealed record Receipt(int Schema, string SourceCommit, string BinarySha256);
}
