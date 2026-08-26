using System.Text.RegularExpressions;
using System.Xml.Linq;
using Shouldly;

namespace BotNexus.Architecture.Tests;

/// <summary>
/// Architecture fitness function fencing what may enter the Blazor WebAssembly payload (#2329).
///
/// <para>
/// <b>Every managed assembly referenced by a Blazor WASM app is downloaded by the browser.</b> The
/// two WASM entry points (<c>...SignalR.BlazorClient</c> and <c>...SignalR.BlazorClient.Mobile</c>,
/// both <c>Microsoft.NET.Sdk.BlazorWebAssembly</c>) therefore have a hard cost per reference that no
/// build error, warning, or test previously surfaced. The failure mode is invisible: the build
/// succeeds, tests pass, and the only symptom is a slower first load for every user.
/// </para>
///
/// <para>
/// This is not hypothetical. PR #2328 added
/// <c>&lt;ProjectReference Include="..\..\domain\BotNexus.Domain\BotNexus.Domain.csproj" /&gt;</c> to
/// <c>BlazorClient.Core</c> for a genuinely good reason (share the canonical
/// <c>ConversationSource</c>/<c>ConversationKind</c> enums instead of hand-mirroring them, because a
/// duplicated enum silently drifts). It was justified as safe on the grounds that
/// <c>BotNexus.Domain</c>'s only package reference is the <c>Vogen</c> compile-time source
/// generator. The "not the gateway host graph" half of that reasoning was correct; the
/// "compile-time only" half was not, because <c>Vogen</c> was declared as a bare
/// <c>&lt;PackageReference&gt;</c> with no <c>PrivateAssets</c>/<c>ExcludeAssets</c> and so flowed
/// transitively as a normal runtime reference, putting <c>Vogen.SharedTypes.dll</c> into the
/// browser download.
/// </para>
///
/// <para>
/// <b>Correction to the issue's proposal 2.</b> #2329 proposed fixing this by marking <c>Vogen</c>
/// <c>PrivateAssets="all"</c> on the grounds that "it is a source generator; no consumer needs it
/// at runtime". That is <b>false for this codebase</b> and was verified empirically rather than
/// assumed: Vogen ships a real runtime type, <c>Vogen.ValueObjectValidationException</c>, which
/// <c>BotNexus.Domain</c> itself catches in <c>CitizenId.TryParse</c>, and which
/// <c>BotNexus.Domain.Tests</c>, <c>BotNexus.Gateway.Tests</c>, and
/// <c>BotNexus.Gateway.Webhooks.Tests</c> reference by name. Applying
/// <c>PrivateAssets="all"</c> produced 11 compile errors across those projects and removed
/// <c>Vogen.SharedTypes.dll</c> from <c>BotNexus.Gateway.Api</c>'s output, i.e. it would have
/// faulted a live catch clause on the server to shrink a client payload. The correct boundary is
/// the one enforced here: <c>BlazorClient.Core</c> must not reference <c>BotNexus.Domain</c> at
/// all, so Vogen never reaches the browser while remaining fully available on the server.
/// </para>
///
/// <para>
/// The fence is deliberately layered, because a direct-reference allowlist alone would have missed
/// exactly the leak above (the offender was two hops away and came in as a NuGet asset):
/// </para>
/// <list type="number">
///   <item><description>
///     <b>Static graph fence</b> - walk the transitive <c>ProjectReference</c> closure of each WASM
///     entry point from the csproj XML and assert every project in it is on an explicit allowlist
///     with a written justification.
///   </description></item>
///   <item><description>
///     <b>Static package fence</b> - for every project in that closure, assert every
///     runtime-flowing <c>PackageReference</c> (i.e. one not neutralised by
///     <c>PrivateAssets="all"</c> / <c>ExcludeAssets="runtime;..."</c>) is on an explicit
///     allowlist. This is the layer that catches the #2328 class of leak <i>statically</i>, and it
///     names the offending package and the project that dragged it in.
///   </description></item>
///   <item><description>
///     <b>Build-output closure fence</b> - scan the WASM app's actual build output for
///     non-framework, non-BotNexus-client assemblies. This is the backstop that cannot be reasoned
///     around: whatever NuGet and the SDK actually decided to emit is what the browser downloads.
///   </description></item>
/// </list>
///
/// <para>
/// House style follows <c>ExtensionManagedDependencyClosureArchitectureTests</c> and
/// <c>ConversationCreationSeamArchitectureTests</c>: a static scan, offenders named in the failure
/// message, and explicit vacuity guards so the fence cannot rot into a test that can never fail.
/// </para>
/// </summary>
public sealed class WasmPayloadDependencyArchitectureTests : ArchitectureTest
{
    /// <summary>
    /// The Blazor WebAssembly entry-point projects, relative to <c>src/extensions</c>. Anything
    /// reachable from these is downloaded by the browser.
    /// </summary>
    private static readonly string[] WasmEntryPoints =
    [
        "BotNexus.Extensions.Channels.SignalR.BlazorClient",
        "BotNexus.Extensions.Channels.SignalR.BlazorClient.Mobile",
    ];

    /// <summary>
    /// Projects permitted inside the WASM transitive project-reference closure. EVERY entry must
    /// carry a written justification - adding a project here is a deliberate decision to grow the
    /// browser download for every user.
    /// </summary>
    private static readonly Dictionary<string, string> AllowedProjectsInWasmClosure = new(StringComparer.OrdinalIgnoreCase)
    {
        // The two WASM entry points themselves.
        ["BotNexus.Extensions.Channels.SignalR.BlazorClient"] =
            "The desktop WASM entry point itself.",
        ["BotNexus.Extensions.Channels.SignalR.BlazorClient.Mobile"] =
            "The mobile WASM entry point itself.",

        // The single shared library.
        ["BotNexus.Extensions.Channels.SignalR.BlazorClient.Core"] =
            "Shared services, contracts, and state models for both Blazor clients. This is the ONLY " +
            "shared library in the payload. Its one permitted project reference is " +
            "BotNexus.Domain.Wire (below) - the sanctioned shape from #2329 proposal 3. It must not " +
            "gain any other project reference: if the client needs a server-owned contract type, " +
            "move that type into the zero-dependency wire assembly rather than referencing the " +
            "server assembly.",

        // The zero-dependency wire assembly, added by #2300/#2308.
        ["BotNexus.Domain.Wire"] =
            "Enum-only, dependency-free wire contracts (ConversationEnums) shared by the gateway and " +
            "both Blazor WASM clients. This is #2329 proposal 3 realised: the clients need the " +
            "SINGLE canonical declaration of the conversation wire enums, and referencing " +
            "BotNexus.Domain to get them would drag Vogen.SharedTypes into the browser payload. " +
            "Cost is one tiny assembly with no transitive closure. It MUST stay dependency-free - " +
            "its csproj carries the same prohibition, and adding a PackageReference or " +
            "ProjectReference there would silently widen the payload for every user.",
    };

    /// <summary>
    /// NuGet packages permitted to flow as RUNTIME assets into the WASM payload. A package absent
    /// from this list either must not be referenced from the WASM closure at all, or must be
    /// neutralised with <c>PrivateAssets="all"</c> (correct for source generators and analysers).
    /// </summary>
    private static readonly Dictionary<string, string> AllowedRuntimePackagesInWasmClosure = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Microsoft.AspNetCore.Components.WebAssembly"] =
            "The Blazor WebAssembly runtime. Non-negotiable - it IS the client.",
        ["Microsoft.AspNetCore.SignalR.Client"] =
            "The SignalR transport the client uses to talk to the gateway. Non-negotiable - it is " +
            "the client's only channel to the server.",
    };

    /// <summary>
    /// Assembly-name prefixes that belong to the .NET / ASP.NET Core framework or to the allowed
    /// packages above, and are therefore expected in the WASM output. Everything else in the output
    /// is a leak.
    /// </summary>
    private static readonly string[] FrameworkAssemblyPrefixes =
    [
        "System.",
        "System,",
        "Microsoft.AspNetCore.",
        "Microsoft.Extensions.",
        "Microsoft.JSInterop",
        "Microsoft.DotNet.",
        "Microsoft.VisualBasic",
        "Microsoft.Win32.",
        "Microsoft.CSharp",
        "Microsoft.NET.",
    ];

    private static readonly string[] FrameworkAssemblyExactNames =
    [
        "System",
        "mscorlib",
        "netstandard",
        "WindowsBase",
        "Microsoft.CSharp",
    ];


    private string ExtensionsRoot => Path.Combine(Repository.Root, "src", "extensions");

    [Fact]
    public void WasmEntryPoints_Exist()
    {
        foreach (var entry in WasmEntryPoints)
        {
            var csproj = Path.Combine(ExtensionsRoot, entry, entry + ".csproj");
            File.Exists(csproj).ShouldBeTrue(
                $"WASM entry point project not found at {csproj}. If a Blazor WebAssembly project " +
                "was renamed or removed, update WasmEntryPoints in this fence - do not let the " +
                "fence silently stop guarding a live payload.");

            var sdk = (XDocument.Load(csproj).Root?.Attribute("Sdk")?.Value) ?? string.Empty;
            sdk.ShouldBe("Microsoft.NET.Sdk.BlazorWebAssembly",
                $"{entry} is listed as a WASM entry point but no longer uses the BlazorWebAssembly " +
                "SDK. Re-check whether this fence still points at the right projects.");
        }
    }

    [Fact]
    public void WasmProjectClosure_ContainsOnlyAllowlistedProjects()
    {
        var offenders = new List<string>();

        foreach (var entry in WasmEntryPoints)
        {
            var root = Path.Combine(ExtensionsRoot, entry, entry + ".csproj");
            foreach (var reached in TransitiveProjectClosure(root))
            {
                if (!AllowedProjectsInWasmClosure.ContainsKey(reached.Name))
                {
                    offenders.Add($"{reached.Name} (reached from {entry} via {reached.Via})");
                }
            }
        }

        offenders.ShouldBeEmpty(
            "The following project(s) are reachable from a Blazor WebAssembly entry point but are " +
            "NOT on the WASM payload allowlist. Every assembly referenced by a WASM app is " +
            "downloaded by the browser, so each of these grows first-load time for every user. " +
            "Either remove the reference, or extract the small piece you actually need into a " +
            "zero-dependency assembly, or - if the reference is genuinely warranted - add it to " +
            "AllowedProjectsInWasmClosure WITH a written justification. See #2329.\nOffenders: " +
            string.Join("; ", offenders));
    }

    [Fact]
    public void WasmProjectClosure_ContainsOnlyAllowlistedRuntimePackages()
    {
        var offenders = new List<string>();

        foreach (var entry in WasmEntryPoints)
        {
            var root = Path.Combine(ExtensionsRoot, entry, entry + ".csproj");

            var projects = new List<string> { root };
            projects.AddRange(TransitiveProjectClosure(root).Select(p => p.Path));

            foreach (var csproj in projects.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var owner = Path.GetFileNameWithoutExtension(csproj);
                foreach (var package in RuntimeFlowingPackageReferences(File.ReadAllText(csproj)))
                {
                    if (!AllowedRuntimePackagesInWasmClosure.ContainsKey(package))
                    {
                        offenders.Add($"{package} (runtime asset of {owner}, in the {entry} payload)");
                    }
                }
            }
        }

        offenders.ShouldBeEmpty(
            "The following NuGet package(s) flow as RUNTIME assets into a Blazor WebAssembly " +
            "payload without being on the allowlist. This is exactly how Vogen.SharedTypes.dll " +
            "reached the browser in PR #2328: a source generator declared as a bare " +
            "<PackageReference> is NOT compile-time only - it flows transitively like any other " +
            "reference. If the package is a source generator or analyser, mark it " +
            "PrivateAssets=\"all\". Otherwise remove it, or add it to " +
            "AllowedRuntimePackagesInWasmClosure WITH a written justification. See #2329.\nOffenders: " +
            string.Join("; ", offenders));
    }

    [SkippableFact]
    public void WasmBuildOutput_ContainsNoNonFrameworkLeakedAssemblies()
    {
        var binRoots = WasmEntryPoints
            .Select(entry => Path.Combine(ExtensionsRoot, entry, "bin"))
            .ToList();

        var scan = ScanWasmBuildOutput(binRoots, dll => Path.GetRelativePath(Repository.Root, dll));

        // #2707: a checkout in which no WASM entry point has ever been built has nothing for this
        // fence to inspect. That is a fact about the machine, not about the source, so it must not
        // be reported as a violated invariant - it SKIPS, naming the missing directories so the
        // skip is visible in test output and a permanently-skipping fence cannot masquerade as a
        // passing one.
        Skip.If(
            scan.State == WasmBuildOutputScanState.NoBuildOutput,
            "SKIPPED (#2707): no Blazor WebAssembly build output exists in this checkout, so there " +
            "is nothing to scan. This is not a payload violation - it means the WASM projects have " +
            "not been built here. Run 'dotnet build dirs.proj' and re-run to exercise this " +
            "fence. Missing output directories: " + string.Join("; ", scan.MissingBinRoots));

        // Anti-vacuity guard, PRESERVED (#2707 criterion 5) and now aimed at the case it was
        // actually for: build output was expected - a bin directory exists - yet it holds no
        // managed assemblies. That is a genuine anomaly, not a fresh checkout.
        (scan.AssembliesScanned > 0).ShouldBeTrue(
            "Blazor WebAssembly build output directories exist but contain no assemblies to scan. " +
            "A payload fence that scans nothing guards nothing. This is NOT the fresh-checkout " +
            "case (that skips explicitly); output was expected here and is missing or empty, which " +
            "points at a broken or partial build. See #2329, #2707.\nDirectories present but " +
            "empty of assemblies: " + string.Join("; ", scan.ScannedBinRoots));

        scan.Offenders.ShouldBeEmpty(
            "The following assemblies are present in Blazor WebAssembly build output but are " +
            "neither .NET framework assemblies nor the BotNexus client assemblies. The browser " +
            "downloads every one of them. This is the transitive backstop: it catches leaks the " +
            "static csproj fences cannot see (a package that pulls another package, or an SDK " +
            "decision to copy an asset). Track each one back to the reference that introduced it " +
            "and remove or neutralise it. See #2329.\n" + StalenessScopeStatement + "\nOffenders: " +
            string.Join("; ", scan.Offenders));
    }

    /// <summary>
    /// The fence's stated position on staleness (#2707 acceptance criterion 4). It is carried in
    /// the failure text so nobody can read a green from this fence as a claim that the payload on
    /// disk corresponds to the current commit.
    /// </summary>
    public const string StalenessScopeStatement =
        "Scope note (#2707): this fence inspects whatever build output is on disk. It CANNOT " +
        "distinguish current output from stale output left by an earlier commit - detecting " +
        "staleness is explicitly out of scope, and keeping the artifact current is the build's " +
        "responsibility, not this scan's. Run a build before trusting a green from this fence.";

    /// <summary>
    /// Scans the given WASM <c>bin</c> roots for managed assemblies and classifies every one it
    /// finds. Pure with respect to its inputs - the verdict is a function of what is on disk under
    /// <paramref name="binRoots"/> and nothing else - which is what makes the fence give the same
    /// answer in a fresh worktree and a previously-built checkout (#2707 criterion 1).
    /// </summary>
    /// <param name="binRoots">Candidate build-output roots, one per WASM entry point.</param>
    /// <param name="relativise">
    /// Formats an absolute dll path for display in offender messages.
    /// </param>
    public static WasmBuildOutputScanResult ScanWasmBuildOutput(
        IEnumerable<string> binRoots,
        Func<string, string> relativise)
    {
        ArgumentNullException.ThrowIfNull(binRoots);
        ArgumentNullException.ThrowIfNull(relativise);

        var offenders = new List<string>();
        var present = new List<string>();
        var missing = new List<string>();
        var scanned = 0;

        foreach (var binRoot in binRoots)
        {
            if (!Directory.Exists(binRoot))
            {
                missing.Add(binRoot);
                continue;
            }

            present.Add(binRoot);

            foreach (var dll in Directory.GetFiles(binRoot, "*.dll", SearchOption.AllDirectories))
            {
                scanned++;
                var name = Path.GetFileNameWithoutExtension(dll);
                if (IsExpectedWasmOutputAssembly(name))
                {
                    continue;
                }

                offenders.Add($"{name}.dll ({relativise(dll)})");
            }
        }

        var state = present.Count == 0
            ? WasmBuildOutputScanState.NoBuildOutput
            : WasmBuildOutputScanState.Scanned;

        return new WasmBuildOutputScanResult(
            state,
            scanned,
            offenders.Distinct(StringComparer.Ordinal).ToList(),
            present,
            missing);
    }

    // ---- vacuity guards: the fence must be able to fail ----

    [Fact]
    public void Fence_IsNotVacuous_DetectsBarePackageReferenceAsRuntimeFlowing()
    {
        // This is verbatim the shape of BotNexus.Domain.csproj that leaked Vogen.SharedTypes.dll.
        const string leaky = """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PackageReference Include="Vogen" />
              </ItemGroup>
            </Project>
            """;

        RuntimeFlowingPackageReferences(leaky).ShouldContain("Vogen",
            "Vacuity guard: a bare <PackageReference> with no PrivateAssets MUST be detected as " +
            "flowing at runtime. If this fails, the package fence passes vacuously.");
        AllowedRuntimePackagesInWasmClosure.ShouldNotContainKey("Vogen",
            "Vacuity guard: Vogen must never be allowlisted as a runtime asset of the WASM payload. " +
            "Note it IS legitimately a runtime dependency on the SERVER (CitizenId.TryParse catches " +
            "Vogen.ValueObjectValidationException) - which is precisely why the fix is to keep " +
            "BotNexus.Domain out of the client closure, not to neutralise the package globally.");
    }

    [Fact]
    public void Fence_PositivePin_TreatsPrivateAssetsAllAsNotRuntimeFlowing()
    {
        const string attributeForm = """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PackageReference Include="Vogen" PrivateAssets="all" />
              </ItemGroup>
            </Project>
            """;
        const string elementForm = """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PackageReference Include="Vogen">
                  <PrivateAssets>all</PrivateAssets>
                </PackageReference>
              </ItemGroup>
            </Project>
            """;
        const string excludeRuntimeForm = """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PackageReference Include="Vogen" ExcludeAssets="runtime;native" />
              </ItemGroup>
            </Project>
            """;

        RuntimeFlowingPackageReferences(attributeForm).ShouldBeEmpty(
            "Positive pin: PrivateAssets=\"all\" as an attribute must neutralise the reference.");
        RuntimeFlowingPackageReferences(elementForm).ShouldBeEmpty(
            "Positive pin: <PrivateAssets>all</PrivateAssets> as a child element must neutralise " +
            "the reference.");
        RuntimeFlowingPackageReferences(excludeRuntimeForm).ShouldBeEmpty(
            "Positive pin: ExcludeAssets containing 'runtime' must neutralise the reference.");
    }

    [Fact]
    public void Fence_IsNotVacuous_ClassifiesALeakedAssemblyNameAsUnexpected()
    {
        IsExpectedWasmOutputAssembly("Vogen.SharedTypes").ShouldBeFalse(
            "Vacuity guard: the assembly that actually leaked in #2328 must be classified as " +
            "unexpected output. If this fails, the output fence passes vacuously.");
        IsExpectedWasmOutputAssembly("BotNexus.Domain").ShouldBeFalse(
            "Vacuity guard: a server-side BotNexus assembly must be classified as unexpected " +
            "output - the fence must not blanket-allow everything named BotNexus.*.");
        IsExpectedWasmOutputAssembly("BotNexus.Gateway").ShouldBeFalse(
            "Vacuity guard: the gateway host assembly must be classified as unexpected output.");

        IsExpectedWasmOutputAssembly("System.Text.Json").ShouldBeTrue(
            "Positive pin: framework assemblies must be accepted.");
        IsExpectedWasmOutputAssembly("Microsoft.AspNetCore.SignalR.Client").ShouldBeTrue(
            "Positive pin: the allowlisted SignalR client must be accepted.");
        IsExpectedWasmOutputAssembly("BotNexus.Extensions.Channels.SignalR.BlazorClient.Core").ShouldBeTrue(
            "Positive pin: the shared client library must be accepted.");
        IsExpectedWasmOutputAssembly("BotNexus.Domain.Wire").ShouldBeTrue(
            "Positive pin: the zero-dependency wire assembly is a sanctioned payload member (#2345). " +
            "Note this is deliberately NOT prefix-based - BotNexus.Domain itself is still rejected " +
            "above, so the allowance cannot widen into the server graph.");
    }

    /// <summary>
    /// The wire assembly is only an acceptable payload member because it has no transitive closure.
    /// Allowlisting it (#2345) removes the closure fence's objection, so this test supplies the
    /// replacement guarantee: if anyone adds a reference to BotNexus.Domain.Wire, the browser payload
    /// silently grows for every user and the allowlist justification becomes a lie.
    /// </summary>
    [Fact]
    public void DomainWire_StaysDependencyFree()
    {
        const string WireProject = "BotNexus.Domain.Wire";

        // Vacuity pin: this test exists solely to police the BotNexus.Domain.Wire allowlist entry.
        // If that entry is ever removed, this test must be removed with it rather than left quietly
        // passing and guarding nothing.
        AllowedProjectsInWasmClosure.ShouldContainKey(
            WireProject,
            $"{WireProject} is no longer allowlisted, so this guard has nothing to police. Remove " +
            "this test alongside the allowlist entry - do not leave it passing vacuously.");

        var csproj = Path.Combine(Repository.Root, "src", "domain", WireProject, WireProject + ".csproj");
        File.Exists(csproj).ShouldBeTrue(
            $"BotNexus.Domain.Wire project not found at {csproj}. It is allowlisted into the WASM " +
            "payload on the strict condition that it stays dependency-free; if it moved or was " +
            "renamed, update this fence and its allowlist entry together.");

        var document = XDocument.Load(csproj);
        var references = document
            .Descendants()
            .Where(element =>
                element.Name.LocalName is "PackageReference" or "ProjectReference")
            .Select(element => element.Attribute("Include")?.Value ?? "(no Include)")
            .ToList();

        references.ShouldBeEmpty(
            "BotNexus.Domain.Wire must stay dependency-free. It is downloaded by every browser as " +
            "part of the Blazor WASM payload, and it is allowlisted (#2345) only because it has an " +
            "empty transitive closure. A reference here drags its whole graph into the payload. Put " +
            "the type in BotNexus.Domain instead. See #2329.\nReferences found: " +
            string.Join("; ", references));
    }

    [Fact]
    public void EveryAllowlistEntry_CarriesAWrittenJustification()
    {
        foreach (var (project, justification) in AllowedProjectsInWasmClosure)
        {
            justification.Length.ShouldBeGreaterThan(
                30,
                $"Allowlist entry '{project}' has no meaningful written justification. Every entry " +
                "in the WASM payload allowlist costs every user download time and must say why it " +
                "is worth it (#2329 acceptance criterion).");
        }

        foreach (var (package, justification) in AllowedRuntimePackagesInWasmClosure)
        {
            justification.Length.ShouldBeGreaterThan(
                30,
                $"Allowlist entry '{package}' has no meaningful written justification (#2329).");
        }
    }

    // ---- helpers ----

    /// <summary>
    /// Walks the transitive <c>ProjectReference</c> closure of <paramref name="rootCsproj"/> from
    /// the csproj XML (no MSBuild evaluation, no build required). Returns each reached project
    /// together with the immediate parent that referenced it, so failures can name the path.
    /// </summary>
    private static List<(string Name, string Path, string Via)> TransitiveProjectClosure(string rootCsproj)
    {
        var results = new List<(string, string, string)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<string>();
        queue.Enqueue(Path.GetFullPath(rootCsproj));
        seen.Add(Path.GetFullPath(rootCsproj));

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            var currentName = Path.GetFileNameWithoutExtension(current);
            var currentDir = Path.GetDirectoryName(current)!;

            foreach (var include in ProjectReferenceIncludes(File.ReadAllText(current)))
            {
                var resolved = Path.GetFullPath(Path.Combine(currentDir, include.Replace('\\', Path.DirectorySeparatorChar)));
                if (!seen.Add(resolved))
                {
                    continue;
                }

                results.Add((Path.GetFileNameWithoutExtension(resolved), resolved, currentName));

                if (File.Exists(resolved))
                {
                    queue.Enqueue(resolved);
                }
            }
        }

        return results.Select(r => (r.Item1, r.Item2, r.Item3)).ToList();
    }

    private static IEnumerable<string> ProjectReferenceIncludes(string csprojXml)
    {
        foreach (Match match in Regex.Matches(
                     csprojXml,
                     @"<ProjectReference\b[^>]*\bInclude\s*=\s*""(?<inc>[^""]+)""",
                     RegexOptions.IgnoreCase))
        {
            yield return match.Groups["inc"].Value;
        }
    }

    /// <summary>
    /// Returns the <c>PackageReference</c> names in <paramref name="csprojXml"/> that flow as
    /// runtime assets - i.e. those NOT neutralised by <c>PrivateAssets="all"</c> (attribute or
    /// child element) or by an <c>ExcludeAssets</c> that excludes <c>runtime</c> / <c>all</c>.
    /// </summary>
    private static IReadOnlyList<string> RuntimeFlowingPackageReferences(string csprojXml)
    {
        var flowing = new List<string>();

        // Match either the self-closing form or the element form with children, capturing both the
        // attributes on the open tag and any child metadata.
        foreach (Match match in Regex.Matches(
                     csprojXml,
                     @"<PackageReference\b(?<attrs>[^>]*?)(?:/>|>(?<body>.*?)</PackageReference\s*>)",
                     RegexOptions.IgnoreCase | RegexOptions.Singleline))
        {
            var attrs = match.Groups["attrs"].Value;
            var body = match.Groups["body"].Success ? match.Groups["body"].Value : string.Empty;

            var includeMatch = Regex.Match(attrs, @"\bInclude\s*=\s*""(?<inc>[^""]+)""", RegexOptions.IgnoreCase);
            if (!includeMatch.Success)
            {
                continue;
            }

            var combined = attrs + body;

            if (Regex.IsMatch(combined, @"PrivateAssets\s*(?:=\s*""|>\s*)[^""<]*\ball\b", RegexOptions.IgnoreCase))
            {
                continue;
            }

            if (Regex.IsMatch(combined, @"ExcludeAssets\s*(?:=\s*""|>\s*)[^""<]*\b(runtime|all)\b", RegexOptions.IgnoreCase))
            {
                continue;
            }

            flowing.Add(includeMatch.Groups["inc"].Value);
        }

        return flowing;
    }

    private static bool IsExpectedWasmOutputAssembly(string assemblyName)
    {
        if (FrameworkAssemblyExactNames.Contains(assemblyName, StringComparer.Ordinal))
        {
            return true;
        }

        if (FrameworkAssemblyPrefixes.Any(p => assemblyName.StartsWith(p, StringComparison.Ordinal)))
        {
            return true;
        }

        // The BotNexus client assemblies themselves - and ONLY those, explicitly enumerated. A
        // blanket "BotNexus.*" allowance would let the gateway host graph in unnoticed, which is
        // the exact regression this fence exists to stop.
        return AllowedProjectsInWasmClosure.ContainsKey(assemblyName);
    }

}

/// <summary>
/// Whether the WASM build-output fence had anything to inspect (#2707). The two cases the original
/// code conflated: nothing was ever produced here (honest skip) versus output was produced and was
/// empty (real anti-vacuity failure).
/// </summary>
public enum WasmBuildOutputScanState
{
    /// <summary>
    /// No WASM entry point has a build-output directory in this checkout. Nothing was produced to
    /// inspect, so the fence skips with an explicit reason rather than failing.
    /// </summary>
    NoBuildOutput,

    /// <summary>
    /// At least one build-output directory exists and was walked. The anti-vacuity guard applies:
    /// output was expected, so finding zero assemblies is a genuine failure.
    /// </summary>
    Scanned,
}

/// <summary>
/// The outcome of scanning the Blazor WebAssembly build output (#2707).
/// </summary>
/// <param name="State">Whether there was any build output to inspect at all.</param>
/// <param name="AssembliesScanned">How many managed assemblies were classified.</param>
/// <param name="Offenders">Assemblies that belong to neither the framework nor the client payload.</param>
/// <param name="ScannedBinRoots">Build-output roots that existed and were walked.</param>
/// <param name="MissingBinRoots">
/// Build-output roots that did not exist. Named in the skip message so the skip states which
/// artifact is missing.
/// </param>
public sealed record WasmBuildOutputScanResult(
    WasmBuildOutputScanState State,
    int AssembliesScanned,
    IReadOnlyList<string> Offenders,
    IReadOnlyList<string> ScannedBinRoots,
    IReadOnlyList<string> MissingBinRoots);
