using System.IO.Abstractions;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BotNexus.Gateway.Configuration;

namespace BotNexus.Gateway.Tests.Configuration;

/// <summary>
/// Issue #3469: the compare-and-swap revision token must not be a confirmation oracle for the
/// secrets the snapshot response deliberately redacts.
/// </summary>
/// <remarks>
/// <para><b>What the defect was.</b> <c>GET /api/config/snapshot</c> redacts every secret out of the
/// returned document but returns a <c>revision</c> that was a bare SHA-256 digest of the
/// <em>unredacted</em> on-disk JSON. The redacted document still reveals the full structure, every
/// non-secret value and the exact set of configured providers, so a caller holding it needs to guess
/// only the remaining secret: hash the candidate document, compare to the token, and a guess becomes
/// a verified fact - offline, unrated-limited and unaudited.</para>
/// <para><b>What these tests pin.</b> The oracle tests below reconstruct exactly what such a caller
/// can build (the real document, because the test knows the true secret - strictly easier than the
/// real attack) and assert the digest does <em>not</em> reproduce the token. They fail against the
/// pre-fix algorithm by construction. The two concurrency tests are the counterweight: they assert
/// the token still discriminates document state, so a fix cannot pass by making the token constant
/// or secret-blind.</para>
/// </remarks>
public sealed class ConfigRevisionTokenOracleTests : IDisposable
{
    private readonly string _rootPath;
    private readonly IFileSystem _fileSystem = new FileSystem();

    private const string SecretValue = "ghp_super_secret_token_value_3469";

    public ConfigRevisionTokenOracleTests()
    {
        _rootPath = Path.Combine(
            Path.GetTempPath(), "botnexus-config-revision-oracle", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_rootPath);
    }

    private string WriteConfig(string secret)
    {
        var path = Path.Combine(_rootPath, "config.json");
        var json = $$"""
        {
          "providers": {
            "github-copilot": {
              "type": "github-copilot",
              "apiKey": "{{secret}}"
            }
          },
          "gateway": {
            "locations": {}
          }
        }
        """;
        File.WriteAllText(path, json);
        return path;
    }

    /// <summary>
    /// The candidate digest an attacker computes: the pre-fix algorithm, reproduced verbatim.
    /// </summary>
    private static string LegacyContentDigest(JsonObject root)
    {
        var canonical = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    /// <summary>
    /// Clause 1. The revision handed out with the redacted snapshot must not be reproducible from
    /// the document's contents, so possession of it confirms nothing about the secret.
    /// </summary>
    [Fact]
    public async Task Snapshot_RevisionDoesNotConfirmAGuessedSecret()
    {
        var configPath = WriteConfig(SecretValue);
        var writer = new PlatformConfigWriter(configPath, _fileSystem);

        var (_, revision) = await writer.ReadWithRevisionAsync();

        // The attacker's offline check, with the true secret already in hand.
        var candidate = JsonNode.Parse(await File.ReadAllTextAsync(configPath))!.AsObject();
        candidate["providers"]!["github-copilot"]!["apiKey"]!.GetValue<string>()
            .ShouldBe(SecretValue, "the fixture must actually contain the secret being tested");

        LegacyContentDigest(candidate).ShouldNotBe(
            revision,
            "the snapshot revision must not be a digest of the unredacted document (#3469)");
    }

    /// <summary>
    /// Clause 2. The 409 path leaks the same token. A conflict is provokable on demand by quoting
    /// any wrong revision, so a fix that only patches the snapshot path leaves the oracle intact.
    /// </summary>
    [Fact]
    public async Task Conflict_RevisionIsAlsoNotDerivableFromTheDocument()
    {
        var configPath = WriteConfig(SecretValue);
        var writer = new PlatformConfigWriter(configPath, _fileSystem);

        var exception = await Should.ThrowAsync<PlatformConfigConcurrencyException>(
            () => writer.ApplyPatchAsync(
                [new ConfigPatchOperation("gateway.logLevel", JsonValue.Create("Debug"))],
                "oracle-conflict",
                "DEFINITELY-NOT-THE-CURRENT-REVISION"));

        var candidate = JsonNode.Parse(await File.ReadAllTextAsync(configPath))!.AsObject();

        exception.ActualRevision.ShouldNotBeNull();
        LegacyContentDigest(candidate).ShouldNotBe(
            exception.ActualRevision,
            "the conflict revision must be the same non-secret-derived value as the snapshot (#3469)");
    }

    /// <summary>
    /// Clause 3. Counterweight: the token must still be a real compare-and-swap guard. A fix that
    /// returned a constant would satisfy the oracle tests and destroy concurrency detection.
    /// </summary>
    [Fact]
    public async Task CompareAndSwap_StillCommitsOnCurrentAndRejectsOnStale()
    {
        var configPath = WriteConfig(SecretValue);
        var writer = new PlatformConfigWriter(configPath, _fileSystem);

        var (_, current) = await writer.ReadWithRevisionAsync();

        var ok = await writer.ApplyPatchAsync(
            [new ConfigPatchOperation("gateway.logLevel", JsonValue.Create("Debug"))],
            "fresh",
            current);
        ok.Success.ShouldBeTrue();

        // The revision just consumed is now stale.
        await Should.ThrowAsync<PlatformConfigConcurrencyException>(
            () => writer.ApplyPatchAsync(
                [new ConfigPatchOperation("gateway.logLevel", JsonValue.Create("Debug"))],
                "stale",
                current));
    }

    /// <summary>
    /// Clause 1, second half: "changed-but-unpredictable". A secret-only edit must still move the
    /// token, otherwise a concurrent secret-only write is undetectable and the confidentiality fix
    /// would have bought a lost-update bug. This is precisely what the redacted-canonicalisation
    /// remedy cannot do, since <c>ConfigSecretMerge.Redact</c> collapses every secret to "***".
    /// </summary>
    [Fact]
    public async Task SecretOnlyChange_StillInvalidatesTheRevision()
    {
        var configPath = WriteConfig(SecretValue);
        var writer = new PlatformConfigWriter(configPath, _fileSystem);

        var (_, before) = await writer.ReadWithRevisionAsync();

        // Only the secret changes; structure and every non-secret value are identical.
        var original = await File.ReadAllTextAsync(configPath);
        await File.WriteAllTextAsync(
            configPath,
            original.Replace(SecretValue, "ghp_a_completely_different_secret", StringComparison.Ordinal));

        var (_, after) = await writer.ReadWithRevisionAsync();

        after.ShouldNotBe(before, "a secret-only change must still invalidate the CAS token (#3469)");
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_rootPath))
                Directory.Delete(_rootPath, recursive: true);
        }
        catch (IOException)
        {
            // Temp cleanup is best-effort.
        }
    }
}
