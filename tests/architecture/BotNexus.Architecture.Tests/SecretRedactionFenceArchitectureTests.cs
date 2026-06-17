using System.Text.RegularExpressions;
using Shouldly;

namespace BotNexus.Architecture.Tests;

/// <summary>
/// Architecture fitness function for the config/secret-echoing redaction fence (#1502, companion
/// to the concrete leak fix #1494).
///
/// The lesson behind #1494 was that a <b>sibling</b> surface (<c>DebugTool</c> raw-SQL output)
/// bypassed the schema-aware redaction the main <c>/config</c> surface already applied, silently
/// reopening a secret leak. This fence makes the guarantee <b>structural</b> rather than
/// per-endpoint: every surface known to echo config / stored content to an HTTP or chat response
/// must route that output through a redactor, and the redactor must keep its secret-pattern
/// coverage. A new surface that forgets to redact, or a regression that drops a redaction call or
/// a secret pattern, fails CI here instead of leaking in production.
///
/// It runs with zero runtime dependency - it parses source text, which is the right guard because
/// "this surface routes through the redactor" is awkward to assert reliably across the differing
/// controller/tool shapes at runtime (and a reflection scan cannot tell that a call is on the
/// output path rather than dead code). Mirrors <see cref="SqliteBusyTimeoutArchitectureTests"/>.
///
/// Two distinct, legitimate redaction mechanisms exist and the fence treats them per-surface:
/// <list type="bullet">
///   <item><c>ConfigController</c> echoes structured JSON config and masks via its key-name-aware
///   <c>RedactSecrets</c> / <c>RedactProviderSecrets</c> helpers.</item>
///   <item><c>DebugTool</c> echoes free-text DB rows / runtime dumps and masks via the regex
///   <c>ISecretRedactor</c> (the <c>Redact(...)</c> path wired in #1494).</item>
/// </list>
/// </summary>
public sealed class SecretRedactionFenceArchitectureTests
{
    private static string RepoRoot => FindRepoRoot();

    private const string ConfigController =
        "src/gateway/BotNexus.Gateway.Api/Controllers/ConfigController.cs";

    private const string DebugTool =
        "src/extensions/BotNexus.Extensions.DebugTool/DebugTool.cs";

    private const string SecretRedactorSource =
        "src/gateway/BotNexus.Gateway/Security/SecretRedactor.cs";

    private static readonly string[] SurfaceFiles =
    {
        ConfigController,
        DebugTool,
        SecretRedactorSource,
    };

    // ConfigController returns config JSON; it must mask via one of its redaction helpers.
    private static readonly Regex ConfigRedactionCall =
        new(@"Redact(Secrets|ProviderSecrets)\s*\(", RegexOptions.Compiled);

    // DebugTool echoes free-text DB/runtime content; it must route it through the regex redactor.
    private static readonly Regex SecretRedactorReference =
        new(@"ISecretRedactor", RegexOptions.Compiled);
    private static readonly Regex RedactCall =
        new(@"\bRedact\s*\(", RegexOptions.Compiled);

    /// <summary>
    /// The secret-pattern families <see cref="BotNexus.Gateway.Security.SecretRedactor"/> must keep.
    /// Each entry is a <c>[GeneratedRegex]</c>-backed factory name; dropping one silently narrows
    /// redaction coverage, which is exactly the kind of regression this pin catches.
    /// </summary>
    private static readonly string[] RequiredRedactorPatternFactories =
    {
        "OpenAiProjectKeyRegex",
        "OpenAiLegacyKeyRegex",
        "AnthropicKeyRegex",
        "GitHubFineGrainedPatRegex",
        "GitHubClassicTokenRegex",
        "AwsAccessKeyRegex",
        "GoogleApiKeyRegex",
        "SlackTokenRegex",
        "StripeSecretKeyRegex",
        "AuthorizationBearerRegex",
        "GenericApiKeyRegex",
    };

    [Fact]
    public void AllSurfaceFiles_Exist()
    {
        foreach (var rel in SurfaceFiles)
        {
            var path = ResolvePath(rel);
            File.Exists(path).ShouldBeTrue($"Expected secret-redaction surface source not found: {path}");
        }
    }

    [Fact]
    public void ConfigController_RoutesConfigOutputThroughRedaction()
    {
        var source = File.ReadAllText(ResolvePath(ConfigController));

        ConfigRedactionCall.IsMatch(source).ShouldBeTrue(
            "ConfigController echoes platform configuration to an HTTP response but never calls " +
            "RedactSecrets / RedactProviderSecrets. Every config-returning endpoint must mask " +
            "secret-shaped values before Ok(...). A new config endpoint that skips redaction " +
            "reopens the #1494 leak class. See #1502.\nFile: " + ResolvePath(ConfigController));
    }

    [Fact]
    public void DebugTool_RoutesEchoedContentThroughSecretRedactor()
    {
        var source = File.ReadAllText(ResolvePath(DebugTool));

        SecretRedactorReference.IsMatch(source).ShouldBeTrue(
            "DebugTool echoes raw DB rows / runtime dumps but does not depend on ISecretRedactor. " +
            "It must take an ISecretRedactor and route echoed content through it (#1494). See #1502.\n" +
            "File: " + ResolvePath(DebugTool));

        RedactCall.IsMatch(source).ShouldBeTrue(
            "DebugTool references ISecretRedactor but never calls Redact(...) on its output. " +
            "The raw-SQL / query / runtime-status output path must be masked before it is returned " +
            "to the agent. See #1494 / #1502.\nFile: " + ResolvePath(DebugTool));
    }

    [Fact]
    public void SecretRedactor_KeepsAllKnownSecretPatternFamilies()
    {
        var source = File.ReadAllText(ResolvePath(SecretRedactorSource));

        foreach (var factory in RequiredRedactorPatternFactories)
        {
            source.ShouldContain(factory,
                Case.Sensitive,
                $"SecretRedactor no longer defines the '{factory}' pattern factory. Removing a secret " +
                "pattern silently narrows redaction coverage across every surface that depends on it " +
                "(session writes, compaction summaries, DebugTool output). If a pattern was intentionally " +
                "renamed/replaced, update this pin to the new factory name. See #1502.");
        }
    }

    [Fact]
    public void Fence_IsNotVacuous_DetectsMissingRedaction()
    {
        // Synthetic regression: a config-echoing surface that returns config without redaction.
        const string unredactedSurface = """
            public sealed class FakeConfigController
            {
                public ActionResult<JsonObject> GetConfig()
                {
                    var response = SerializeConfig();
                    return Ok(response);
                }
            }
            """;

        ConfigRedactionCall.IsMatch(unredactedSurface).ShouldBeFalse(
            "Vacuity guard: a surface that returns config with no RedactSecrets/RedactProviderSecrets " +
            "call must NOT match the redaction detector. If this fails, the detector is too loose and " +
            "the ConfigController fence passes vacuously.");

        // Synthetic regression: a tool that echoes a query result with no redactor reference.
        const string unredactedTool = """
            public sealed class FakeDebugTool
            {
                public string RunQuery(string sql) => ExecuteRaw(sql);
            }
            """;

        SecretRedactorReference.IsMatch(unredactedTool).ShouldBeFalse(
            "Vacuity guard: a tool with no ISecretRedactor reference must NOT match the redactor " +
            "detector. If this fails, the DebugTool fence passes vacuously.");
    }

    [Fact]
    public void Fence_PositivePin_AcceptsRedactedSurfaces()
    {
        // Synthetic positive: the fixed shapes must be accepted so the fence does not over-tighten.
        const string redactedConfig = """
            public sealed class FakeConfigController
            {
                public ActionResult<JsonObject> GetConfig()
                {
                    var response = SerializeConfig();
                    RedactSecrets(response);
                    return Ok(response);
                }
            }
            """;

        ConfigRedactionCall.IsMatch(redactedConfig).ShouldBeTrue(
            "Positive pin: a ConfigController that calls RedactSecrets before Ok(...) must be accepted. " +
            "If this fails, the redaction detector is over-tight.");

        const string redactedTool = """
            public sealed class FakeDebugTool
            {
                private readonly ISecretRedactor? _redactor;
                private string Redact(string text) => _redactor?.Redact(text) ?? text;
                public string RunQuery(string sql) => Redact(ExecuteRaw(sql));
            }
            """;

        SecretRedactorReference.IsMatch(redactedTool).ShouldBeTrue(
            "Positive pin precondition: the fixed tool references ISecretRedactor.");
        RedactCall.IsMatch(redactedTool).ShouldBeTrue(
            "Positive pin: a tool that calls Redact(...) on its output must be accepted. " +
            "If this fails, the Redact-call detector is over-tight.");
    }

    private static string ResolvePath(string relative) =>
        Path.Combine(RepoRoot, relative.Replace('/', Path.DirectorySeparatorChar));

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
