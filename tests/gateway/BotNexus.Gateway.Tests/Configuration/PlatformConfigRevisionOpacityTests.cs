using System.IO.Abstractions;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BotNexus.Gateway.Api.Controllers;
using BotNexus.Gateway.Configuration;
using Microsoft.AspNetCore.Mvc;

namespace BotNexus.Gateway.Tests.Configuration;

/// <summary>
/// Issue #3469: the compare-and-swap revision token must not be derivable from the secret material
/// it travels alongside.
/// </summary>
/// <remarks>
/// <para>
/// <c>GET /api/config/snapshot</c> redacts provider API keys and channel bot tokens out of the
/// document it returns, but historically returned a <c>revision</c> that was a bare SHA-256 of the
/// <em>unredacted</em> bytes on disk. Every caller authorised for the redacted settings view
/// therefore received an offline confirmation oracle: the redacted document already discloses the
/// full structure, every non-secret value and the exact set of configured providers, so a candidate
/// for the one remaining unknown could be confirmed or refuted in a single hash - no rate limit, no
/// audit trail.
/// </para>
/// <para>
/// The tests below are written as the attack, not as an implementation detail check.
/// <see cref="Snapshot_RevisionDoesNotConfirmAGuessedSecret"/> literally performs the oracle attack
/// against the endpoint and asserts it fails; it passes against <c>main</c> only if the oracle is
/// still open, so it is the clause-6 regression test. The remainder pin the properties the fix must
/// not break: the same non-derivable value on the <c>409 Conflict</c> path, and compare-and-swap
/// still detecting a real conflict, including a conflict caused by a change to a secret alone.
/// </para>
/// </remarks>
public sealed class PlatformConfigRevisionOpacityTests : IDisposable
{
    private readonly string _rootPath;
    private readonly IFileSystem _fileSystem = new FileSystem();

    public PlatformConfigRevisionOpacityTests()
    {
        _rootPath = Path.Combine(
            Path.GetTempPath(), "botnexus-config-revision-opacity", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_rootPath);
    }

    /// <summary>
    /// The clause-1 regression test, and it fails against <c>main</c>.
    /// </summary>
    /// <remarks>
    /// The attacker is modelled exactly as the threat describes: they hold the redacted snapshot
    /// (so they know the whole document except the secret), they hold the revision the endpoint
    /// handed them, and they have a correct guess at the API key. They reconstruct the candidate
    /// document and apply the published revision algorithm. If the result matches the token they
    /// were given, the guess is confirmed - the oracle is open. This test asserts it does not match.
    /// </remarks>
    [Fact]
    public async Task Snapshot_RevisionDoesNotConfirmAGuessedSecret()
    {
        const string realApiKey = "sk-live-3469-super-secret";
        var configPath = WriteConfig(
            "{\"providers\":{\"openai\":{\"type\":\"openai\",\"apiKey\":\"" + realApiKey + "\"}}}");
        var writer = new PlatformConfigWriter(configPath, _fileSystem);

        var response = await new ConfigController().GetSnapshot(writer, CancellationToken.None);
        var snapshot = ((ObjectResult)response.Result!).Value.ShouldBeOfType<ConfigSnapshotResponse>();

        // Precondition: the endpoint really did withhold the secret. Without this the test could
        // pass for the wrong reason (nothing to confirm in the first place).
        snapshot.Config["providers"]!["openai"]!["apiKey"]!.GetValue<string>()
            .ShouldBe(ConfigSecretMerge.Placeholder, "the snapshot must redact the API key");

        // The attacker's candidate: the redacted document they were handed, with their guess
        // substituted back in. Read from disk so the candidate is byte-identical to the real
        // document when - and only when - the guess is correct.
        var candidate = JsonNode.Parse(await File.ReadAllTextAsync(configPath))!.AsObject();
        candidate["providers"]!["openai"]!["apiKey"] = realApiKey;

        var guessConfirmed = string.Equals(
            LegacyContentDigest(candidate), snapshot.Revision, StringComparison.Ordinal);

        guessConfirmed.ShouldBeFalse(
            "a correct guess at the API key must not be confirmable by re-deriving the revision "
            + "token; the token crosses the redaction boundary, so it must not be a function the "
            + "holder of a candidate secret can evaluate (#3469)");
    }

    /// <summary>
    /// Clause 2: the token on the <c>409 Conflict</c> body is the same non-secret-derived value.
    /// A fix that only patched the snapshot path would leave the oracle fully intact here, since
    /// any caller can provoke a conflict on demand by quoting a revision that is certainly stale.
    /// </summary>
    [Fact]
    public async Task Conflict_RevisionIsAlsoNotDerivableFromTheDocument()
    {
        const string realApiKey = "sk-live-conflict-path";
        var configPath = WriteConfig(
            "{\"providers\":{\"openai\":{\"type\":\"openai\",\"apiKey\":\"" + realApiKey + "\"}}}");
        var writer = new PlatformConfigWriter(configPath, _fileSystem);
        var controller = new ConfigController();

        var conflict = await controller.PatchConfig(
            new ConfigPatchRequest(
                [new ConfigPatchOperationDto("gateway.port", JsonValue.Create(8080))],
                ExpectedRevision: "definitely-not-the-current-revision"),
            writer,
            CancellationToken.None);

        var body = ((ConflictObjectResult)conflict.Result!).Value.ShouldBeOfType<ConfigPatchResponse>();
        body.Success.ShouldBeFalse();
        body.Revision.ShouldNotBeNullOrWhiteSpace();

        var onDisk = JsonNode.Parse(await File.ReadAllTextAsync(configPath))!.AsObject();
        body.Revision.ShouldNotBe(
            LegacyContentDigest(onDisk),
            "the revision reported on the conflict path is the same token the snapshot path returns, "
            + "so it must be equally non-derivable (#3469 clause 2)");

        // ...and it is genuinely the same token, so a client can use it to retry.
        var (_, current) = await writer.ReadWithRevisionAsync();
        body.Revision.ShouldBe(current);
    }

    /// <summary>
    /// Clause 3: making the token opaque must not cost compare-and-swap its teeth. A patch quoting
    /// the current revision commits; a patch quoting the revision it was read at before someone
    /// else committed is rejected with <c>409</c>.
    /// </summary>
    [Fact]
    public async Task CompareAndSwap_StillCommitsOnCurrentAndRejectsOnStale()
    {
        var configPath = WriteConfig("""{"gateway":{"port":8080}}""");
        var writer = new PlatformConfigWriter(configPath, _fileSystem);
        var controller = new ConfigController();

        var (_, first) = await writer.ReadWithRevisionAsync();

        var accepted = await controller.PatchConfig(
            new ConfigPatchRequest(
                [new ConfigPatchOperationDto("gateway.port", JsonValue.Create(9090))],
                ExpectedRevision: first),
            writer,
            CancellationToken.None);

        var acceptedBody = ((OkObjectResult)accepted.Result!).Value.ShouldBeOfType<ConfigPatchResponse>();
        acceptedBody.Success.ShouldBeTrue();

        // The now-stale first revision must no longer be accepted.
        var rejected = await controller.PatchConfig(
            new ConfigPatchRequest(
                [new ConfigPatchOperationDto("gateway.port", JsonValue.Create(7070))],
                ExpectedRevision: first),
            writer,
            CancellationToken.None);

        rejected.Result.ShouldBeOfType<ConflictObjectResult>();

        var persisted = JsonNode.Parse(await File.ReadAllTextAsync(configPath))!.AsObject();
        persisted["gateway"]!["port"]!.GetValue<int>().ShouldBe(9090);
    }

    /// <summary>
    /// The property that ruled out the rejected remedy. Hashing a <em>redacted</em>
    /// canonicalisation would make two documents differing only in a secret share one revision, so
    /// a concurrent secret-only write would become undetectable - a lost-update defect traded for
    /// the confidentiality one. This pins that a secret-only change still moves the token, and so
    /// still trips the guard.
    /// </summary>
    [Fact]
    public async Task SecretOnlyChange_StillInvalidatesTheRevision()
    {
        var configPath = WriteConfig("""
            {"providers":{"openai":{"type":"openai","apiKey":"key-before"}}}
            """);
        var writer = new PlatformConfigWriter(configPath, _fileSystem);

        var (_, before) = await writer.ReadWithRevisionAsync();

        // Another writer rotates the credential and changes nothing else.
        await writer.MutateAsync(
            root => root["providers"]!["openai"]!["apiKey"] = "key-after",
            "rotate-secret");

        var (_, after) = await writer.ReadWithRevisionAsync();
        after.ShouldNotBe(before, "a secret-only write must still be detectable as a conflict");

        await Should.ThrowAsync<PlatformConfigConcurrencyException>(
            () => writer.ApplyPatchAsync(
                [new ConfigPatchOperation("gateway.port", JsonValue.Create(8080))],
                "stale-after-rotation",
                before));
    }

    /// <summary>
    /// The revision algorithm as it stood before #3469: a bare SHA-256 over the canonical
    /// serialization of the document. Reproduced here as the <em>attacker's</em> tool, not as a
    /// production helper - the tests above assert the endpoint's token can no longer be reproduced
    /// by anyone able to evaluate it.
    /// </summary>
    private static string LegacyContentDigest(JsonObject root)
    {
        var canonical = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private string WriteConfig(string json)
    {
        var path = Path.Combine(_rootPath, Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, json);
        return path;
    }

    public void Dispose()
    {
        if (Directory.Exists(_rootPath))
            Directory.Delete(_rootPath, recursive: true);
    }
}
