namespace BotNexus.Extensions.GitHub;

/// <summary>
/// A fully resolved GitHub acting identity: the App installation whose token will be minted for a
/// given agent (#2733).
/// </summary>
/// <remarks>
/// <para><b>Why this exists as a distinct type.</b> Before #2733 the extension had exactly one
/// installation identity, bound from the flat <c>GitHub</c> section, so "which identity is acting"
/// was not a value in the program at all — it was ambient process state, which is precisely how
/// <c>gh auth switch</c> became the ergonomic remedy. Making the identity an explicit, resolved,
/// immutable value means a caller can only *select* an identity from configuration; there is no
/// mutable global for a tool call to change.</para>
/// <para>The PEM itself is NOT held here — only its path. The private key is read at mint time by
/// <see cref="HttpGitHubInstallationTokenSource"/> and never travels through a type an agent-facing
/// code path can observe.</para>
/// </remarks>
/// <param name="Name">Configuration name of the identity profile (the key under <c>GitHub:identities</c>).</param>
/// <param name="AppId">GitHub App id used as the JWT issuer.</param>
/// <param name="InstallationId">Installation whose scoped token is minted.</param>
/// <param name="PrivateKeyPath">Filesystem path to the App PEM private key.</param>
public sealed record GitHubActingIdentity(
    string Name,
    string AppId,
    string InstallationId,
    string PrivateKeyPath);
