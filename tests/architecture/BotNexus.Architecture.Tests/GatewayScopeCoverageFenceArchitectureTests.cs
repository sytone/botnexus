namespace BotNexus.Architecture.Tests;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Shouldly;

/// <summary>
/// Fitness fence for gateway permission scope coverage (#2621 AC5).
/// <para>
/// The defect #2621 fixes was a control that existed but was never consulted. The identical thing
/// can happen one layer up: someone adds an authenticated controller under a brand-new resource
/// segment, <c>GatewayScopes.Resources</c> never learns about it, and from then on that endpoint
/// resolves to no scope. It would still fail closed under enforcement - but the operator has no
/// grantable scope for it, so the endpoint becomes permanently unreachable for every non-wildcard
/// caller and nobody finds out until a support ticket. #2606's gap arose for exactly this reason:
/// nothing fenced the consumers of a declared field.
/// </para>
/// <para>
/// This test compares two populations - the routes the controllers actually declare, and the
/// resources the scope vocabulary knows about - so an incomplete addition fails the build the same
/// day it lands rather than being discovered by an audit months later.
/// </para>
/// </summary>
public sealed class GatewayScopeCoverageFenceArchitectureTests
{
    private static string RepoRoot => FindRepoRoot();

    private static string ControllersDirectory => Path.Combine(
        RepoRoot, "src", "gateway", "BotNexus.Gateway.Api", "Controllers");

    private static string ScopesFile => Path.Combine(
        RepoRoot, "src", "gateway", "BotNexus.Gateway.Contracts", "Security", "GatewayScopes.cs");

    /// <summary>
    /// Routes that legitimately carry no scope because the auth middleware never reaches them -
    /// they are listed in its <c>ShouldSkipAuth</c> allow-list and authenticate by another
    /// mechanism entirely. Each needs a reason; an entry with no reason is how an exemption list
    /// turns into a bypass.
    /// </summary>
    private static readonly Dictionary<string, string> UnauthenticatedRouteExemptions =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["federation"] = "api/federation/cross-world is skipped by ShouldSkipAuth; it authenticates per-peer.",
        };

    /// <summary>
    /// Files permitted to spell a scope-shaped literal, each with the reason. These are NOT
    /// gateway API-key permissions - they are a separate vocabulary that collides by string
    /// coincidence. The entry is what keeps that collision visible instead of letting the fence
    /// quietly stop looking at a whole directory.
    /// </summary>
    private static readonly Dictionary<string, string> ScopeLiteralExemptions =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["HubScopeGuard.cs"] =
                "Pre-dates #2621 (landed in #1987). Declares an OAuth CLAIM vocabulary "
                + "('gateway:read'/'gateway:control') read off a ClaimsPrincipal on SignalR hub "
                + "methods - a different mechanism from GatewayCallerIdentity.Permissions, which "
                + "happens to share the 'gateway:read' spelling. Unifying the two is real work with "
                + "its own compatibility surface and is tracked separately; leaving it unexempted "
                + "would make this fence red on arrival for a collision it did not cause.",
        };

    [Fact]
    public void EveryAuthenticatedControllerRoute_HasAResourceInTheScopeVocabulary()
    {
        var declared = ReadDeclaredResources();
        declared.ShouldNotBeEmpty("no resources declared - this fence would be vacuous");

        var violations = new List<string>();

        foreach (var (file, resource) in EnumerateControllerResources())
        {
            if (UnauthenticatedRouteExemptions.ContainsKey(resource))
                continue;

            if (!declared.Contains(resource))
            {
                violations.Add(
                    $"  {file} exposes /api/{resource} but '{resource}' is absent from GatewayScopes.Resources.\n"
                    + "    Fix: add it there. Without an entry the route resolves to no scope, so no operator "
                    + "can grant access to it and enforcement refuses every non-wildcard caller (#2621).");
            }
        }

        violations.ShouldBeEmpty(
            "authenticated gateway routes must be covered by the scope vocabulary (#2621 AC5):\n"
            + string.Join("\n", violations));
    }

    [Fact]
    public void EveryExemptedRoute_StillExists_AndIsStillSkippedByTheAuthMiddleware()
    {
        // A stale exemption is worse than none: it silently pre-authorises a resource segment that
        // may later be reused by a genuinely authenticated controller. The entry must expire
        // loudly.
        var middleware = File.ReadAllText(Path.Combine(
            RepoRoot, "src", "gateway", "BotNexus.Gateway.Api", "GatewayAuthMiddleware.cs"));

        var actualResources = EnumerateControllerResources()
            .Select(entry => entry.Resource)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var stale = new List<string>();

        foreach (var (resource, reason) in UnauthenticatedRouteExemptions)
        {
            reason.ShouldNotBeNullOrWhiteSpace($"exemption '{resource}' must carry a reason");

            if (!actualResources.Contains(resource))
            {
                stale.Add($"  '{resource}' is exempted but no controller declares that route any more.");
                continue;
            }

            if (!middleware.Contains($"/api/{resource}", StringComparison.OrdinalIgnoreCase))
            {
                stale.Add(
                    $"  '{resource}' is exempted as unauthenticated but GatewayAuthMiddleware no longer "
                    + "skips it - it is now an authenticated route and needs a real scope.");
            }
        }

        stale.ShouldBeEmpty(
            "stale scope-coverage exemptions (#2621 AC5):\n" + string.Join("\n", stale));
    }

    [Fact]
    public void TheEnforcementCheck_IsInvokedByTheAuthMiddleware()
    {
        // The whole issue is a control that is declared but never consulted. This asserts the call
        // site exists at all, so deleting the invocation is a build-visible act rather than a
        // quiet reversion to the state #2621 describes.
        var middleware = File.ReadAllText(Path.Combine(
            RepoRoot, "src", "gateway", "BotNexus.Gateway.Api", "GatewayAuthMiddleware.cs"));

        middleware.ShouldContain(
            "IsPermittedAsync",
            Case.Sensitive,
            "GatewayAuthMiddleware must consult the permission check on the authenticated path (#2621).");

        middleware.ShouldContain(
            "GatewayScopes.Resolve",
            Case.Sensitive,
            "the required scope must come from the single vocabulary, not a local string (#2621 constraint 4).");
    }

    [Fact]
    public void NoSourceFileOutsideGatewayScopes_RespellsAScopeSuffixAsALiteral()
    {
        // Constraint 4: one vocabulary, not N free-form strings. A ":read"/":write" literal built
        // elsewhere is the drift this issue exists to prevent.
        var violations = new List<string>();

        foreach (var file in EnumerateSourceFiles(Path.Combine(RepoRoot, "src")))
        {
            if (string.Equals(Path.GetFullPath(file), Path.GetFullPath(ScopesFile), StringComparison.OrdinalIgnoreCase))
                continue;

            if (ScopeLiteralExemptions.ContainsKey(Path.GetFileName(file)))
                continue;

            var text = File.ReadAllText(file);

            foreach (var resource in ReadDeclaredResources())
            {
                foreach (var access in new[] { "read", "write" })
                {
                    var literal = "\"" + resource + ":" + access + "\"";
                    var index = text.IndexOf(literal, StringComparison.Ordinal);
                    if (index < 0)
                        continue;

                    var line = text.Take(index).Count(c => c == '\n') + 1;
                    violations.Add(
                        $"  {ToRepoRelative(file)}:{line} spells scope {literal} as a literal.\n"
                        + "    Fix: derive it via GatewayScopes.Resolve. The vocabulary is declared once (#2621).");
                }
            }
        }

        violations.ShouldBeEmpty(
            "permission scopes must not be re-spelled as literals in src (#2621 constraint 4):\n"
            + string.Join("\n", violations));
    }

    [Fact]
    public void EveryScopeLiteralExemption_StillExists_AndStillContainsAScopeShapedLiteral()
    {
        // A fictional or expired exemption is worse than none: it becomes blanket cover for future
        // additions to that file. The entry must fail loudly when it stops being true.
        var resources = ReadDeclaredResources();
        var stale = new List<string>();

        foreach (var (fileName, reason) in ScopeLiteralExemptions)
        {
            reason.ShouldNotBeNullOrWhiteSpace($"exemption '{fileName}' must carry a reason");

            var matches = EnumerateSourceFiles(Path.Combine(RepoRoot, "src"))
                .Where(path => string.Equals(Path.GetFileName(path), fileName, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (matches.Count == 0)
            {
                stale.Add($"  '{fileName}' is exempted but no such file exists under src any more.");
                continue;
            }

            var stillViolates = matches.Any(path =>
            {
                var text = File.ReadAllText(path);
                return resources.Any(resource =>
                    text.Contains($"\"{resource}:read\"", StringComparison.Ordinal) ||
                    text.Contains($"\"{resource}:write\"", StringComparison.Ordinal));
            });

            if (!stillViolates)
            {
                stale.Add(
                    $"  '{fileName}' is exempted but no longer contains a scope-shaped literal - "
                    + "remove the exemption.");
            }
        }

        stale.ShouldBeEmpty(
            "stale scope-literal exemptions (#2621 constraint 4):\n" + string.Join("\n", stale));
    }

    private static IEnumerable<(string File, string Resource)> EnumerateControllerResources()
    {
        foreach (var file in Directory.EnumerateFiles(ControllersDirectory, "*Controller.cs", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(file);
            var controllerName = Path.GetFileNameWithoutExtension(file);
            if (controllerName.EndsWith("Controller", StringComparison.Ordinal))
                controllerName = controllerName[..^"Controller".Length];

            foreach (Match match in Regex.Matches(text, @"\[Route\(""([^""]+)""\)\]"))
            {
                var route = match.Groups[1].Value.Replace(
                    "[controller]", controllerName.ToLowerInvariant(), StringComparison.OrdinalIgnoreCase);

                var segments = route.Split('/', StringSplitOptions.RemoveEmptyEntries);
                if (segments.Length < 2 || !string.Equals(segments[0], "api", StringComparison.OrdinalIgnoreCase))
                    continue;

                yield return (ToRepoRelative(file), segments[1].ToLowerInvariant());
            }
        }
    }

    private static IReadOnlySet<string> ReadDeclaredResources()
    {
        var text = File.ReadAllText(ScopesFile);

        var block = Regex.Match(text, @"Resources\s*=\s*\[(?<body>[^\]]*)\]", RegexOptions.Singleline);
        block.Success.ShouldBeTrue("could not locate GatewayScopes.Resources - the fence cannot read the vocabulary");

        return Regex.Matches(block.Groups["body"].Value, "\"([^\"]+)\"")
            .Select(match => match.Groups[1].Value)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> EnumerateSourceFiles(string root)
        => Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                        && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

    private static string ToRepoRelative(string absolutePath)
        => absolutePath.Substring(RepoRoot.Length).TrimStart(Path.DirectorySeparatorChar).Replace('\\', '/');

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
