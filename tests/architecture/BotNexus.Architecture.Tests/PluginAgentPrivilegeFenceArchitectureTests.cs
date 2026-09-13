using System.Reflection;
using System.Text.RegularExpressions;
using BotNexus.Extensions.Plugins.Agents;
using BotNexus.Gateway.Abstractions.Models;
using Shouldly;

namespace BotNexus.Architecture.Tests;

/// <summary>
/// Architecture fitness function for <c>#2685</c> clause 4: the plugin agent privilege fence is
/// expressed <b>structurally over the descriptor member set</b>, so a newly added settable member
/// of <see cref="AgentDescriptor"/> is fenced by default rather than silently permitted.
/// <para>
/// <b>Why structural and not an allow-list of forbidden names.</b> A plugin-shipped agent
/// descriptor arrives from a marketplace, so every member it can populate is an attack surface.
/// A deny-list names today's dangerous members; the next member added to the descriptor is not on
/// it, and is therefore permitted the moment it exists - silently, with no error and no log. The
/// fence inverts that: the fence classifies members as <i>declarable</i> or <i>narrowed</i>
/// explicitly, and everything else - including anything added tomorrow - is fenced. The failure
/// mode of forgetting becomes "the new member is rejected", not "the new member is granted".
/// </para>
/// <para>
/// Shape borrowed deliberately from <c>AgentDescriptorFingerprintFenceArchitectureTests</c>
/// (<c>#2588</c>), which solved the same class of problem for change detection: reflect over the
/// settable public instance properties of <see cref="AgentDescriptor"/> - the ones a configuration
/// source can populate - and assert a structural property of the code that consumes them.
/// </para>
/// <para>
/// <b>Vacuity.</b> A fence that reflects over nothing is green. Every assertion is preceded by a
/// guard that the scan found its subject, and the source-level assertions are pinned against the
/// real file so a moved or renamed fence fails loudly instead of silently guarding nothing.
/// </para>
/// </summary>
public sealed class PluginAgentPrivilegeFenceArchitectureTests : ArchitectureTest
{
    private const string FenceSource =
        "src/extensions/BotNexus.Extensions.Plugins/Agents/PluginAgentDescriptorFence.cs";

    /// <summary>
    /// The members a plugin-shipped descriptor may populate, and the reason each is safe. These
    /// are identity, presentation and model-selection members: none of them grants the agent
    /// access to anything the installing user does not already have.
    /// <para>
    /// This list is the ONLY thing standing between a member and the fence. Adding a name here is
    /// a deliberate security decision and shows up as a diff in a security-relevant file - which
    /// is exactly the review the structural default forces.
    /// </para>
    /// </summary>
    private static readonly IReadOnlySet<string> ExpectedDeclarable = new HashSet<string>(StringComparer.Ordinal)
    {
        // Identity and presentation - no capability implied.
        nameof(AgentDescriptor.DisplayName),
        nameof(AgentDescriptor.Emoji),
        nameof(AgentDescriptor.Description),
        nameof(AgentDescriptor.Summary),
        nameof(AgentDescriptor.Order),
        // Model selection - constrained downstream by the provider/model registry, which is
        // configured by the installing user, not by the plugin.
        nameof(AgentDescriptor.ModelId),
        nameof(AgentDescriptor.ApiProvider),
        nameof(AgentDescriptor.AllowedModelIds),
        nameof(AgentDescriptor.CacheRetentionMode),
        nameof(AgentDescriptor.Thinking),
        nameof(AgentDescriptor.ContextWindow),
        // Prompt content - text the agent reads, not a grant.
        nameof(AgentDescriptor.SystemPrompt),
        nameof(AgentDescriptor.SystemPromptFile),
        nameof(AgentDescriptor.SystemPromptFiles),
        // Tool ids name tools that must already be registered by the host; an unknown id resolves
        // to nothing. Declaring one cannot conjure a capability the host has not installed.
        nameof(AgentDescriptor.ToolIds),
        // Behavioural knobs with no privilege dimension.
        nameof(AgentDescriptor.MaxConcurrentSessions),
        nameof(AgentDescriptor.Metadata),
        nameof(AgentDescriptor.Memory),
        nameof(AgentDescriptor.Soul),
        nameof(AgentDescriptor.Heartbeat),
        nameof(AgentDescriptor.DateTimeInjection),
        nameof(AgentDescriptor.ConversationRetention),
    };

    /// <summary>
    /// Members a plugin descriptor may declare but which are <b>clamped</b> to the installing
    /// user's own ceiling rather than rejected (<c>#2685</c> clause 3). Only file access behaves
    /// this way: it is the one member whose declaration is meaningful at reduced scope.
    /// </summary>
    private static readonly IReadOnlySet<string> ExpectedNarrowed = new HashSet<string>(StringComparer.Ordinal)
    {
        nameof(AgentDescriptor.FileAccess),
    };

    /// <summary>
    /// The members a configuration source can populate: public, instance, settable properties
    /// declared on <see cref="AgentDescriptor"/>. Settability is the right discriminator - a
    /// get-only member is by construction derived from one of these and cannot be declared.
    /// </summary>
    private static IReadOnlyList<string> SettableDescriptorMembers =>
        typeof(AgentDescriptor)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(p => p.SetMethod is not null && p.SetMethod.IsPublic)
            .Select(p => p.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

    /// <summary>
    /// Anti-vacuity: the fence's subject must exist and be locatable. If the fence type is moved
    /// or renamed, every assertion below must fail loudly rather than scan an empty string.
    /// </summary>
    [Fact]
    public void Fence_Subject_Exists_AndIsLocatable()
    {
        var path = ResolvePath(FenceSource);
        File.Exists(path).ShouldBeTrue(
            $"{FenceSource} not found at {path}. The #2685 fence is anchored to that file; if the "
            + "plugin agent fence moved, update FenceSource here so this test keeps guarding it "
            + "rather than silently guarding nothing.");

        var text = File.ReadAllText(path);
        text.Contains("DeclarableMembers", StringComparison.Ordinal).ShouldBeTrue(
            "the fence must expose its declarable-member set by that name; the structural "
            + "assertions below read it.");
    }

    /// <summary>
    /// The primary structural fence. Every settable descriptor member is either explicitly
    /// declarable, explicitly narrowed, or fenced. There is no fourth outcome, and the fence's
    /// own classification must agree with the reviewed sets pinned in this test - so adding a
    /// member to <see cref="AgentDescriptor"/> and quietly adding it to the plugin fence's
    /// declarable set fails here until the security decision is reviewed in this file too.
    /// </summary>
    [Fact]
    public void EverySettableDescriptorMember_IsClassified_WithDenyAsTheDefault()
    {
        var settable = SettableDescriptorMembers;

        // Anti-vacuity: AgentDescriptor has well over twenty settable members today.
        settable.Count.ShouldBeGreaterThan(
            20,
            $"Reflection over AgentDescriptor's settable public properties returned "
            + $"{settable.Count} members, which is implausibly few. The fence is vacuous - fix the "
            + "reflection query before trusting a green result.");

        var declarable = PluginAgentDescriptorFence.DeclarableMembers;
        var narrowed = PluginAgentDescriptorFence.NarrowedMembers;
        var fenced = PluginAgentDescriptorFence.FencedMembers;

        declarable.ShouldNotBeEmpty("a fence classifying nothing as declarable is vacuous.");
        fenced.ShouldNotBeEmpty(
            "a fence with an empty fenced set permits everything, which is the exact failure "
            + "clause 4 exists to prevent.");

        // 1. The three sets partition the settable member set - no member is unclassified, and
        //    none is classified twice.
        var union = declarable.Concat(narrowed).Concat(fenced).ToArray();
        union.Length.ShouldBe(
            union.Distinct(StringComparer.Ordinal).Count(),
            "a descriptor member appears in more than one fence classification. Declarable, "
            + "narrowed and fenced must be disjoint or the effective behaviour depends on "
            + "evaluation order.");

        var classified = union.ToHashSet(StringComparer.Ordinal);
        var unclassified = settable.Where(m => !classified.Contains(m)).ToArray();
        unclassified.ShouldBeEmpty(
            "AgentDescriptor declares settable members that PluginAgentDescriptorFence does not "
            + "classify. An unclassified member is fenced by the implementation's structural "
            + "default - which is correct and safe - but leaving it unclassified means the "
            + "security decision was never made. Classify each member below as declarable, "
            + "narrowed, or fenced, and mirror the decision in ExpectedDeclarable/"
            + "ExpectedNarrowed in this test.\nUnclassified members:\n  "
            + string.Join("\n  ", unclassified));

        // 2. The implementation's declarable set must match the reviewed set in THIS file. This
        //    is what makes the fence a review gate rather than a mirror of itself: widening the
        //    privilege surface requires editing a test whose whole purpose is to be read.
        declarable.OrderBy(n => n, StringComparer.Ordinal).ShouldBe(
            ExpectedDeclarable.OrderBy(n => n, StringComparer.Ordinal),
            "PluginAgentDescriptorFence.DeclarableMembers diverged from the reviewed set pinned "
            + "in this architecture test. A plugin-shipped agent descriptor comes from a "
            + "marketplace, so every member it may populate is a privilege decision. If the "
            + "change is intended, update ExpectedDeclarable here WITH the reason the member "
            + "cannot escalate.");

        narrowed.OrderBy(n => n, StringComparer.Ordinal).ShouldBe(
            ExpectedNarrowed.OrderBy(n => n, StringComparer.Ordinal),
            "PluginAgentDescriptorFence.NarrowedMembers diverged from the reviewed set. Narrowing "
            + "means the member is accepted at reduced scope rather than rejected; adding one is "
            + "a deliberate weakening of the reject-by-default posture.");

        // 3. The fenced set is the COMPLEMENT, computed - not enumerated. This is the structural
        //    property clause 4 demands: fenced membership is derived from "not explicitly
        //    permitted", so a new settable member joins it automatically.
        var expectedFenced = settable
            .Where(m => !ExpectedDeclarable.Contains(m) && !ExpectedNarrowed.Contains(m))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();
        fenced.OrderBy(n => n, StringComparer.Ordinal).ToList().ShouldBe(
            expectedFenced,
            "the fenced set must be the computed complement of the permitted sets over the live "
            + "descriptor member set. A hand-enumerated fenced list is an allow-list of forbidden "
            + "names wearing a different hat, and does not satisfy #2685 clause 4.");
    }

    /// <summary>
    /// The deny-by-default property stated directly: a member the fence has never heard of is
    /// fenced. This is asserted against a synthetic name that cannot exist on the descriptor, so
    /// it holds regardless of what <see cref="AgentDescriptor"/> declares today.
    /// </summary>
    [Fact]
    public void AnUnknownMember_IsFenced_NotPermitted()
    {
        var declarable = PluginAgentDescriptorFence.DeclarableMembers;
        var narrowed = PluginAgentDescriptorFence.NarrowedMembers;

        const string hypothetical = "SomeMemberAddedTomorrow";
        declarable.ShouldNotContain(
            hypothetical,
            "an unknown member must never be reported as declarable.");
        narrowed.ShouldNotContain(hypothetical);

        PluginAgentDescriptorFence.IsDeclarable(hypothetical).ShouldBeFalse(
            "a member the fence does not recognise must be fenced. If this fails, the fence's "
            + "default is permit and a member added to AgentDescriptor tomorrow becomes a "
            + "plugin-declarable privilege surface the moment it exists (#2685 clause 4).");
    }

    /// <summary>
    /// Source-level guard: the fenced set must be <i>computed</i>, not spelled out. A future
    /// refactor that replaces the complement with a literal collection of names reintroduces the
    /// allow-list the issue explicitly rejects, and would still satisfy the reflection assertions
    /// above on the day it was written.
    /// </summary>
    [Fact]
    public void FencedSet_IsComputedFromTheDescriptor_NotHandEnumerated()
    {
        var text = File.ReadAllText(ResolvePath(FenceSource));

        Regex.IsMatch(text, @"typeof\s*\(\s*AgentDescriptor\s*\)").ShouldBeTrue(
            "the fence must reflect over AgentDescriptor to derive its member set. Without that "
            + "reflection the classification cannot track members added later, which is the whole "
            + "of clause 4.");

        Regex.IsMatch(text, @"GetProperties\s*\(").ShouldBeTrue(
            "the fence must enumerate the descriptor's properties rather than name them.");

        Regex.IsMatch(text, @"SetMethod").ShouldBeTrue(
            "the fence must discriminate on settability - a settable member is one a "
            + "configuration source can populate, and therefore one a plugin could declare.");
    }

    private string ResolvePath(string relative) =>
        Path.Combine(Repository.Root, relative.Replace('/', Path.DirectorySeparatorChar));
}
