using System.Text.RegularExpressions;
using Shouldly;

namespace BotNexus.Architecture.Tests;

/// <summary>
/// Architecture fitness function enforcing the issue #2310 single-creation-seam contract:
/// <c>new Conversation { ... }</c> may appear in EXACTLY ONE production type,
/// <c>ConversationFactory</c>, plus <c>ConversationRowMapper</c> for persistence hydration.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why.</b> Before this fence, conversation creation was scattered across eight independent call
/// sites in five assemblies, each hand-stamping <c>Source</c> and <c>Kind</c>. Every new provenance
/// field had to be threaded to all eight, and the failure mode was silent: a missed site does not
/// error, it takes the enum default (<c>ConversationSource.Channel</c>) and produces a conversation
/// that lies about its own origin. Nothing failed the build. This test is that missing build failure.
/// </para>
/// <para>
/// <b>Allowlist rationale.</b> <c>ConversationFactory</c> is the seam itself. <c>ConversationRowMapper</c>
/// is not creation at all - it rehydrates a conversation that already exists in the store, and its
/// provenance is read off the row rather than chosen; routing it through the factory would mean
/// re-stamping <c>CreatedAt</c> and inventing a <c>Source</c> the row already carries. Tests are
/// exempt: fixtures legitimately construct arbitrary conversation shapes (including deliberately
/// invalid ones) to exercise store and mapper behaviour.
/// </para>
/// <para>
/// Fence shape mirrors <see cref="ViewSelectionSingleWriterArchitectureTests"/> and
/// <see cref="AgentKindArchitectureTests"/>: (a) the real-source scan, (b) an anti-vacuity self-test
/// proving the sole permitted site actually contains the construction, (c) regex self-tests proving
/// the pattern catches the canonical violation and does not fire on legitimate lookalikes.
/// </para>
/// </remarks>
public sealed class ConversationCreationSeamArchitectureTests
{
    /// <summary>The single production type permitted to construct a <c>Conversation</c>.</summary>
    private const string SeamFileName = "ConversationFactory.cs";

    /// <summary>
    /// Production files allowed to contain a <c>Conversation</c> object-initializer construction.
    /// Every entry needs a justification; adding one without a reason is the same class of drift the
    /// fence exists to prevent. Compared by file name (repo-unique).
    /// </summary>
    private static readonly (string FileName, string Reason)[] s_allowlist =
    {
        (SeamFileName,
            "The creation seam itself (#2310). This is the one place a Conversation is constructed."),

        ("ConversationRowMapper.cs",
            "Persistence hydration, not creation: rebuilds a Conversation that already exists in the " +
            "store, reading Source/Kind/CreatedAt off the row rather than choosing them."),
    };

    [Fact]
    public void Conversation_IsConstructedInExactlyOneProductionType()
    {
        var srcDir = SrcDir();

        var violations = new List<string>();
        foreach (var file in Directory.EnumerateFiles(srcDir, "*.cs", SearchOption.AllDirectories))
        {
            var name = Path.GetFileName(file);
            if (s_allowlist.Any(a => a.FileName.Equals(name, StringComparison.OrdinalIgnoreCase)))
                continue;

            var stripped = StripComments(File.ReadAllText(file));
            if (ConstructsConversation(stripped))
                violations.Add($"  {Path.GetRelativePath(srcDir, file)}");
        }

        violations.ShouldBeEmpty(
            $"A Conversation may only be constructed in {SeamFileName} (the creation seam) and " +
            "ConversationRowMapper.cs (persistence hydration). Every other origin path must call an " +
            "intent-revealing ConversationFactory.CreateForChannel / CreateForCron / CreateForWebhook / " +
            "CreateForAgent factory, so provenance (Source, Kind) is chosen by which factory you call " +
            "and cannot be silently omitted (#2310). A raw `new Conversation { ... }` that forgets " +
            "Source does not error - it takes the enum default and produces a conversation that lies " +
            "about its own origin. That is exactly what this fence stops.\n" +
            "Violations:\n" + string.Join("\n", violations));
    }

    [Fact]
    public void Seam_ActuallyConstructsAConversation()
    {
        // Anti-vacuity: if the seam stopped containing the construction (renamed, moved, deleted),
        // the scan above would pass trivially because nobody constructs a Conversation anywhere.
        var seam = Directory
            .EnumerateFiles(SrcDir(), SeamFileName, SearchOption.AllDirectories)
            .FirstOrDefault();

        seam.ShouldNotBeNull($"Expected the conversation creation seam file {SeamFileName} under src/.");
        ConstructsConversation(StripComments(File.ReadAllText(seam)))
            .ShouldBeTrue($"{SeamFileName} must itself construct a Conversation - it is the sole creation seam (#2310).");
    }

    [Fact]
    public void Seam_ExposesAFactoryForEveryConversationSource()
    {
        // The seam's value is that provenance is picked by WHICH factory you call. If a
        // ConversationSource value has no corresponding factory, callers on that origin path have
        // nowhere to go and will be tempted back to a raw constructor.
        var seam = Directory
            .EnumerateFiles(SrcDir(), SeamFileName, SearchOption.AllDirectories)
            .FirstOrDefault();
        seam.ShouldNotBeNull($"Expected the conversation creation seam file {SeamFileName} under src/.");

        var source = StripComments(File.ReadAllText(seam));
        foreach (var value in Enum.GetNames<BotNexus.Gateway.Abstractions.Models.ConversationSource>())
        {
            Regex.IsMatch(source, $@"\bCreateFor{value}\s*\(")
                .ShouldBeTrue(
                    $"ConversationSource.{value} has no CreateFor{value} factory on the seam. Every " +
                    "origin must have an intent-revealing entry point, otherwise that path has no " +
                    "sanctioned way to mint a conversation (#2310).");
        }
    }

    [Fact]
    public void Regex_IsNotVacuous_AgainstSyntheticViolation()
    {
        const string violation = """
            var conversation = new Conversation
            {
                ConversationId = ConversationId.Create(),
                AgentId = agentId
            };
            """;
        ConstructsConversation(violation).ShouldBeTrue(
            "Vacuity guard: the fence regex must match a raw `new Conversation { ... }` construction.");

        const string fullyQualified = "new BotNexus.Gateway.Abstractions.Models.Conversation\n{\n    AgentId = a\n};";
        ConstructsConversation(fullyQualified).ShouldBeTrue(
            "Vacuity guard: the fence regex must also match a fully-qualified construction, which is " +
            "how WebhookInboundController historically wrote it.");
    }

    [Fact]
    public void Regex_DoesNotFalsePositive_OnLookalikes()
    {
        const string clean = """
            var conversation = ConversationFactory.CreateForCron(id, agentId);
            var summary = new ConversationSummary(id, agentId);
            var dto = new ConversationResponse(id);
            var ids = new List<ConversationId>();
            var routing = new ConversationRoutingResult(conversation);
            """;
        ConstructsConversation(clean).ShouldBeFalse(
            "False-positive guard: factory calls and other Conversation-prefixed types (summaries, " +
            "DTOs, routing results) must not trip the seam fence.");
    }

    /// <summary>
    /// True when the source constructs a <c>Conversation</c> (optionally fully qualified). Requires the
    /// type name to be followed by a word boundary that is NOT another identifier character, so
    /// <c>ConversationSummary</c>, <c>ConversationId</c> and friends do not match.
    /// </summary>
    private static bool ConstructsConversation(string source)
        => Regex.IsMatch(source, @"\bnew\s+(?:[A-Za-z_][\w]*\s*\.\s*)*Conversation(?![\w])");

    private static string StripComments(string source)
    {
        var noBlock = Regex.Replace(source, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);
        return Regex.Replace(noBlock, @"//[^\r\n]*", string.Empty);
    }

    private static string SrcDir()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "Directory.Packages.props")))
            current = current.Parent;
        current.ShouldNotBeNull("Could not locate repo root from " + AppContext.BaseDirectory);

        var dir = Path.Combine(current.FullName, "src");
        Directory.Exists(dir).ShouldBeTrue("Expected source dir at " + dir);
        return dir;
    }
}
