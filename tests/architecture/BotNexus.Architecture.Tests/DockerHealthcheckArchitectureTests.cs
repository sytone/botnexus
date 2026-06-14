using System.Text.RegularExpressions;
using Shouldly;

namespace BotNexus.Architecture.Tests;

/// <summary>
/// Architecture fitness function guarding the container HEALTHCHECK contract (#1432).
///
/// The <c>mcr.microsoft.com/dotnet/aspnet:10.0</c> runtime base image ships neither
/// <c>curl</c> nor <c>wget</c> — only <c>dotnet</c>. A HEALTHCHECK that shells out to
/// <c>curl</c>/<c>wget</c> therefore exits <c>1</c> on every interval and the container
/// is reported <c>unhealthy</c> forever, even though <c>GET /health</c> returns 200.
/// Orchestrators that gate on container health (compose <c>service_healthy</c>, swarm,
/// k8s readiness via docker health) then treat a perfectly working gateway as never-ready.
///
/// This was caught only during a manual end-to-end container boot (CI cannot run Docker),
/// so a static fitness function is the right regression guard: if the Dockerfile's
/// HEALTHCHECK depends on a binary, that binary MUST be installed in the runtime stage.
/// The fence runs with zero Docker dependency — it parses the Dockerfile text.
/// </summary>
/// <remarks>
/// The fence is intentionally narrow: it only requires an install step when the
/// HEALTHCHECK actually depends on <c>curl</c> or <c>wget</c>. A future switch to a
/// dotnet-native probe (no external binary) would satisfy the fence without an apt layer.
/// </remarks>
public sealed class DockerHealthcheckArchitectureTests
{
    private static string RepoRoot => FindRepoRoot();

    private static string DockerfilePath => Path.Combine(RepoRoot, "Dockerfile");

    private static string DockerComposePath => Path.Combine(RepoRoot, "docker-compose.yml");

    // Binaries that are NOT present in the aspnet:10.0 base image and must be
    // explicitly installed before a HEALTHCHECK can depend on them.
    private static readonly string[] NonBaseBinaries = { "curl", "wget" };

    [Fact]
    public void Dockerfile_Exists()
    {
        File.Exists(DockerfilePath).ShouldBeTrue($"Dockerfile not found at {DockerfilePath}");
    }

    [Fact]
    public void Dockerfile_HealthcheckBinaryIsInstalledInRuntimeStage()
    {
        var dockerfile = File.ReadAllText(DockerfilePath);

        var healthcheck = ExtractHealthcheckLine(dockerfile);
        if (healthcheck is null)
        {
            // No HEALTHCHECK declared — nothing to police. (A future image may rely on the
            // orchestrator's own probe instead.)
            return;
        }

        foreach (var binary in NonBaseBinaries)
        {
            if (!HealthcheckUsesBinary(healthcheck, binary))
            {
                continue;
            }

            DockerfileInstallsBinary(dockerfile, binary).ShouldBeTrue(
                $"Dockerfile HEALTHCHECK depends on `{binary}`, but `{binary}` is not present in the " +
                "mcr.microsoft.com/dotnet/aspnet:10.0 base image (it ships only dotnet). The probe will " +
                $"exit 1 every interval and the container will be reported unhealthy forever. Add a " +
                $"`RUN apt-get install -y --no-install-recommends {binary}` step in the runtime stage, " +
                "or switch the HEALTHCHECK to a dotnet-native probe that needs no external binary. " +
                "See issue #1432.\nFile: " + DockerfilePath);
        }
    }

    [Fact]
    public void DockerCompose_HealthcheckBinaryIsAvailableInImage()
    {
        // docker-compose.yml builds the same Dockerfile image, so a curl/wget healthcheck there
        // is only valid if the Dockerfile installs that binary. This keeps the two healthcheck
        // declarations from drifting (compose using a binary the image doesn't ship).
        if (!File.Exists(DockerComposePath))
        {
            return;
        }

        var compose = File.ReadAllText(DockerComposePath);
        var dockerfile = File.ReadAllText(DockerfilePath);

        foreach (var binary in NonBaseBinaries)
        {
            if (!ComposeHealthcheckUsesBinary(compose, binary))
            {
                continue;
            }

            DockerfileInstallsBinary(dockerfile, binary).ShouldBeTrue(
                $"docker-compose.yml healthcheck depends on `{binary}`, but the Dockerfile image it builds " +
                $"does not install `{binary}` (absent from the aspnet:10.0 base). The compose container will " +
                $"be reported unhealthy forever. Install `{binary}` in the Dockerfile runtime stage or change " +
                "the compose healthcheck. See issue #1432.\nFile: " + DockerComposePath);
        }
    }

    [Fact]
    public void Fence_IsNotVacuous_DetectsCurlHealthcheckWithoutInstall()
    {
        // Synthetic regression: the pre-#1432 Dockerfile shape — curl HEALTHCHECK, no curl install.
        // The detection helpers MUST flag this as a violation.
        const string brokenDockerfile = """
            FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
            WORKDIR /app
            COPY --from=build /app/publish .
            EXPOSE 5000
            HEALTHCHECK --interval=10s --timeout=5s --start-period=30s --retries=5 \
                CMD curl -f http://localhost:5000/health || exit 1
            ENTRYPOINT ["dotnet", "BotNexus.Gateway.Api.dll"]
            """;

        var healthcheck = ExtractHealthcheckLine(brokenDockerfile);
        healthcheck.ShouldNotBeNull("Vacuity guard precondition: HEALTHCHECK must be extracted.");
        HealthcheckUsesBinary(healthcheck!, "curl").ShouldBeTrue(
            "Vacuity guard: the broken shape's HEALTHCHECK must be detected as using curl.");
        DockerfileInstallsBinary(brokenDockerfile, "curl").ShouldBeFalse(
            "Vacuity guard: the broken shape does NOT install curl and the detector must report that. " +
            "If this fails, the install detector is too loose and the fence passes vacuously.");
    }

    [Fact]
    public void Fence_PositivePin_AcceptsCurlHealthcheckWithInstall()
    {
        // Synthetic positive: curl HEALTHCHECK WITH a curl install step. Must be accepted so the
        // fence does not over-tighten against the real (now-fixed) production Dockerfile.
        const string fixedDockerfile = """
            FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
            WORKDIR /app
            RUN apt-get update \
                && apt-get install -y --no-install-recommends curl \
                && rm -rf /var/lib/apt/lists/*
            COPY --from=build /app/publish .
            HEALTHCHECK --interval=10s --timeout=5s --start-period=30s --retries=5 \
                CMD curl -f http://localhost:5000/health || exit 1
            ENTRYPOINT ["dotnet", "BotNexus.Gateway.Api.dll"]
            """;

        DockerfileInstallsBinary(fixedDockerfile, "curl").ShouldBeTrue(
            "Positive pin: a Dockerfile that installs curl must be accepted. If this fails, the " +
            "install detector is over-tight and would false-positive against the real Dockerfile.");
    }

    // ---- helpers ----

    /// <summary>
    /// Extracts the full HEALTHCHECK instruction, joining backslash line-continuations
    /// into a single logical line. Returns null when no HEALTHCHECK is declared.
    /// </summary>
    private static string? ExtractHealthcheckLine(string dockerfile)
    {
        var logical = JoinContinuations(dockerfile);
        foreach (var line in logical)
        {
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("HEALTHCHECK", StringComparison.OrdinalIgnoreCase))
            {
                return trimmed;
            }
        }

        return null;
    }

    private static bool HealthcheckUsesBinary(string healthcheckLine, string binary)
    {
        // Word-boundary match so we don't match e.g. "curly" — the binary must appear as a token.
        var pattern = new Regex(@"(?<![\w-])" + Regex.Escape(binary) + @"(?![\w-])", RegexOptions.IgnoreCase);
        return pattern.IsMatch(healthcheckLine);
    }

    private static bool ComposeHealthcheckUsesBinary(string compose, string binary)
    {
        // Only consider the binary inside a `test:`/healthcheck context. A coarse but reliable
        // signal: the binary token appears on a line that also references the healthcheck test
        // array. We scan lines under a `healthcheck:` block for a `test:` entry naming the binary.
        var lines = compose.Replace("\r\n", "\n").Split('\n');
        var inHealthcheck = false;
        var binaryPattern = new Regex(@"(?<![\w-])" + Regex.Escape(binary) + @"(?![\w-])", RegexOptions.IgnoreCase);

        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("healthcheck:", StringComparison.OrdinalIgnoreCase))
            {
                inHealthcheck = true;
                continue;
            }

            // Leaving the healthcheck block: a non-indented, non-empty line that is not part of it.
            if (inHealthcheck && trimmed.Length > 0 && !line.StartsWith(" ") && !line.StartsWith("\t"))
            {
                inHealthcheck = false;
            }

            if (inHealthcheck && trimmed.StartsWith("test:", StringComparison.OrdinalIgnoreCase) &&
                binaryPattern.IsMatch(trimmed))
            {
                return true;
            }
        }

        return false;
    }

    private static bool DockerfileInstallsBinary(string dockerfile, string binary)
    {
        // A RUN ... apt-get install ... <binary> (or apk add / yum install) step anywhere in the file.
        var logical = JoinContinuations(dockerfile);
        var installPattern = new Regex(
            @"\b(?:apt-get\s+install|apt\s+install|apk\s+add|yum\s+install|dnf\s+install|microdnf\s+install)\b",
            RegexOptions.IgnoreCase);
        var binaryPattern = new Regex(@"(?<![\w-])" + Regex.Escape(binary) + @"(?![\w-])", RegexOptions.IgnoreCase);

        foreach (var line in logical)
        {
            var trimmed = line.TrimStart();
            if (!trimmed.StartsWith("RUN", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (installPattern.IsMatch(trimmed) && binaryPattern.IsMatch(trimmed))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Collapses Dockerfile backslash line-continuations so each multi-line instruction
    /// (RUN, HEALTHCHECK) becomes one logical line for matching.
    /// </summary>
    private static List<string> JoinContinuations(string dockerfile)
    {
        var raw = dockerfile.Replace("\r\n", "\n").Split('\n');
        var result = new List<string>();
        var current = new System.Text.StringBuilder();

        foreach (var line in raw)
        {
            if (line.TrimEnd().EndsWith('\\'))
            {
                current.Append(line.TrimEnd().TrimEnd('\\')).Append(' ');
            }
            else
            {
                current.Append(line);
                result.Add(current.ToString());
                current.Clear();
            }
        }

        if (current.Length > 0)
        {
            result.Add(current.ToString());
        }

        return result;
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
