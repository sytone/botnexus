using System.IO.Abstractions.TestingHelpers;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Extensions;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Security;
using BotNexus.Gateway.Contracts.Memory;
using BotNexus.Gateway.Extensions;
using BotNexus.Gateway.Search;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;

namespace BotNexus.Gateway.Tests.Search;

public sealed class BuiltInSearchContributorTests
{
    [Fact]
    public async Task MemoryContributor_UsesNativeSearchAndPreservesScoreTrustTargetAndBounds()
    {
        var registry = Substitute.For<IAgentRegistry>();
        registry.GetAll().Returns([
            Descriptor("alpha", memoryEnabled: true),
            Descriptor("disabled", memoryEnabled: false)
        ]);

        var memory = Substitute.For<IAgentMemory>();
        memory.SearchAsync(Arg.Any<AgentMemorySearchRequest>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var request = call.Arg<AgentMemorySearchRequest>();
                request.AgentId.ShouldBe("alpha");
                request.Query.ShouldBe("needle");
                request.TopK.ShouldBe(2);
                return Task.FromResult<IReadOnlyList<AgentMemorySearchResult>>([
                    new("entry/1", "first matching memory", "manual", null, DateTimeOffset.Parse("2026-09-24T12:00:00Z"), 0.9)
                    {
                        TrustTier = "trusted"
                    },
                    new("entry-2", "second matching memory", "conversation", "session-1", DateTimeOffset.Parse("2026-09-23T12:00:00Z"), 0.7)
                    {
                        TrustTier = "unknown"
                    },
                    new("entry-3", "provider over-return", "manual", null, DateTimeOffset.Parse("2026-09-22T12:00:00Z"), 0.6)
                    {
                        TrustTier = "trusted"
                    }
                ]);
            });

        var factory = Substitute.For<IAgentMemoryFactory>();
        factory.Create("alpha").Returns(memory);
        var contributor = new MemorySearchContributor(registry, factory);
        using var cancellation = new CancellationTokenSource();

        var results = await contributor.SearchAsync(new SearchRequest("needle", 2), cancellation.Token);

        contributor.SourceId.ShouldBe("memory");
        contributor.IsAvailable.ShouldBeTrue();
        contributor.CanAssessProvenanceTrust.ShouldBeTrue();
        results.Count.ShouldBe(2);
        results[0].Relevance.ShouldBe(0.9);
        results[0].ProvenanceTrust.ShouldBe(SearchProvenanceTrust.Trusted);
        results[0].Target.ShouldBe("/memory/alpha/entries/entry%2F1");
        results[1].ProvenanceTrust.ShouldBe(SearchProvenanceTrust.Untrusted);
        await memory.Received(1).SearchAsync(Arg.Any<AgentMemorySearchRequest>(), cancellation.Token);
        factory.DidNotReceive().Create("disabled");
    }

    [Fact]
    public async Task MemoryContributor_PropagatesCancellation()
    {
        var registry = Substitute.For<IAgentRegistry>();
        registry.GetAll().Returns([Descriptor("alpha", memoryEnabled: true)]);
        var memory = Substitute.For<IAgentMemory>();
        memory.SearchAsync(Arg.Any<AgentMemorySearchRequest>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromCanceled<IReadOnlyList<AgentMemorySearchResult>>(call.Arg<CancellationToken>()));
        var factory = Substitute.For<IAgentMemoryFactory>();
        factory.Create("alpha").Returns(memory);
        var contributor = new MemorySearchContributor(registry, factory);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Should.ThrowAsync<OperationCanceledException>(() =>
            contributor.SearchAsync(new SearchRequest("needle", 5), cancellation.Token));
    }

    [Fact]
    public async Task FileContributor_MatchesContentAndFileNameWithinBoundAndSkipsDeniedFiles()
    {
        const string workspace = "/agents/alpha/workspace";
        var fileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
        {
            [$"{workspace}/notes/allowed.txt"] = new("first line\nneedle in allowed content\nlast line"),
            [$"{workspace}/needle-name.md"] = new("filename-only match"),
            [$"{workspace}/private/denied.txt"] = new("needle must not escape policy"),
            [$"{workspace}/other.txt"] = new("nothing relevant")
        }, workspace);
        fileSystem.File.SetLastWriteTimeUtc($"{workspace}/notes/allowed.txt", DateTime.Parse("2026-09-24T10:00:00Z").ToUniversalTime());

        var registry = Substitute.For<IAgentRegistry>();
        registry.GetAll().Returns([Descriptor("alpha", deniedPaths: [$"{workspace}/private/**"])]);
        var workspaces = Substitute.For<IAgentWorkspaceManager>();
        workspaces.GetWorkspacePath("alpha").Returns(workspace);
        var contributor = new FileSearchContributor(registry, workspaces, fileSystem);

        var results = await contributor.SearchAsync(new SearchRequest("needle", 2));

        contributor.SourceId.ShouldBe("files");
        contributor.IsAvailable.ShouldBeTrue();
        contributor.CanAssessProvenanceTrust.ShouldBeFalse();
        results.Count.ShouldBe(2);
        results.ShouldAllBe(result => result.ProvenanceTrust == SearchProvenanceTrust.Untrusted);
        results.ShouldContain(result => result.Snippet.Contains("needle in allowed content", StringComparison.Ordinal));
        results.ShouldContain(result => result.Title == "needle-name.md");
        results.ShouldNotContain(result => result.Target.Contains("denied.txt", StringComparison.Ordinal));
        results.ShouldAllBe(result => result.Snippet.Length <= FileSearchContributor.MaxSnippetLength);
    }

    [Fact]
    public async Task FileContributor_ReturnsNoResultsWhenWorkspaceIsUnavailable()
    {
        var registry = Substitute.For<IAgentRegistry>();
        registry.GetAll().Returns([Descriptor("alpha")]);
        var workspaces = Substitute.For<IAgentWorkspaceManager>();
        workspaces.GetWorkspacePath("alpha").Returns("/agents/alpha/workspace");
        var contributor = new FileSearchContributor(registry, workspaces, new MockFileSystem());

        contributor.IsAvailable.ShouldBeFalse();
        (await contributor.SearchAsync(new SearchRequest("needle", 5))).ShouldBeEmpty();
    }

    [Fact]
    public void GatewayRegistration_ExposesBuiltInsThroughSharedContributorContract()
    {
        var services = new ServiceCollection();
        services.AddBotNexusGateway();

        var contributorTypes = services
            .Where(descriptor => descriptor.ServiceType == typeof(ISearchContributor))
            .Select(descriptor => descriptor.ImplementationType)
            .ToList();

        contributorTypes.ShouldContain(typeof(MemorySearchContributor));
        contributorTypes.ShouldContain(typeof(FileSearchContributor));
    }

    private static AgentDescriptor Descriptor(
        string id,
        bool memoryEnabled = false,
        IReadOnlyList<string>? deniedPaths = null)
        => new()
        {
            AgentId = AgentId.From(id),
            DisplayName = id,
            ModelId = "test-model",
            ApiProvider = "test-provider",
            Memory = memoryEnabled ? new MemoryAgentConfig { Enabled = true } : null,
            FileAccess = deniedPaths is null ? null : new FileAccessPolicy { DeniedPaths = deniedPaths }
        };
}
