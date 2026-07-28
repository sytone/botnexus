using System.Globalization;
using System.Security.Cryptography;

namespace BotNexus.Cli.Commands;

/// <summary>
/// Outcome of a verified payload download. A caller may only execute, extract, or otherwise
/// trust the bytes when <see cref="Succeeded"/> is <see langword="true"/>; on any failure the
/// downloader guarantees no file was left at the destination.
/// </summary>
/// <param name="Succeeded">Whether the payload was fetched completely and matched the expected digest.</param>
/// <param name="FilePath">Absolute path of the verified payload, or <see langword="null"/> when verification failed.</param>
/// <param name="FailureReason">Human-readable reason the download was rejected, or <see langword="null"/> on success.</param>
internal readonly record struct VerifiedDownloadResult(
    bool Succeeded,
    string? FilePath,
    string? FailureReason)
{
    internal static VerifiedDownloadResult Success(string filePath) => new(true, filePath, null);

    internal static VerifiedDownloadResult Failure(string reason) => new(false, null, reason);
}

/// <summary>
/// Fail-closed downloader for content that BotNexus would subsequently execute, install, or
/// unpack (update artifacts, remediation scripts, bootstrap payloads).
///
/// This exists because a download-then-execute step is only as trustworthy as its weakest
/// failure mode: a proxy error page, a connection dropped mid-body, or a substituted artifact
/// all look like "a file appeared on disk" to naive code. Every BotNexus install/update path
/// that fetches executable content must route through this helper so the payload is proven
/// complete and byte-identical to a known SHA-256 digest *before* anything runs it.
/// See <c>docs/development/downloaded-payload-verification.md</c> for the governing rule.
/// </summary>
internal static class VerifiedPayloadDownloader
{
    private const int Sha256HexLength = 64;

    /// <summary>
    /// Fetches <paramref name="uri"/> to <paramref name="destinationPath"/> and returns success
    /// only when the response was 2xx, the body was fully received (no truncation against an
    /// advertised Content-Length), non-empty, and its SHA-256 equals
    /// <paramref name="expectedSha256"/> (case-insensitive hex).
    ///
    /// The payload is staged in a sibling temporary file and only moved into place after
    /// verification, so a rejected download never leaves executable content behind for another
    /// process - or a careless retry - to pick up.
    /// </summary>
    internal static async Task<VerifiedDownloadResult> DownloadAndVerifyAsync(
        HttpClient httpClient,
        Uri uri,
        string expectedSha256,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(uri);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);

        if (!IsWellFormedSha256(expectedSha256))
        {
            return VerifiedDownloadResult.Failure(
                "Expected SHA-256 digest is missing or malformed; refusing to download executable content without a valid 64-character hex digest.");
        }

        var directory = Path.GetDirectoryName(Path.GetFullPath(destinationPath));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // Stage beside the destination so the verified move is same-volume and atomic-ish.
        var stagingPath = destinationPath + ".partial-" + Guid.NewGuid().ToString("N");

        try
        {
            using var response = await httpClient
                .GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return VerifiedDownloadResult.Failure(
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "Download of {0} failed with HTTP status {1} ({2}); failing closed without writing any payload.",
                        uri,
                        (int)response.StatusCode,
                        response.StatusCode));
            }

            var advertisedLength = response.Content.Headers.ContentLength;

            long written;
            string actualSha256;

            await using (var source = await response.Content
                .ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false))
            await using (var staging = new FileStream(
                stagingPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None))
            using (var hasher = SHA256.Create())
            {
                var buffer = new byte[81920];
                written = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    hasher.TransformBlock(buffer, 0, read, null, 0);
                    await staging.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    written += read;
                }

                hasher.TransformFinalBlock([], 0, 0);
                actualSha256 = Convert.ToHexString(hasher.Hash ?? []).ToLowerInvariant();
            }

            if (written == 0)
            {
                return VerifiedDownloadResult.Failure(
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "Download of {0} produced an empty payload; refusing to treat a zero-byte body as executable content.",
                        uri));
            }

            if (advertisedLength is { } expectedLength && written != expectedLength)
            {
                return VerifiedDownloadResult.Failure(
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "Download of {0} was truncated: received {1} byte(s) but the server advertised {2}.",
                        uri,
                        written,
                        expectedLength));
            }

            if (!CryptographicOperations.FixedTimeEquals(
                    Convert.FromHexString(actualSha256),
                    Convert.FromHexString(expectedSha256.Trim())))
            {
                return VerifiedDownloadResult.Failure(
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "Checksum verification failed for {0}: expected SHA-256 {1} but the downloaded payload hashed to {2}.",
                        uri,
                        expectedSha256.Trim().ToLowerInvariant(),
                        actualSha256));
            }

            File.Move(stagingPath, destinationPath, overwrite: true);
            return VerifiedDownloadResult.Success(destinationPath);
        }
        catch (HttpRequestException ex)
        {
            return VerifiedDownloadResult.Failure(
                string.Format(
                    CultureInfo.InvariantCulture,
                    "Download of {0} failed: {1}",
                    uri,
                    ex.Message));
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            return VerifiedDownloadResult.Failure(
                string.Format(
                    CultureInfo.InvariantCulture,
                    "Download of {0} timed out: {1}",
                    uri,
                    ex.Message));
        }
        catch (IOException ex)
        {
            return VerifiedDownloadResult.Failure(
                string.Format(
                    CultureInfo.InvariantCulture,
                    "Download of {0} could not be written to disk: {1}",
                    uri,
                    ex.Message));
        }
        finally
        {
            // Any staged bytes that were never promoted are hostile-by-default: delete them.
            TryDelete(stagingPath);
        }
    }

    /// <summary>
    /// Computes the lowercase hex SHA-256 of a file already on disk, for callers that need to
    /// record or compare a digest outside the download path.
    /// </summary>
    internal static async Task<string> ComputeSha256Async(
        string filePath,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        await using var stream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static bool IsWellFormedSha256(string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return false;
        }

        var trimmed = candidate.Trim();
        if (trimmed.Length != Sha256HexLength)
        {
            return false;
        }

        foreach (var c in trimmed)
        {
            var isHex = c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F';
            if (!isHex)
            {
                return false;
            }
        }

        return true;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // A locked staging file is undesirable but must not mask the real failure reason.
        }
        catch (UnauthorizedAccessException)
        {
            // Same rationale as above.
        }
    }
}
