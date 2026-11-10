using System.Text.RegularExpressions;
using Shouldly;

namespace BotNexus.Architecture.Tests;

/// <summary>
/// Architecture fitness function for the credential-disclosure guarantee behind
/// <c>Secret</c> and <c>WebhookSecret</c>.
///
/// <para><b>Why a fence and not just review.</b> Both types are safe by construction: their
/// <c>ToString</c> returns a redacted marker and their record member printing is overridden, so
/// interpolation, structured-log placeholders and exception messages cannot spill the plaintext.
/// Exactly one method defeats that on purpose - <c>Reveal()</c> - and it is a named method rather
/// than a property precisely so the set of places that unwrap a credential is enumerable. This
/// pins that set. A new unwrapping site is not forbidden; it just cannot arrive unnoticed.</para>
///
/// <para><b>And the protections themselves.</b> The allow-list is worthless if the guarantee it
/// protects quietly regresses, so the fence also asserts that each type keeps its redacted
/// <c>ToString</c> and its <c>PrintMembers</c> override. Deleting either would be a one-line
/// change that no test would otherwise notice, and every interpolation in the codebase would
/// start leaking at once.</para>
///
/// <para>Source-text based, like <see cref="SecretRedactionFenceArchitectureTests"/> and
/// <see cref="SecretFilePermissionFenceArchitectureTests"/>: "this call unwraps a credential" is
/// not reliably observable by reflection, and a reflection scan cannot tell a live call from dead
/// code.</para>
/// </summary>
public sealed class SecretUnwrapFenceArchitectureTests : ArchitectureTest
{
    private const string SecretSource = "src/domain/BotNexus.Domain/Security/Secret.cs";
    private const string WebhookSecretSource = "src/domain/BotNexus.Domain/Security/WebhookSecret.cs";

    /// <summary>
    /// Files permitted to unwrap a credential. Each one was reviewed for what it does with the
    /// plaintext: hands it to a transport and keeps it out of logs and error messages.
    ///
    /// Adding an entry is the point at which someone should ask whether the plaintext is really
    /// needed here, and whether it can reach a log, an exception message, or an agent's context.
    /// </summary>
    private static readonly string[] AllowedUnwrapSites =
    {
        // Puts the webhook secret in the Matrix API's Authorization header.
        "src/extensions/BotNexus.Extensions.Channels.Matrix/MatrixHttpClient.cs",

        // Passes the webhook secret to Telegram as secret_token when registering the webhook.
        "src/extensions/BotNexus.Extensions.Channels.Telegram/TelegramChannelAdapter.cs",

        // Expands a value only when a configuration consumer explicitly requests it.
        "src/gateway/BotNexus.Gateway/Security/ConfigurationSecretReferenceResolver.cs",
    };

    private static readonly Regex RevealCall = new(@"\.Reveal\s*\(\s*\)", RegexOptions.Compiled);

    [Fact]
    public void OnlyRegisteredSites_UnwrapACredential()
    {
        var offenders = ProductionSourceFiles()
            .Where(file => RevealCall.IsMatch(File.ReadAllText(file)))
            .Select(RepositoryRelativePath)
            .Where(relative => !AllowedUnwrapSites.Contains(relative, StringComparer.OrdinalIgnoreCase))
            .OrderBy(relative => relative, StringComparer.Ordinal)
            .ToList();

        offenders.ShouldBeEmpty(
            "These files unwrap a credential but are not registered in AllowedUnwrapSites. "
            + "Reveal() is the one call that defeats redaction, so every site is listed and reviewed. "
            + "If the plaintext is genuinely needed, add the file to the list - and check first that it "
            + "cannot reach a log, an exception message, or an agent's context.");
    }

    /// <summary>
    /// The allow-list must not outlive the call sites it names, or it silently stops being a
    /// record of anything and starts hiding a site someone deleted and later restored.
    /// </summary>
    [Fact]
    public void EveryRegisteredSite_StillUnwrapsACredential()
    {
        var stale = AllowedUnwrapSites
            .Where(relative => !File.Exists(AbsolutePath(relative))
                               || !RevealCall.IsMatch(File.ReadAllText(AbsolutePath(relative))))
            .OrderBy(relative => relative, StringComparer.Ordinal)
            .ToList();

        stale.ShouldBeEmpty("These entries in AllowedUnwrapSites no longer unwrap a credential. Remove them.");
    }

    [Theory]
    [InlineData(SecretSource, "Secret(redacted)")]
    [InlineData(WebhookSecretSource, "WebhookSecret(redacted)")]
    public void EachCredentialType_KeepsItsRedactedToString(string relativePath, string marker)
    {
        var source = File.ReadAllText(AbsolutePath(relativePath));

        source.ShouldContain($"RedactedMarker = \"{marker}\"");
        source.ShouldContain("public override string ToString() => RedactedMarker;");
    }

    [Theory]
    [InlineData(SecretSource)]
    [InlineData(WebhookSecretSource)]
    public void EachCredentialType_SuppressesRecordMemberPrinting(string relativePath)
    {
        var source = File.ReadAllText(AbsolutePath(relativePath));

        source.ShouldContain(
            "private bool PrintMembers(StringBuilder builder)",
            customMessage: "Without this override the compiler-generated record printing walks the backing "
                         + "field, and the plaintext comes out of every ToString-adjacent path.");
    }

    /// <summary>
    /// A property would be picked up implicitly by serialisers and structured logging, which is
    /// the entire reason unwrapping is a method.
    /// </summary>
    [Theory]
    [InlineData(SecretSource)]
    [InlineData(WebhookSecretSource)]
    public void Unwrapping_IsAMethodNotAProperty(string relativePath)
    {
        var source = File.ReadAllText(AbsolutePath(relativePath));

        source.ShouldContain("public string Reveal()");
        source.ShouldNotContain("public string Reveal {");
    }

    private string AbsolutePath(string repositoryRelativePath)
        => Path.Combine(Repository.Root, repositoryRelativePath.Replace('/', Path.DirectorySeparatorChar));

    private string RepositoryRelativePath(string absolutePath)
        => Path.GetRelativePath(Repository.Root, absolutePath).Replace('\\', '/');

    private IEnumerable<string> ProductionSourceFiles()
        => Directory.EnumerateFiles(Repository.SourceRoot, "*.cs", SearchOption.AllDirectories)
            .Where(file =>
            {
                var relative = RepositoryRelativePath(file);
                return !relative.Contains("/obj/", StringComparison.Ordinal)
                       && !relative.Contains("/bin/", StringComparison.Ordinal);
            });
}
