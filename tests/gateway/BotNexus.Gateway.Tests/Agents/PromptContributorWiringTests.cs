using System.IO.Abstractions.TestingHelpers;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Agents;
using BotNexus.Gateway.Configuration;
using BotNexus.Gateway.Contracts.Memory;
using BotNexus.Gateway.Prompts;
using BotNexus.Gateway.Security;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;

namespace BotNexus.Gateway.Tests.Agents;

/// <summary>
/// A registered <see cref="IPromptContributor"/> must actually reach the assembled system prompt
/// (#3667, acceptance criterion 2).
/// </summary>
/// <remarks>
/// <para>
/// Every assertion here goes <b>through the composer</b> - <see cref="SystemPromptBuilder.Build"/>
/// and <see cref="WorkspaceContextBuilder"/> - never through <see cref="PromptPipeline"/> directly.
/// That distinction is the whole point of this file: <c>PromptPipeline</c> already honoured
/// contributors correctly in isolation before #3667, and <c>PromptPrimitivesTests</c> already
/// covered it. A pipeline-level test would have passed on the broken tree and proved nothing. The
/// defect was that the sole production composer never called <c>AddContributors</c>, so only a
/// composer-level assertion can detect it or its regression.
/// </para>
/// </remarks>
public sealed class PromptContributorWiringTests
{
    private const string ContributionMarker = "CONTRIBUTED-BY-EXTENSION-3667";

    /// <summary>
    /// The direct-composer assertion: hand the builder a contributor and its lines must appear.
    /// </summary>
    [Fact]
    public void RegisteredContributor_ReachesTheAssembledSystemPrompt()
    {
        var prompt = SystemPromptBuilder.Build(BuildParams(
            new TestContributor(500, "Extension Block", [ContributionMarker])));

        prompt.ShouldContain("## Extension Block");
        prompt.ShouldContain(
            ContributionMarker,
            Case.Sensitive,
            "A contributor handed to SystemPromptBuilder must reach the assembled prompt. Before " +
            "#3667 the builder never called PromptPipeline.AddContributors, so this content was " +
            "silently dropped with no error and no log line.");
    }

    /// <summary>
    /// Anti-vacuity for the assertion above: the marker must be absent when no contributor is
    /// supplied, or the test would pass on any prompt containing arbitrary text.
    /// </summary>
    [Fact]
    public void WithNoContributors_TheMarkerIsAbsentAndThePromptStillBuilds()
    {
        var prompt = SystemPromptBuilder.Build(BuildParams());

        prompt.ShouldNotContain(ContributionMarker);
        prompt.ShouldNotBeNullOrWhiteSpace(
            "Supplying no contributors must remain a no-op, not an empty prompt.");
    }

    /// <summary>
    /// Contributor ordering is honoured against the builder's own section order keys, not merely
    /// appended at the end. <c>Runtime</c> is the last built-in section (order 240), so a
    /// contributor at 5 must precede it and one at 9000 must follow it.
    /// </summary>
    [Fact]
    public void ContributorPriority_OrdersAgainstBuiltInSections()
    {
        var prompt = SystemPromptBuilder.Build(BuildParams(
            new TestContributor(5, "Early", ["EARLY-3667"]),
            new TestContributor(9000, "Late", ["LATE-3667"])));

        var early = prompt.IndexOf("EARLY-3667", StringComparison.Ordinal);
        var runtime = prompt.IndexOf("<runtime>", StringComparison.Ordinal);
        var late = prompt.IndexOf("LATE-3667", StringComparison.Ordinal);

        early.ShouldBeGreaterThanOrEqualTo(0);
        runtime.ShouldBeGreaterThanOrEqualTo(0, "Sanity: the runtime block must be present.");
        late.ShouldBeGreaterThanOrEqualTo(0);

        early.ShouldBeLessThan(runtime, "A contributor priced below every section key renders first.");
        late.ShouldBeGreaterThan(runtime, "A contributor priced above every section key renders last.");
    }

    /// <summary>
    /// A contributor whose <c>ShouldInclude</c> returns false contributes nothing. This proves the
    /// wiring hands the real <see cref="PromptContext"/> through rather than short-circuiting it.
    /// </summary>
    [Fact]
    public void ContributorThatOptsOut_ContributesNothing()
    {
        var prompt = SystemPromptBuilder.Build(BuildParams(
            new TestContributor(500, "Excluded", [ContributionMarker], include: false)));

        prompt.ShouldNotContain(ContributionMarker);
    }

    /// <summary>
    /// The end-to-end assertion: a contributor registered in a real DI container reaches the prompt
    /// through <see cref="WorkspaceContextBuilder"/>, which is the production collection site. This
    /// is the clause-2 proof - the previous tests show the builder honours contributors it is
    /// given, this one shows something actually gives them to it.
    /// </summary>
    [Fact]
    public async Task ContributorRegisteredInDi_ReachesThePromptThroughWorkspaceContextBuilder()
    {
        var workspacePath = Path.Combine(Path.GetTempPath(), "bn-3667-" + Guid.NewGuid().ToString("N"), "workspace");
        var homePath = Path.Combine(Path.GetTempPath(), "bn-3667-home-" + Guid.NewGuid().ToString("N"));
        var fileSystem = new MockFileSystem();
        fileSystem.Directory.CreateDirectory(workspacePath);
        fileSystem.Directory.CreateDirectory(homePath);

        var workspaceManager = new MemoryInjectionToolPolicyTests.StubWorkspaceManager(workspacePath);

        var services = new ServiceCollection();
        services.AddSingleton<IPromptContributor>(
            new TestContributor(500, "Extension Block", [ContributionMarker]));
        using var provider = services.BuildServiceProvider();

        // Resolve the contract from a real container, exactly as the composition root does. The
        // point under test is that WorkspaceContextBuilder accepts the resolved set and threads it
        // to the builder - not that ServiceCollection can round-trip a singleton.
        var builder = new WorkspaceContextBuilder(
            workspaceManager,
            fileSystem,
            new BotNexusHome(fileSystem, homePath),
            Substitute.For<IConversationStore>(),
            Substitute.For<ISessionStore>(),
            new NotSupportedMemoryFactory(),
            new DefaultToolPolicyProvider(
                new StaticOptionsMonitor<PlatformConfig>(new PlatformConfig()),
                NullLogger<DefaultToolPolicyProvider>.Instance),
            provider.GetServices<IPromptContributor>());

        var prompt = await builder.BuildSystemPromptAsync(
            new AgentDescriptor
            {
                AgentId = AgentId.From("contributor-agent"),
                DisplayName = "Contributor Agent",
                ModelId = "test-model",
                ApiProvider = "test-provider"
            },
            executionContext: null);

        prompt.ShouldContain(
            ContributionMarker,
            Case.Sensitive,
            "An IPromptContributor resolved from the container must reach the prompt assembled by " +
            "WorkspaceContextBuilder. This is the production path #3667 wired; without the " +
            "AddContributors call it is silently dropped.");
    }

    /// <summary>
    /// The composition root must actually SELECT the contributor-aware constructor. Everything
    /// above proves the code path works when the constructor is used; this proves the container
    /// uses it. A wiring reachable only by a constructor DI never picks is the same silent
    /// no-contribution defect in a new place.
    /// </summary>
    [Fact]
    public void ContainerConstruction_SelectsTheContributorAwareConstructor()
    {
        var contributorConstructors = typeof(WorkspaceContextBuilder)
            .GetConstructors()
            .Where(c => c.GetParameters()
                .Any(p => p.ParameterType == typeof(IEnumerable<IPromptContributor>)))
            .ToList();

        contributorConstructors.Count.ShouldBe(
            1,
            "Exactly one constructor takes the contributor set. More than one makes DI's " +
            "greediest-satisfiable choice ambiguous.");

        var contributorCtor = contributorConstructors[0];
        var widest = typeof(WorkspaceContextBuilder)
            .GetConstructors()
            .OrderByDescending(c => c.GetParameters().Length)
            .First();

        widest.ShouldBe(
            contributorCtor,
            "Microsoft.Extensions.DependencyInjection selects the constructor with the most " +
            "parameters it can satisfy. IEnumerable<T> is always satisfiable, so the contributor " +
            "constructor must remain the widest or the container will silently choose a narrower " +
            "one and contributors will stop reaching the prompt - exactly the #3667 defect.");
    }

    private static SystemPromptParams BuildParams(params IPromptContributor[] contributors) => new()
    {
        WorkspaceDir = Path.Combine(Path.GetTempPath(), "bn-3667-workspace"),
        ToolNames = ["read", "write", "shell"],
        PromptMode = PromptMode.Full,
        Runtime = new RuntimeInfo
        {
            AgentId = "test-agent",
            Channel = "signalr"
        },
        PromptContributors = contributors
    };

    private sealed class TestContributor(
        int priority,
        string heading,
        IReadOnlyList<string> lines,
        bool include = true) : IPromptContributor
    {
        public PromptSection? Target => null;

        public int Priority => priority;

        public bool ShouldInclude(PromptContext context) => include;

        public PromptContribution GetContribution(PromptContext context) => new()
        {
            SectionHeading = heading,
            Lines = lines
        };
    }
}

/// <summary>
/// Memory is irrelevant to contributor wiring. Throwing <see cref="NotSupportedException"/> is the
/// documented "provider not registered" signal that <c>WorkspaceContextBuilder</c> already handles
/// by falling through to file-based loading, so this keeps SQLite and the memory stack entirely
/// out of a test about prompt composition.
/// </summary>
file sealed class NotSupportedMemoryFactory : IAgentMemoryFactory
{
    public IAgentMemory Create(string agentId, string? providerName = null)
        => throw new NotSupportedException("No memory provider is registered for this test.");

    public IReadOnlyList<string> GetRegisteredProviders() => [];
}

file sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
{
    public T CurrentValue => value;

    public T Get(string? name) => value;

    public IDisposable? OnChange(Action<T, string?> listener) => null;
}
