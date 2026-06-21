using System.Text.RegularExpressions;
using Shouldly;

namespace BotNexus.Architecture.Tests;

/// <summary>
/// Architecture fitness functions guarding the two fixes in #1538 (main-branch CI red on the
/// "Security: Secrets &amp; Dependencies" workflow).
///
/// <para><b>Part 1 - the unpatchable transitive advisory.</b> <c>Microsoft.Data.Sqlite</c> pulls in
/// the native <c>SQLitePCLRaw.lib.e_sqlite3</c> package, which carries the HIGH-severity advisory
/// <c>GHSA-2m69-gcr7-jv3q</c>. At the time of #1538 that advisory's affected range was <c>&lt;= 2.1.11</c>
/// with <b>no published patched version</b> (2.1.11 was the latest release and was still flagged), so
/// there was nowhere to bump to. .NET 10's <c>NuGetAudit</c> promotes the finding to <c>NU1903</c>, and
/// with <c>TreatWarningsAsErrors=true</c> this fails <c>dotnet restore</c> on every SQLite-referencing
/// project, turning <c>main</c> CI red. The fix is an <b>advisory-scoped</b>
/// <c>&lt;NuGetAuditSuppress&gt;</c> entry in <c>Directory.Packages.props</c> - NOT a blanket
/// <c>NoWarn NU1903</c>, so any other/future vulnerable package still breaks the build, and the
/// finding stays visible to <c>dotnet list package --vulnerable</c>. This fence pins the suppression
/// so the main-CI-red regression cannot silently reappear, and asserts it is advisory-scoped.
///
/// <para><b>Part 2 - the broken auto-issue github-script.</b> The workflow's own failure reporter
/// (a <c>github-script</c> step) built its issue body by interpolating
/// <c>const body = `${{ steps.summary.outputs.body }}`;</c> directly into a JavaScript template
/// literal. The summary body contains Markdown including an inline-code backtick
/// (<c>`dotnet add package ...`</c>), which prematurely terminates the template literal and breaks the
/// script with <c>SyntaxError: Unexpected identifier 'dotnet'</c> - so every security-scan failure was
/// silently un-reported. It is also a script-injection vector. The fix passes the value via an
/// environment variable read with <c>process.env</c>. This fence forbids reintroducing the
/// <c>${{ ... }}</c>-into-a-JS-template-literal antipattern for the summary body.
///
/// Both checks are text fitness functions over the committed repo files (zero runtime dependency),
/// matching the existing repo-config fences (e.g. <see cref="SqliteBusyTimeoutArchitectureTests"/>,
/// DockerHealthcheckArchitectureTests).
/// </summary>
public sealed class SecurityWorkflowAndAuditSuppressionArchitectureTests
{
    private static string RepoRoot => FindRepoRoot();

    private const string AdvisoryId = "GHSA-2m69-gcr7-jv3q";
    private const string AdvisoryUrl = "https://github.com/advisories/" + AdvisoryId;

    private static readonly string PackagesPropsPath =
        Path.Combine(RepoRoot, "Directory.Packages.props");

    private static readonly string SecurityWorkflowPath = Path.Combine(
        RepoRoot, ".github", "workflows", "security-secrets-deps.yml");

    // Matches a NuGetAuditSuppress item for the specific advisory (URL or bare GHSA id).
    private static readonly Regex AdvisorySuppress = new(
        @"<NuGetAuditSuppress\b[^>]*Include\s*=\s*""[^""]*GHSA-2m69-gcr7-jv3q[^""]*""",
        RegexOptions.IgnoreCase);

    // The antipattern: const body = `${{ steps.summary.outputs.body }}`; (interpolating the body
    // into a JS template literal). Allow arbitrary whitespace around tokens.
    private static readonly Regex BodyTemplateLiteralInterpolation = new(
        @"const\s+body\s*=\s*`\$\{\{\s*steps\.summary\.outputs\.body\s*\}\}`",
        RegexOptions.IgnoreCase);

    [Fact]
    public void GuardedFiles_Exist()
    {
        File.Exists(PackagesPropsPath).ShouldBeTrue(
            $"Expected central package management file not found: {PackagesPropsPath}");
        File.Exists(SecurityWorkflowPath).ShouldBeTrue(
            $"Expected security workflow not found: {SecurityWorkflowPath}");
    }

    [Fact]
    public void PackagesProps_SuppressesTheUnpatchableSqliteAdvisory()
    {
        var props = File.ReadAllText(PackagesPropsPath);

        AdvisorySuppress.IsMatch(props).ShouldBeTrue(
            $"Directory.Packages.props must contain a <NuGetAuditSuppress Include=\"...{AdvisoryId}...\" /> " +
            "entry. Without it, NuGetAudit promotes the unpatchable SQLitePCLRaw.lib.e_sqlite3 advisory " +
            "to NU1903 and TreatWarningsAsErrors fails `dotnet restore` on every SQLite project, turning " +
            "main CI red. Remove this suppression only once a patched native lib (> 2.1.11) ships. See #1538.");

        // It must reference the advisory by its stable URL, not a fragile package+version pin.
        props.ShouldContain(AdvisoryUrl,
            customMessage: "The audit suppression should reference the advisory by its canonical URL " +
            $"({AdvisoryUrl}) so it survives version bumps and is greppable. See #1538.");
    }

    [Fact]
    public void PackagesProps_DoesNotBlanketSuppressNu1903()
    {
        var props = File.ReadAllText(PackagesPropsPath);

        // A blanket NoWarn/NoWarning of NU1903 would hide ALL high-severity vuln findings, defeating
        // the audit. The fix must be advisory-scoped only.
        Regex.IsMatch(props, @"<(NoWarn|WarningsNotAsErrors|MSBuildWarningsAsMessages)>[^<]*NU1903",
            RegexOptions.IgnoreCase).ShouldBeFalse(
            "Directory.Packages.props must NOT blanket-suppress NU1903 (that would hide every future " +
            "high-severity vulnerable package). Suppress only the specific advisory via " +
            "<NuGetAuditSuppress>. See #1538.");
    }

    [Fact]
    public void SecurityWorkflow_DoesNotInterpolateBodyIntoJsTemplateLiteral()
    {
        var workflow = File.ReadAllText(SecurityWorkflowPath);

        BodyTemplateLiteralInterpolation.IsMatch(workflow).ShouldBeFalse(
            "security-secrets-deps.yml must NOT build the issue body as " +
            "`const body = `${{ steps.summary.outputs.body }}`;`. The summary body contains Markdown " +
            "with inline-code backticks (e.g. `dotnet add package ...`) that prematurely terminate the " +
            "JS template literal, breaking the github-script step with " +
            "'SyntaxError: Unexpected identifier' so failures go silently unreported. Pass the value via " +
            "an env var and read it with process.env instead. See #1538.");
    }

    [Fact]
    public void SecurityWorkflow_ReadsSummaryBodyFromProcessEnv()
    {
        var workflow = File.ReadAllText(SecurityWorkflowPath);

        // Positive pin: the fixed shape reads the body from process.env (inert data, no template-literal
        // break and no script injection).
        workflow.ShouldContain("process.env.SCAN_BODY",
            customMessage: "The 'Create or update security issue' github-script step should read the " +
            "summary body from process.env.SCAN_BODY (set in the step's env: block) rather than " +
            "interpolating ${{ steps.summary.outputs.body }} into the script. See #1538.");

        Regex.IsMatch(workflow, @"SCAN_BODY:\s*\$\{\{\s*steps\.summary\.outputs\.body\s*\}\}").ShouldBeTrue(
            "The github-script step must pass steps.summary.outputs.body into an env var named SCAN_BODY " +
            "(SCAN_BODY: ${{ steps.summary.outputs.body }}). See #1538.");
    }

    [Fact]
    public void AdvisorySuppressDetector_IsNotVacuous()
    {
        // Vacuity guard: the detector must NOT match a file that lacks the advisory suppression, and
        // must match one that has it. If either fails the fence passes/locks vacuously.
        const string withoutSuppress = """
            <Project>
              <ItemGroup>
                <PackageVersion Include="Microsoft.Data.Sqlite" Version="10.0.0-preview.3.25171.6" />
              </ItemGroup>
            </Project>
            """;
        const string withSuppress = """
            <Project>
              <ItemGroup>
                <NuGetAuditSuppress Include="https://github.com/advisories/GHSA-2m69-gcr7-jv3q" />
              </ItemGroup>
            </Project>
            """;

        AdvisorySuppress.IsMatch(withoutSuppress).ShouldBeFalse(
            "Vacuity guard: detector must report the advisory suppression is ABSENT when it is missing.");
        AdvisorySuppress.IsMatch(withSuppress).ShouldBeTrue(
            "Vacuity guard: detector must recognise the advisory suppression when it is present.");
    }

    [Fact]
    public void TemplateLiteralAntipatternDetector_IsNotVacuous()
    {
        // Vacuity guard for the github-script fence: the detector must flag the broken shape and accept
        // the fixed (process.env) shape.
        const string brokenShape = """
            script: |
              const body = `${{ steps.summary.outputs.body }}`;
            """;
        const string fixedShape = """
            env:
              SCAN_BODY: ${{ steps.summary.outputs.body }}
            script: |
              const body = process.env.SCAN_BODY || '';
            """;

        BodyTemplateLiteralInterpolation.IsMatch(brokenShape).ShouldBeTrue(
            "Vacuity guard: detector must flag the broken template-literal interpolation shape.");
        BodyTemplateLiteralInterpolation.IsMatch(fixedShape).ShouldBeFalse(
            "Vacuity guard: detector must accept the fixed process.env shape.");
    }

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
