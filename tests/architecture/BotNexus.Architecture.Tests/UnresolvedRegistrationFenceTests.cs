using System.Text.RegularExpressions;
using Shouldly;

namespace BotNexus.Architecture.Tests;

/// <summary>
/// Reports service registrations that no source consumes (#3511).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this class of dead code is invisible.</b> An unresolved DI registration compiles, the
/// container builds, the graph is valid, and every test passes. Nothing anywhere reports it. That is
/// how the configuration project accumulated an inheritance engine with no callers, a shadow harness
/// writing a report nothing read, a hosted service that was never registered, a feature gate that was
/// never called, and a <c>NoOpConfigStoreRoundTrip</c> that existed only to satisfy a constructor -
/// roughly 3,000 lines removed across #3508 and #3510, none of which any test had flagged.
/// </para>
/// <para>
/// <b>"No consumer" is NOT "delete it".</b> This fence cannot tell dead code from a seam that is
/// waiting for the feature it was built for. <c>ICitizenRegistry</c> is the worked example: it has no
/// consumer today because the agents that will populate it are future work, and deleting it on this
/// fence's say-so was wrong. A registration with no consumer is a QUESTION - "is this waiting, or is
/// it abandoned?" - and only a human who knows the roadmap can answer it.
/// </para>
/// <para>
/// So the allow-list carries two distinct kinds of entry, and the distinction is the point:
/// <see cref="FrameworkResolved"/> for contracts whose consumer is real but invisible to a source
/// scan, and <see cref="AwaitingConsumers"/> for seams deliberately registered ahead of their
/// feature. Both are legitimate; conflating them would let a genuinely abandoned registration hide
/// behind either label.
/// </para>
/// </remarks>
public sealed class UnresolvedRegistrationFenceTests : ArchitectureTest
{
    /// <summary>
    /// Contracts resolved by a framework rather than by BotNexus code. Each entry claims the
    /// consumer EXISTS and names why a source scan cannot see it.
    /// </summary>
    private static readonly Dictionary<string, string> FrameworkResolved = new(StringComparer.Ordinal)
    {
        ["IAuthorizationHandler"] = "resolved by ASP.NET Core's authorization middleware, not by BotNexus code",
        ["IHostedService"] = "resolved by the generic host",
        ["IConfigureOptions"] = "resolved by the options infrastructure",
        ["IPostConfigureOptions"] = "resolved by the options infrastructure",
        ["IValidateOptions"] = "resolved by the options infrastructure",
        ["ILoggerProvider"] = "enumerated by the logging factory; providers are registered to be collected, never injected",
        ["IUserIdProvider"] = "SignalR's own contract; the hub infrastructure resolves it to map connections to users",
    };

    /// <summary>
    /// Seams registered ahead of the feature that will consume them. Each entry claims the
    /// registration is INTENTIONAL and names the work it is waiting for.
    /// </summary>
    /// <remarks>
    /// An entry here is a statement about the roadmap, not about the code, so it can only be added by
    /// someone who knows the plan. That is deliberate: it makes "this is future work" an explicit,
    /// reviewable claim rather than something inferred from a reference count.
    /// </remarks>
    private static readonly Dictionary<string, string> AwaitingConsumers = new(StringComparer.Ordinal)
    {
        ["ICitizenRegistry"] = "citizens seam; to be populated with the agents (Jon, 2026-08-21)",
        ["IAgentIdentityResolver"] = "conversation agent-identity hydration seam, referenced by the P9-H design notes in SqliteSessionStore",
    };

    private static readonly Regex Registration =
        new(@"(?:TryAdd|Add)(?:Singleton|Scoped|Transient)<\s*(I[A-Z]\w+)\s*,", RegexOptions.Compiled);

    /// <summary>
    /// A type declaration's base list - <c>class Foo : IFoo</c> - which declares an implementation
    /// rather than consuming one.
    /// </summary>
    private static readonly Regex BaseList =
        new(@"^\s*(?:public|internal|private|protected|sealed|abstract|partial|static|\s)*(?:class|record|struct)\s+\w+.*:\s*.*\bI[A-Z]\w+", RegexOptions.Compiled);

    private IEnumerable<string> ConsumerSourceFiles()
    {
        var roots = new[]
        {
            Path.Combine(Repository.SourceRoot, "gateway", "BotNexus.Gateway"),
            Path.Combine(Repository.SourceRoot, "gateway", "BotNexus.Gateway.Api"),
            // The gateway registers services whose consumers live in sibling projects it depends on -
            // ConfigHydrationService consumes IConfigSchemaContributor from here, for example. Scanning
            // only the two registration assemblies reported that as orphaned, which is a false positive
            // of exactly the kind that trains people to allow-list rather than investigate.
            Path.Combine(Repository.SourceRoot, "gateway", "BotNexus.Gateway.Configuration"),
            Path.Combine(Repository.SourceRoot, "gateway", "BotNexus.Gateway.Channels"),
            Path.Combine(Repository.SourceRoot, "gateway", "BotNexus.Gateway.Webhooks"),
            Path.Combine(Repository.SourceRoot, "gateway", "BotNexus.Cron"),
            // Extensions resolve gateway services too - IUserRegistry is injected into the SignalR
            // GatewayHub and nowhere else. Omitting this root reported it as orphaned, which would
            // have deleted a live seam.
            Path.Combine(Repository.SourceRoot, "extensions"),
        };

        foreach (var root in roots.Where(Directory.Exists))
        {
            // .razor as well as .cs: Blazor components inject services with @inject, so a portal
            // service consumed only from markup is invisible to a C#-only scan. Seven live services
            // were reported as orphaned before this was added - allow-listing them instead would have
            // been seven false exemptions papering over one wrong glob.
            foreach (var file in Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories)
                         .Where(f => f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) ||
                                     f.EndsWith(".razor", StringComparison.OrdinalIgnoreCase)))
            {
                var relative = Path.GetRelativePath(Repository.Root, file).Replace('\\', '/');
                if (relative.Contains("/obj/", StringComparison.Ordinal) ||
                    relative.Contains("/bin/", StringComparison.Ordinal))
                    continue;

                yield return file;
            }
        }
    }

    [Fact]
    public void EveryGatewayRegistration_IsConsumedOrDeclared()
    {
        var contents = ConsumerSourceFiles().ToDictionary(f => f, File.ReadAllText, StringComparer.Ordinal);

        var registered = contents.Values
            .SelectMany(text => Registration.Matches(text).Select(m => m.Groups[1].Value))
            .Distinct(StringComparer.Ordinal)
            .Where(i => !FrameworkResolved.ContainsKey(i) && !AwaitingConsumers.ContainsKey(i))
            .ToList();

        var orphans = new List<string>();

        foreach (var iface in registered)
        {
            // A consumer is any mention that is NOT the registration itself, NOT the interface
            // declaration, NOT an implementation's base list, and NOT a comment: a constructor
            // parameter, a field, or a GetService call.
            //
            // Excluding the base list matters. Without it, `class Foo : IFoo` counts as a consumer of
            // IFoo, so an interface with an implementation but no caller looks used - which is the
            // exact shape of orphaned registration this fence exists to find. A mutation restoring
            // the IConfigPathResolver registration survived until this exclusion was added.
            var consumed = contents.Values.Any(text =>
                text.Split('\n').Any(line =>
                    line.Contains(iface, StringComparison.Ordinal) &&
                    !Registration.IsMatch(line) &&
                    !line.Contains($"interface {iface}", StringComparison.Ordinal) &&
                    !BaseList.IsMatch(line) &&
                    !line.TrimStart().StartsWith("//", StringComparison.Ordinal) &&
                    !line.TrimStart().StartsWith("///", StringComparison.Ordinal)));

            if (!consumed)
                orphans.Add(iface);
        }

        orphans.Sort(StringComparer.Ordinal);

        orphans.ShouldBeEmpty(
            "These services are registered but no source consumes them. An unresolved registration " +
            "compiles, builds a valid container, and passes every test - so nothing else will report it.\n" +
            "\n" +
            "This is a QUESTION, not a verdict. Decide which it is:\n" +
            "  - abandoned  -> delete the registration and the type\n" +
            "  - waiting for a feature -> add to AwaitingConsumers with the work it is waiting for\n" +
            "  - resolved by a framework -> add to FrameworkResolved with the reason it is invisible\n" +
            "\n" +
            "Do NOT assume the first. ICitizenRegistry was deleted on this fence's evidence and had to " +
            "be restored: it is a seam waiting for the agents that will populate it.\n" +
            "\n" +
            "Unconsumed:\n  " + string.Join("\n  ", orphans));
    }

    /// <summary>
    /// Non-vacuity: the scan must find real registrations. A regex that matched nothing would make
    /// the assertion above pass trivially and forever.
    /// </summary>
    [Fact]
    public void Fence_FindsRealRegistrations()
    {
        var registered = ConsumerSourceFiles()
            .SelectMany(f => Registration.Matches(File.ReadAllText(f)).Select(m => m.Groups[1].Value))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        registered.Count.ShouldBeGreaterThan(40, "the gateway registers many services");
        registered.ShouldContain("IChannelManager", "a known registration must be detected");
    }

    /// <summary>
    /// Every allow-list entry must carry a non-trivial reason, so an exemption cannot be added as a
    /// silent bypass.
    /// </summary>
    [Fact]
    public void AllowLists_EntriesCarryAReason()
    {
        foreach (var (type, reason) in FrameworkResolved.Concat(AwaitingConsumers))
        {
            reason.Length.ShouldBeGreaterThan(20, $"'{type}' needs a real reason, not a placeholder");
        }
    }

    /// <summary>
    /// The two allow-lists stay disjoint. A type is either framework-resolved or awaiting its
    /// consumers; claiming both would make the distinction meaningless and let an abandoned
    /// registration shelter under whichever label is less scrutinised.
    /// </summary>
    [Fact]
    public void AllowLists_AreDisjoint()
    {
        FrameworkResolved.Keys.Intersect(AwaitingConsumers.Keys, StringComparer.Ordinal)
            .ShouldBeEmpty("a registration cannot be both framework-resolved and awaiting consumers");
    }
}
