using System.Text.RegularExpressions;
using Shouldly;

namespace BotNexus.Architecture.Tests;

/// <summary>
/// Architecture fitness function for the provider error-body redaction seam (#2881).
///
/// <para>
/// <b>The defect this fences.</b> A non-2xx provider response body was interpolated verbatim into an
/// exception message, and <c>Agent.cs</c> copies <c>ex.Message</c> into the assistant message's
/// <c>ErrorMessage</c> and into session state. That message is therefore <b>persisted</b> and
/// <b>rendered to the user</b>. Providers routinely echo the rejected <c>Authorization</c> header or
/// API key back in a 401/403 body, so the most credential-rich response produced the most detailed
/// leak. Redaction existed and was wired for the <i>logging</i> path (<c>ProviderLoggingHandler</c>)
/// but not the <i>exception</i> path.
/// </para>
///
/// <para>
/// <b>Why a fence and not just unit tests.</b> Behavioural tests pin
/// <c>ProviderHttpErrorHelper</c> and <c>ProviderAuthenticationException.BuildMessage</c>, which is
/// necessary but not sufficient: the failure mode that produced #2881 is <i>a new provider call site
/// that never reaches the redacted helper at all</i>. There were five such call sites and nothing
/// structural stopped a sixth. This fence asserts the property that unit tests structurally cannot -
/// that no source file in <c>src/agent</c> interpolates a raw error body into an exception outside
/// the one redacted choke point.
/// </para>
///
/// <para>
/// It is a source-text scan with zero runtime dependency, matching the house style of
/// <see cref="SecretRedactionFenceArchitectureTests"/> and
/// <see cref="ExtensionManagedDependencyClosureArchitectureTests"/>: a reflection scan cannot tell
/// that a string flows into an exception constructor, and offenders are named in the failure
/// message. Explicit vacuity guards and positive pins keep the detectors honest in both directions.
/// </para>
/// </summary>
public sealed class ProviderErrorRedactionFenceArchitectureTests
{
    private static string RepoRoot => FindRepoRoot();

    private static string AgentRoot => Path.Combine(RepoRoot, "src", "agent");

    private const string ChokePoint =
        "src/agent/BotNexus.Agent.Providers.Core/ProviderHttpErrorHelper.cs";

    private const string AuthException =
        "src/agent/BotNexus.Agent.Providers.Core/ProviderAuthenticationException.cs";

    /// <summary>
    /// The provider entry points named in #2881 that must forward a redactor into the choke point.
    /// Each either calls <c>ThrowForFailedResponse</c> directly or supplies the stream engine's
    /// <c>ThrowForError</c> / <c>SecretRedactor</c> transport-profile members.
    /// </summary>
    private static readonly string[] CallSiteFiles =
    {
        "src/agent/BotNexus.Agent.Providers.Anthropic/AnthropicProvider.cs",
        "src/agent/BotNexus.Agent.Providers.Copilot/Messages/CopilotMessagesProvider.cs",
        "src/agent/BotNexus.Agent.Providers.Copilot/Responses/CopilotResponsesProvider.cs",
        "src/agent/BotNexus.Agent.Providers.Copilot/Completions/CopilotCompletionsProvider.cs",
        "src/agent/BotNexus.Agent.Providers.OpenAI/OpenAICompletionsProvider.cs",
        "src/agent/BotNexus.Agent.Providers.OpenAI/OpenAIResponsesProvider.cs",
        "src/agent/BotNexus.Agent.Providers.Core/Streaming/CompletionsStreamEngine.cs",
        "src/agent/BotNexus.Agent.Providers.Core/Streaming/ResponsesStreamEngine.cs",
    };

    /// <summary>Detects a reference to the redaction seam interface.</summary>
    private static readonly Regex SecretRedactorReference =
        new(@"\bISecretRedactor\b", RegexOptions.Compiled);

    /// <summary>
    /// Detects the redaction call itself, i.e. <c>Redact(</c> on the choke point. Matches both the
    /// interface call and the helper's shared <c>Redact(errorBody, secretRedactor)</c> wrapper.
    /// </summary>
    private static readonly Regex RedactCall =
        new(@"\bRedact\s*\(", RegexOptions.Compiled);

    /// <summary>
    /// Detects a raw error body interpolated directly into a thrown exception - the exact #2881
    /// shape, e.g. <c>throw new HttpRequestException($"HTTP {status}: {errorBody}")</c>. Deliberately
    /// scoped to <c>throw new ...</c> statements so ordinary logging or telemetry that mentions an
    /// error body is not swept up; the leak this fences is the one that reaches persisted state
    /// through <c>ex.Message</c>.
    /// </summary>
    private static readonly Regex RawErrorBodyThrow =
        new(@"throw\s+new\s+\w+[^;]*\{\s*(errorBody|providerError|responseBody|rawBody)\s*\}",
            RegexOptions.Compiled | RegexOptions.Singleline);

    [Fact]
    public void ChokePointAndCallSiteFiles_Exist()
    {
        foreach (var rel in CallSiteFiles.Append(ChokePoint).Append(AuthException))
        {
            var path = ResolvePath(rel);
            File.Exists(path).ShouldBeTrue(
                $"Expected provider error-redaction source not found: {path}. If a provider was " +
                "renamed or removed, update this fence rather than deleting it - the seam it " +
                "guards (#2881) still applies to whatever replaced it.");
        }
    }

    /// <summary>
    /// AC1/AC2: the choke point takes a redactor and applies it, and the auth message builder does
    /// the same for its <c>Provider response:</c> suffix.
    /// </summary>
    [Fact]
    public void ChokePoint_AcceptsAndAppliesASecretRedactor()
    {
        var helper = File.ReadAllText(ResolvePath(ChokePoint));

        SecretRedactorReference.IsMatch(helper).ShouldBeTrue(
            "ProviderHttpErrorHelper must accept an ISecretRedactor. It interpolates an untrusted " +
            "provider error body into an exception message that Agent.cs persists as the " +
            "session-visible ErrorMessage. See #2881.\nFile: " + ResolvePath(ChokePoint));

        RedactCall.IsMatch(helper).ShouldBeTrue(
            "ProviderHttpErrorHelper references ISecretRedactor but never calls Redact(...). The " +
            "error body must be scrubbed BEFORE any string interpolation. See #2881.\nFile: " +
            ResolvePath(ChokePoint));

        var authSource = File.ReadAllText(ResolvePath(AuthException));

        SecretRedactorReference.IsMatch(authSource).ShouldBeTrue(
            "ProviderAuthenticationException.BuildMessage appends the provider's 401/403 body to a " +
            "user-facing message - the single most likely place for a provider to echo the " +
            "credential it just rejected - so it must accept an ISecretRedactor. See #2881.\nFile: " +
            ResolvePath(AuthException));

        RedactCall.IsMatch(authSource).ShouldBeTrue(
            "ProviderAuthenticationException.BuildMessage must route the error body through the " +
            "redactor before building the 'Provider response:' suffix. See #2881.\nFile: " +
            ResolvePath(AuthException));
    }

    /// <summary>
    /// AC3: every provider entry point identified in #2881 threads a redactor through, rather than
    /// relying on the helper's null default and silently leaking.
    /// </summary>
    [Fact]
    public void EveryProviderCallSite_ThreadsARedactorThrough()
    {
        var offenders = new List<string>();

        foreach (var rel in CallSiteFiles)
        {
            var source = File.ReadAllText(ResolvePath(rel));
            if (!SecretRedactorReference.IsMatch(source))
            {
                offenders.Add(rel);
            }
        }

        offenders.ShouldBeEmpty(
            "The following provider entry point(s) do not reference ISecretRedactor, so they call " +
            "the error helper with its null default and leak any credential the provider echoes in " +
            "an error body into persisted session state. Thread the redactor through (constructor " +
            "parameter -> transport profile -> ProviderHttpErrorHelper). See #2881.\nOffenders: " +
            string.Join(", ", offenders));
    }

    /// <summary>
    /// AC5: no file under <c>src/agent</c> may interpolate a raw error body straight into a thrown
    /// exception. The only sanctioned formatting site is the choke point, which redacts first.
    /// </summary>
    [Fact]
    public void NoAgentSource_InterpolatesARawErrorBodyIntoAThrownException()
    {
        var offenders = new List<string>();

        foreach (var file in Directory.GetFiles(AgentRoot, "*.cs", SearchOption.AllDirectories))
        {
            var rel = ToRepoRelative(file);

            // The choke point is the ONE sanctioned interpolation site: it reassigns errorBody to
            // the redacted value before any throw, which is the whole point of the design.
            if (string.Equals(rel, ChokePoint, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (RawErrorBodyThrow.IsMatch(File.ReadAllText(file)))
            {
                offenders.Add(rel);
            }
        }

        offenders.ShouldBeEmpty(
            "The following file(s) interpolate a raw provider error body directly into a thrown " +
            "exception. That message is persisted as the session-visible ErrorMessage and rendered " +
            "to the user, so a credential echoed back by the provider leaks into the transcript. " +
            "Route the failure through ProviderHttpErrorHelper.ThrowForFailedResponse with a " +
            "redactor instead of formatting the body here. See #2881.\nOffenders: " +
            string.Join(", ", offenders));
    }

    /// <summary>
    /// AC6: <c>git grep "ISecretRedactor" -- src/agent</c> returns non-zero hits. Asserted directly
    /// on the tree so the clause is verified rather than taken on trust.
    /// </summary>
    [Fact]
    public void AgentTree_ReferencesTheRedactionSeam()
    {
        var hits = Directory
            .GetFiles(AgentRoot, "*.cs", SearchOption.AllDirectories)
            .Count(f => SecretRedactorReference.IsMatch(File.ReadAllText(f)));

        hits.ShouldBeGreaterThan(0,
            "No file under src/agent references ISecretRedactor. Before #2881 this count was zero " +
            "and that was the structural reason provider error bodies leaked: the redaction seam " +
            "was simply absent from the provider layer.");
    }

    [Fact]
    public void Fence_IsNotVacuous_DetectsRawInterpolationAndMissingRedactor()
    {
        // Synthetic regression: the exact pre-#2881 shape.
        const string leaking = """
            public static void ThrowForFailedResponse(HttpResponseMessage response, string errorBody)
            {
                throw new HttpRequestException($"HTTP {statusCode}: {errorBody}");
            }
            """;

        RawErrorBodyThrow.IsMatch(leaking).ShouldBeTrue(
            "Vacuity guard: the pre-fix raw-interpolation shape MUST be detected. If this fails the " +
            "detector is too tight and the AC5 scan passes vacuously.");

        SecretRedactorReference.IsMatch(leaking).ShouldBeFalse(
            "Vacuity guard: a helper with no ISecretRedactor parameter must NOT match the redactor " +
            "detector. If this fails, the call-site fence passes vacuously.");
    }

    [Fact]
    public void Fence_PositivePin_AcceptsTheRedactedShape()
    {
        // Synthetic positive: the fixed shape must be accepted so the fence does not over-tighten
        // and block the very design it is enforcing.
        const string redacted = """
            public static void ThrowForFailedResponse(
                HttpResponseMessage response, string errorBody, string providerName,
                ISecretRedactor? secretRedactor = null)
            {
                errorBody = Redact(errorBody, secretRedactor);
                throw new HttpRequestException($"HTTP {statusCode}: {body}");
            }
            """;

        SecretRedactorReference.IsMatch(redacted).ShouldBeTrue(
            "Positive pin: the fixed helper signature references ISecretRedactor.");
        RedactCall.IsMatch(redacted).ShouldBeTrue(
            "Positive pin: the fixed helper calls Redact(...) before interpolating.");

        // A provider that forwards a redactor through must not be flagged as an offender.
        const string forwardingCallSite = """
            public sealed class FakeProvider(HttpClient http, ISecretRedactor? secretRedactor = null)
            {
                private void Fail(HttpResponseMessage response, string errorBody)
                    => ProviderHttpErrorHelper.ThrowForFailedResponse(response, errorBody, "Fake", secretRedactor);
            }
            """;

        SecretRedactorReference.IsMatch(forwardingCallSite).ShouldBeTrue(
            "Positive pin: a provider that threads the redactor through must be accepted. If this " +
            "fails, the AC3 detector is over-tight.");
        RawErrorBodyThrow.IsMatch(forwardingCallSite).ShouldBeFalse(
            "Positive pin: forwarding a body to the redacted helper is NOT a raw interpolation and " +
            "must not be flagged by the AC5 scan.");
    }

    private static string ResolvePath(string relative) =>
        Path.Combine(RepoRoot, relative.Replace('/', Path.DirectorySeparatorChar));

    private static string ToRepoRelative(string absolute) =>
        Path.GetRelativePath(RepoRoot, absolute).Replace(Path.DirectorySeparatorChar, '/');

    private static string FindRepoRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "BotNexus.slnx")))
        {
            current = current.Parent;
        }

        current.ShouldNotBeNull("Could not locate repo root (BotNexus.slnx) from " + AppContext.BaseDirectory);
        return current!.FullName;
    }
}
