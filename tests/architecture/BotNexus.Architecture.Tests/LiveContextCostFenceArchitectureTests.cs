using System.Text.RegularExpressions;
using BotNexus.Gateway.Sessions;
using Shouldly;

namespace BotNexus.Architecture.Tests;

/// <summary>
/// Architecture fitness functions enforcing the #3536 invariant: <b>selecting an entry for the live
/// LLM context and costing it are one decision</b>.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="SessionContextProjector.IsVisibleInLiveContext"/> correctly selected Tool entries -
/// which a continuous session really does send - while the compaction estimator sized them by
/// <c>entry.Content</c> alone. <c>SessionEntry.ToolArgs</c> turned out to be 69% of the visible
/// context on the motivating session: the estimator reported ~403,891 tokens where ~1,290,071 were
/// present (a 3.19x undercount), and the #1599 bloat trigger costed a 27,354-character tool-start
/// row at zero because its <c>Content</c> was empty.
/// </para>
/// <para>
/// The defect was not the arithmetic - it was that the cost rule lived inline at each call site, so
/// a field added to <c>SessionEntry</c> could reach the provider while remaining free in every
/// estimate. These fences make that shape impossible to reintroduce silently.
/// </para>
/// </remarks>
public sealed class LiveContextCostFenceArchitectureTests : ArchitectureTest
{
    /// <summary>
    /// The cost helper must sit in the same type as the visibility predicate. If a future refactor
    /// moves one without the other, the pairing this issue exists to enforce is gone.
    /// </summary>
    [Fact]
    public void CostHelper_LivesBesideTheVisibilityPredicate()
    {
        var costMethod = typeof(SessionContextProjector).GetMethod(nameof(SessionContextProjector.GetLiveContextCharCost));
        var predicate = typeof(SessionContextProjector).GetMethod(nameof(SessionContextProjector.IsVisibleInLiveContext));

        costMethod.ShouldNotBeNull(
            "GetLiveContextCharCost is the single definition of what a visible entry costs. " +
            "Removing or renaming it means each call site decides again, which is exactly the #3536 defect.");
        predicate.ShouldNotBeNull();
        costMethod!.DeclaringType.ShouldBe(predicate!.DeclaringType,
            "Selection and cost must be defined together so neither can drift from the other.");
    }

    /// <summary>
    /// The cost helper must account for every <c>SessionEntry</c> string field that is serialised
    /// into the provider message list. This is the fence that actually fails when someone adds a new
    /// payload-bearing field and forgets to charge for it.
    /// </summary>
    /// <remarks>
    /// Deliberately asserts on the SOURCE of the helper rather than on a computed value: the failure
    /// mode is an omitted term, and only reading the expression can detect a term that was never
    /// written. A behavioural test cannot distinguish "field not counted" from "field happened to be
    /// empty in the fixture" - which is precisely how this survived unnoticed.
    /// </remarks>
    [Fact]
    public void CostHelper_CountsEveryPayloadBearingField()
    {
        var source = File.ReadAllText(
            Repository.Path("src", "gateway", "BotNexus.Gateway.Sessions", "SessionContextProjector.cs"));

        var body = ExtractMethodBody(source, "GetLiveContextCharCost");

        foreach (var field in new[] { "Content", "ToolArgs", "ThinkingContent" })
        {
            body.Contains(field, StringComparison.Ordinal).ShouldBeTrue(
                $"GetLiveContextCharCost must charge for SessionEntry.{field} - it is sent to the " +
                "provider on a continuous session. Omitting it silently under-counts context and " +
                "lets a session exhaust its window without ever tripping the compaction threshold (#3536).");
        }
    }

    /// <summary>
    /// No file under <c>src/gateway/</c> outside the allowlist may size a session entry by reading
    /// <c>Content?.Length</c> inside an aggregation. That is the exact inline shape that produced the
    /// 3.19x undercount; the sanctioned form is <c>GetLiveContextCharCost</c>.
    /// </summary>
    [Fact]
    public void NoInlineContentOnlySizing_OutsideAllowlist()
    {
        var gatewayRoot = Repository.Path("src", "gateway");
        var allowlist = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            // The canonical cost definition itself.
            "SessionContextProjector.cs",
            // Sizes skill markdown and memory notes, not session entries - a different unit with its
            // own budget. Named explicitly so the exemption is a decision, not an oversight.
            "SkillResolver.cs",
            "SkillsCommandContributor.cs",
            "MemoryPromptBudget.cs",
            "MemoryDreamingCronAction.cs"
        };

        // An aggregation over entries that reads only Content.Length - e.g.
        //   .Sum(entry => (long)(entry.Content?.Length ?? 0))
        var inlineSizing = new Regex(
            @"(Sum|Aggregate)\s*\(\s*\w+\s*=>.*?\.Content\s*\??\s*\.\s*Length",
            RegexOptions.Compiled | RegexOptions.Singleline);

        var offenders = new List<string>();
        var scanned = 0;

        foreach (var file in Directory.EnumerateFiles(gatewayRoot, "*.cs", SearchOption.AllDirectories))
        {
            if (allowlist.Contains(Path.GetFileName(file)))
            {
                continue;
            }

            scanned++;
            var text = File.ReadAllText(file);
            if (inlineSizing.IsMatch(text))
            {
                offenders.Add(Path.GetRelativePath(gatewayRoot, file));
            }
        }

        // Anti-vacuity: a broken enumeration must fail loudly rather than pass green on zero files.
        scanned.ShouldBeGreaterThan(100,
            "The sweep inspected implausibly few files - the enumeration is probably broken, " +
            "which would make this fence silently useless.");

        offenders.ShouldBeEmpty(
            "These files size session entries by Content alone. Use " +
            "SessionContextProjector.GetLiveContextCharCost so ToolArgs and ThinkingContent are " +
            "charged for too (#3536): " + string.Join(", ", offenders));
    }

    private static string ExtractMethodBody(string source, string methodName)
    {
        var start = source.IndexOf(methodName, StringComparison.Ordinal);
        start.ShouldBeGreaterThan(-1, $"Could not locate {methodName} in SessionContextProjector.cs.");

        var open = source.IndexOf('{', start);
        open.ShouldBeGreaterThan(-1);

        var depth = 0;
        for (var i = open; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return source[open..(i + 1)];
                }
            }
        }

        throw new InvalidOperationException($"Unbalanced braces while reading {methodName}.");
    }
}
