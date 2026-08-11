using System.Text.RegularExpressions;
using Shouldly;

namespace BotNexus.Architecture.Tests;

/// <summary>
/// Architecture fitness function for clause 1 of #2747: the BotNexus CLI must build every
/// <b>gateway-API</b> <see cref="System.Net.Http.HttpClient"/> through the single
/// <c>GatewayClientFactory</c>.
///
/// <para><b>Why a fence and not just a fix.</b> #2747 was not one bug, it was the same bug
/// six times: six commands each answered "does this request carry a credential, and to whom
/// may we send it?" independently, and all six answered "none, to whatever the operator
/// typed on <c>--url</c>". Fixing the six call sites removes today's instances; it does
/// nothing about the seventh command, which reintroduces the defect by omission - the author
/// writes <c>new HttpClient()</c>, it works against the local gateway, and the credential
/// policy is silently absent. A policy that must hold for every gateway call cannot be
/// enforced by six independent definitions, so this test makes the absence of a seventh
/// definition a build failure rather than a review-time hope.</para>
///
/// <para><b>Allow-listing is explicit, never implicit.</b> Some CLI code legitimately builds
/// its own client: local reachability probes and non-gateway third-party API calls (GitHub
/// Copilot device-code/token endpoints). Those are enumerated below with a stated reason
/// each. The alternative - a regex that quietly skips anything named "*Probe" or "*Check" -
/// would let a future gateway command slip through by naming coincidence, which is exactly
/// the silent-match failure mode this fence exists to prevent. An entry that no longer
/// constructs a client is a stale entry and also fails, so the list cannot rot into a
/// blanket exemption.</para>
///
/// <para>Source-text based, like <see cref="SecretFilePermissionFenceArchitectureTests"/>:
/// "this call site consulted the credential policy" is a property of how the client was
/// constructed, which reflection over the compiled assembly cannot observe.</para>
/// </summary>
public sealed class GatewayClientFactoryFenceArchitectureTests
{
    private static string RepoRoot => FindRepoRoot();

    /// <summary>Root of the CLI project this fence governs.</summary>
    private const string CliRoot = "src/gateway/BotNexus.Cli";

    /// <summary>The one type permitted to define how a gateway-API client is built.</summary>
    private const string FactorySource = CliRoot + "/Services/GatewayClientFactory.cs";

    /// <summary>
    /// Files permitted to construct an <c>HttpClient</c> directly, each with the reason it is
    /// NOT a gateway-API call site. Adding an entry here is a deliberate, reviewable act.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> AllowedConstructionSites =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [FactorySource] =
                "The factory itself - the single definition of the gateway client and its credential policy.",
            [CliRoot + "/Commands/CronCommands.cs"] =
                "Injection seam only: the parameterless constructor supplies a client for the DI-less " +
                "path, and every cron subcommand hands that client to GatewayClientFactory.ApplyPolicy " +
                "before any request. Pinned by CronCommands_AppliesPolicyToItsInjectedClient below.",
            [CliRoot + "/Commands/DoctorCommand.cs"] =
                "Local reachability probe: doctor asks 'is anything listening', not the gateway API.",
            [CliRoot + "/Commands/Doctor/LocationAccessibilityCheck.cs"] =
                "Local reachability probe for a configured location URL - not a gateway-API call.",
            [CliRoot + "/Commands/Provider/CopilotProviderSubcommand.cs"] =
                "Third-party GitHub Copilot device-code/token/model endpoints - a different service " +
                "with its own credential, so the gateway credential policy does not apply.",
            [CliRoot + "/Services/GatewayProcessManager.cs"] =
                "Liveness probe against the locally managed gateway process; never carries operator input.",
            [CliRoot + "/Services/HttpHealthChecker.cs"] =
                "Generic health-endpoint probe used by process supervision - not a gateway-API call.",
        };

    /// <summary>
    /// Gateway-API command surfaces that must be routed through the factory. These are the
    /// clause-1 call sites; the list is the positive half of the fence, so a command that is
    /// silently deleted or renamed fails rather than passing vacuously.
    /// </summary>
    private static readonly string[] GatewayApiCallSites =
    {
        CliRoot + "/Commands/CronCommands.cs",
        CliRoot + "/Commands/ConversationCommands.cs",
        CliRoot + "/Commands/DebugGatewayCommand.cs",
        // Clause 1, previously bare: POST {gatewayUrl}/api/chat.
        CliRoot + "/Commands/PromptCommands.cs",
        // Clause 1, previously bare: GET {gatewayUrl}/api/config/validate.
        CliRoot + "/Commands/ValidateCommand.cs",
    };

    private static readonly Regex HttpClientConstruction =
        new(@"new\s+HttpClient\s*[({]", RegexOptions.Compiled);

    private static readonly Regex FactoryUse =
        new(@"GatewayClientFactory\s*\.\s*(Resolve|ApplyPolicy)\s*\(", RegexOptions.Compiled);

    [Fact]
    public void Factory_Exists()
    {
        var path = ResolvePath(FactorySource);
        File.Exists(path).ShouldBeTrue(
            "The single gateway-client factory is missing. Every gateway-facing CLI command " +
            $"depends on it for the credential policy (#2747). Expected at: {path}");
    }

    [Fact]
    public void NoGatewayApiHttpClient_IsConstructedOutsideTheFactory()
    {
        var cliRoot = Path.Combine(RepoRoot, CliRoot.Replace('/', Path.DirectorySeparatorChar));
        Directory.Exists(cliRoot).ShouldBeTrue($"CLI source root not found: {cliRoot}");

        var offenders = Directory
            .EnumerateFiles(cliRoot, "*.cs", SearchOption.AllDirectories)
            .Where(file => HttpClientConstruction.IsMatch(File.ReadAllText(file)))
            .Select(ToRepoRelative)
            .Where(relative => !AllowedConstructionSites.ContainsKey(relative))
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();

        offenders.ShouldBeEmpty(
            "These CLI files construct an HttpClient directly instead of going through " +
            "GatewayClientFactory: " + string.Join(", ", offenders) +
            ".\nA bare client carries no credential and will happily send an unauthenticated " +
            "request to whatever host the operator typed after --url - the exact defect #2747 " +
            "closed in six commands at once. Call GatewayClientFactory.Resolve (or ApplyPolicy " +
            "for an injected client) and honour the refusal it returns. If this file genuinely " +
            "does not talk to the gateway API, add it to AllowedConstructionSites WITH A REASON " +
            "so the exemption is reviewed rather than assumed.");
    }

    [Fact]
    public void EveryAllowListEntry_StillExists_AndStillConstructsAClient()
    {
        foreach (var (relative, reason) in AllowedConstructionSites)
        {
            var path = ResolvePath(relative);
            File.Exists(path).ShouldBeTrue(
                $"Allow-listed file no longer exists: {relative} (reason on record: {reason}). " +
                "Remove the entry - a stale allow-list slowly becomes a blanket exemption. See #2747.");

            HttpClientConstruction.IsMatch(File.ReadAllText(path)).ShouldBeTrue(
                $"'{relative}' is allow-listed to construct an HttpClient but no longer does. " +
                "Remove the entry so the exemption cannot silently cover a future gateway-API " +
                "call added to this file. See #2747.");
        }
    }

    [Fact]
    public void EveryGatewayApiCallSite_RoutesThroughTheFactory()
    {
        foreach (var relative in GatewayApiCallSites)
        {
            var path = ResolvePath(relative);
            File.Exists(path).ShouldBeTrue(
                $"Expected gateway-API command source not found: {path}. If it was renamed, update " +
                "GatewayApiCallSites - do not delete the entry without confirming the call seam is gone.");

            FactoryUse.IsMatch(File.ReadAllText(path)).ShouldBeTrue(
                $"'{relative}' issues gateway-API requests but never calls GatewayClientFactory.Resolve " +
                "or GatewayClientFactory.ApplyPolicy, so its requests bypass the credential policy: no " +
                "credential is attached, and an operator-supplied --url is contacted unauthenticated " +
                $"instead of being refused. See #2747 clause 1.\nFile: {path}");
        }
    }

    [Fact]
    public void CronCommands_AppliesPolicyToItsInjectedClient()
    {
        // CronCommands is allow-listed for construction because it owns an injected client.
        // That exemption is only safe while the injected client is policed before use.
        var source = File.ReadAllText(ResolvePath(CliRoot + "/Commands/CronCommands.cs"));

        Regex.IsMatch(source, @"GatewayClientFactory\s*\.\s*ApplyPolicy\s*\(").ShouldBeTrue(
            "CronCommands constructs its own HttpClient (the DI-less constructor seam) and is " +
            "allow-listed for it, on the condition that every subcommand runs the client through " +
            "GatewayClientFactory.ApplyPolicy first. No ApplyPolicy call remains, so the exemption " +
            "now hides an unpoliced gateway client. See #2747.");
    }

    [Fact]
    public void GatewayCallSites_DoNotHardcodeAStaleDefaultPort()
    {
        // #2747/#2858 drift: PromptCommands fell back to localhost:5000 while the gateway binds
        // 5005 and GatewayClientFactory.DefaultUrl says 5005. A second spelling of "the default
        // gateway URL" is the same duplicated-definition defect as a second HttpClient.
        var offenders = GatewayApiCallSites
            .Where(relative => File.ReadAllText(ResolvePath(relative)).Contains("localhost:5000\""))
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();

        offenders.ShouldBeEmpty(
            "These gateway call sites hardcode a stale default gateway URL: " +
            string.Join(", ", offenders) +
            ".\nThe gateway binds 5005 and GatewayClientFactory.DefaultUrl is the single source of " +
            "that truth. A local literal drifts the moment the default moves, producing a connection " +
            "refused that looks like a dead gateway. Use GatewayClientFactory.DefaultUrl. See #2747.");
    }

    [Fact]
    public void Fence_IsNotVacuous_DetectsBareClientAndMissingFactoryUse()
    {
        const string bareGatewayCommand = """
            internal sealed class SeventhGatewayCommand
            {
                public async Task<int> RunAsync(string gatewayUrl)
                {
                    using var httpClient = new HttpClient();
                    var response = await httpClient.GetAsync($"{gatewayUrl}/api/agents");
                    return response.IsSuccessStatusCode ? 0 : 1;
                }
            }
            """;

        HttpClientConstruction.IsMatch(bareGatewayCommand).ShouldBeTrue(
            "Vacuity guard: a bare 'new HttpClient()' MUST be detected. If this fails the fence " +
            "matches nothing and a seventh unpoliced gateway command ships unnoticed.");
        FactoryUse.IsMatch(bareGatewayCommand).ShouldBeFalse(
            "Vacuity guard: a command that never calls the factory must NOT satisfy the routing " +
            "assertion, otherwise EveryGatewayApiCallSite_RoutesThroughTheFactory passes vacuously.");

        // The object-initializer form must be caught too - it is how the existing local probes
        // spell it, and a detector that only matched '()' would miss half the surface.
        HttpClientConstruction.IsMatch("var c = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };")
            .ShouldBeTrue("Vacuity guard: the object-initializer construction form must be detected.");
    }

    [Fact]
    public void Fence_PositivePin_AcceptsFactoryRoutedCommand()
    {
        const string routedCommand = """
            internal sealed class RoutedGatewayCommand
            {
                public async Task<int> RunAsync(string gatewayUrl, string? token)
                {
                    var resolution = GatewayClientFactory.Resolve(
                        gatewayUrl,
                        TimeSpan.FromSeconds(30),
                        token,
                        GatewayClientFactory.DefaultCredentialSource());
                    if (resolution.IsRefused)
                        return 1;

                    using var httpClient = resolution.Client!;
                    var response = await httpClient.GetAsync("/api/agents");
                    return response.IsSuccessStatusCode ? 0 : 1;
                }
            }
            """;

        FactoryUse.IsMatch(routedCommand).ShouldBeTrue(
            "Positive pin: a command routed through GatewayClientFactory.Resolve must be accepted. " +
            "If this fails the routing detector is over-tight and correct code cannot go green.");
        HttpClientConstruction.IsMatch(routedCommand).ShouldBeFalse(
            "Positive pin: consuming the factory's client must NOT be flagged as a bare construction.");
    }

    private static string ToRepoRelative(string absolutePath) =>
        Path.GetRelativePath(RepoRoot, absolutePath).Replace('\\', '/');

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
