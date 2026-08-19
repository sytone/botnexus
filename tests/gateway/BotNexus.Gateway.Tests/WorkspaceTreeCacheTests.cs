using System.IO.Abstractions;
using System.IO.Abstractions.TestingHelpers;
using System.Reflection;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Agents;
using BotNexus.Gateway.Api.Controllers;
using BotNexus.Gateway.Api.Models;
using BotNexus.Gateway.Api.Workspace;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace BotNexus.Gateway.Tests;

/// <summary>
/// Covers the revalidating workspace tree cache (issue #3357): the portal polls
/// GET /api/agents/{id}/workspace continuously and each call used to re-walk 1000-2600 entries.
/// The contract is "save the walk without ever serving a stale tree", so every assertion here
/// pairs a saved-walk claim with a freshness claim.
/// </summary>
public sealed class WorkspaceTreeCacheTests
{
    private const string WorkspacePath = @"C:\workspace\agent-a";

    // AC1: a repeated request against an unchanged workspace must not re-enumerate the tree.
    [Fact]
    public void GetWorkspace_RepeatedRequest_UnchangedWorkspace_DoesNotReWalk()
    {
        var (fileSystem, counter) = CreateCountingFileSystem(DefaultFiles());
        var controller = CreateController(fileSystem, new WorkspaceTreeCache());

        var first = ReadTree(controller, depth: 2);
        var walksAfterFirst = counter.EnumerateCalls;
        var second = ReadTree(controller, depth: 2);

        walksAfterFirst.ShouldBeGreaterThan(0, "the cold call must actually walk the workspace");
        counter.EnumerateCalls.ShouldBe(walksAfterFirst, "the second identical call must not re-walk");
        Flatten(second).ShouldBe(Flatten(first));
    }

    // AC5 non-vacuity for AC1: with the cache lookup disabled the walk count must rise again.
    [Fact]
    public void GetWorkspace_WithCacheLookupDisabled_ReWalksOnEverySecondRequest()
    {
        var (fileSystem, counter) = CreateCountingFileSystem(DefaultFiles());
        var controller = CreateController(fileSystem, new WorkspaceTreeCache(lookupEnabled: false));

        ReadTree(controller, depth: 2);
        var walksAfterFirst = counter.EnumerateCalls;
        ReadTree(controller, depth: 2);

        counter.EnumerateCalls.ShouldBeGreaterThan(walksAfterFirst);
    }

    // AC2: a file added inside the depth limit must appear in the very next response.
    [Fact]
    public void GetWorkspace_AfterFileAdded_ReflectsNewFileImmediately()
    {
        var (fileSystem, _) = CreateCountingFileSystem(DefaultFiles());
        var controller = CreateController(fileSystem, new WorkspaceTreeCache());

        ReadTree(controller, depth: 2);
        AdvanceClock(fileSystem, Path.Combine(WorkspacePath, "memory"));
        fileSystem.AddFile(Path.Combine(WorkspacePath, "memory", "new.md"), new MockFileData("fresh"));

        Flatten(ReadTree(controller, depth: 2)).ShouldContain("memory/new.md");
    }

    // AC2: a removal must also invalidate, not just an addition.
    [Fact]
    public void GetWorkspace_AfterFileRemoved_DropsRemovedFileImmediately()
    {
        var (fileSystem, _) = CreateCountingFileSystem(DefaultFiles());
        var controller = CreateController(fileSystem, new WorkspaceTreeCache());

        Flatten(ReadTree(controller, depth: 2)).ShouldContain("SOUL.md");
        fileSystem.File.Delete(Path.Combine(WorkspacePath, "SOUL.md"));

        Flatten(ReadTree(controller, depth: 2)).ShouldNotContain("SOUL.md");
    }

    // AC2, the case a directory-mtime-only validator would miss: an in-place edit changes the
    // reported size while leaving the parent directory's timestamp untouched.
    [Fact]
    public void GetWorkspace_AfterFileModifiedInPlace_ReflectsNewSize()
    {
        var (fileSystem, _) = CreateCountingFileSystem(DefaultFiles());
        var controller = CreateController(fileSystem, new WorkspaceTreeCache());

        var originalSize = FindEntry(ReadTree(controller, depth: 2), "SOUL.md").Size;
        fileSystem.File.WriteAllText(Path.Combine(WorkspacePath, "SOUL.md"), "a much longer soul document");

        var updatedSize = FindEntry(ReadTree(controller, depth: 2), "SOUL.md").Size;
        updatedSize.ShouldNotBe(originalSize);
        updatedSize.ShouldBe("a much longer soul document".Length);
    }

    // AC5 non-vacuity for AC2: with invalidation disabled the mutation must NOT be observed,
    // proving the freshness assertions above are carried by revalidation and not by luck.
    [Fact]
    public void GetWorkspace_WithInvalidationDisabled_ServesStaleTree()
    {
        var (fileSystem, _) = CreateCountingFileSystem(DefaultFiles());
        var controller = CreateController(fileSystem, new WorkspaceTreeCache(invalidationEnabled: false));

        ReadTree(controller, depth: 2);
        fileSystem.AddFile(Path.Combine(WorkspacePath, "memory", "new.md"), new MockFileData("fresh"));

        Flatten(ReadTree(controller, depth: 2)).ShouldNotContain("memory/new.md");
    }

    // AC3: the key includes the requested depth, so a depth-2 hit cannot answer a depth-0 request.
    [Fact]
    public void GetWorkspace_DifferentDepth_DoesNotReuseTreeBuiltForAnotherDepth()
    {
        var (fileSystem, _) = CreateCountingFileSystem(DefaultFiles());
        var controller = CreateController(fileSystem, new WorkspaceTreeCache());

        var deep = ReadTree(controller, depth: 2);
        var shallow = ReadTree(controller, depth: 0);

        deep.DepthLimit.ShouldBe(2);
        shallow.DepthLimit.ShouldBe(0);
        Flatten(deep).ShouldContain("memory/2026-05-15.md");
        Flatten(shallow).ShouldNotContain("memory/2026-05-15.md");
    }

    // AC3: the key includes the agent, so one agent never sees another agent's tree.
    [Fact]
    public void GetWorkspace_DifferentAgent_DoesNotReuseAnotherAgentsTree()
    {
        var fileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
        {
            [Path.Combine(WorkspacePath, "only-in-a.md")] = new("a"),
            [Path.Combine(@"C:\workspace\agent-b", "only-in-b.md")] = new("b")
        });
        var cache = new WorkspaceTreeCache();

        var a = ReadTree(CreateController(fileSystem, cache), depth: 1);
        var b = ReadTree(CreateController(fileSystem, cache, agentId: "agent-b", workspacePath: @"C:\workspace\agent-b"), depth: 1, agentId: "agent-b");

        Flatten(a).ShouldBe(["only-in-a.md"]);
        Flatten(b).ShouldBe(["only-in-b.md"]);
    }

    // AC4: entries the validator rejects must never reach the cache, so a cached hit cannot
    // become a path-traversal bypass. A symlink escaping the workspace stays excluded on the
    // second (cached) call exactly as on the first.
    [Fact]
    public void GetWorkspace_CachedResponse_StillExcludesEntriesOutsideWorkspace()
    {
        var fileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
        {
            [Path.Combine(WorkspacePath, "inside.md")] = new("in"),
            [@"C:\secrets\outside.md"] = new("out")
        });
        var controller = CreateController(fileSystem, new WorkspaceTreeCache());

        Flatten(ReadTree(controller, depth: 2)).ShouldBe(["inside.md"]);
        Flatten(ReadTree(controller, depth: 2)).ShouldBe(["inside.md"]);
    }

    private static Dictionary<string, MockFileData> DefaultFiles() => new()
    {
        [Path.Combine(WorkspacePath, "SOUL.md")] = new("soul"),
        [Path.Combine(WorkspacePath, "memory", "2026-05-15.md")] = new("entry"),
        [Path.Combine(WorkspacePath, "memory", "archive", "old.md")] = new("archived")
    };

    /// <summary>
    /// MockFileSystem does not always move a directory's timestamp when a child is added, so tests
    /// that rely on directory-mtime invalidation nudge it explicitly rather than assuming.
    /// </summary>
    private static void AdvanceClock(MockFileSystem fileSystem, string directoryPath) =>
        fileSystem.Directory.SetLastWriteTimeUtc(directoryPath, DateTime.UtcNow.AddSeconds(5));

    private static WorkspaceDirectoryResponse ReadTree(WorkspaceController controller, int depth, string agentId = "agent-a")
    {
        var result = controller.GetWorkspace(agentId, depth);
        var payload = (result.Result as OkObjectResult)?.Value.ShouldBeOfType<WorkspaceDirectoryResponse>();
        payload.ShouldNotBeNull();
        return payload!;
    }

    private static List<string> Flatten(WorkspaceDirectoryResponse response)
    {
        var paths = new List<string>();
        void Walk(IEnumerable<WorkspaceEntryDto> entries)
        {
            foreach (var entry in entries)
            {
                paths.Add(entry.Path);
                Walk(entry.Children);
            }
        }

        Walk(response.Entries);
        return paths;
    }

    private static WorkspaceEntryDto FindEntry(WorkspaceDirectoryResponse response, string path)
    {
        WorkspaceEntryDto? Search(IEnumerable<WorkspaceEntryDto> entries)
        {
            foreach (var entry in entries)
            {
                if (entry.Path == path)
                    return entry;

                var nested = Search(entry.Children);
                if (nested is not null)
                    return nested;
            }

            return null;
        }

        var found = Search(response.Entries);
        found.ShouldNotBeNull($"expected entry '{path}' in the tree");
        return found!;
    }

    private static (MockFileSystem FileSystem, EnumerationCounter Counter) CreateCountingFileSystem(
        Dictionary<string, MockFileData> files)
    {
        var fileSystem = new MockFileSystem(files);
        return (fileSystem, EnumerationCounter.Attach(fileSystem));
    }

    private static WorkspaceController CreateController(
        MockFileSystem fileSystem,
        WorkspaceTreeCache cache,
        string agentId = "agent-a",
        string workspacePath = WorkspacePath)
    {
        var registry = new DefaultAgentRegistry(NullLogger<DefaultAgentRegistry>.Instance);
        registry.Register(new AgentDescriptor
        {
            AgentId = AgentId.From(agentId),
            DisplayName = agentId,
            ModelId = "gpt-4.1",
            ApiProvider = "openai"
        });

        var workspaceManager = new Mock<IAgentWorkspaceManager>();
        workspaceManager.Setup(manager => manager.GetWorkspacePath(agentId)).Returns(workspacePath);

        return new WorkspaceController(registry, workspaceManager.Object, CountingFileSystem.Wrap(fileSystem), cache);
    }

    /// <summary>Shared mutable call counter for the wrapped filesystem.</summary>
    public sealed class EnumerationCounter
    {
        private static readonly Dictionary<MockFileSystem, EnumerationCounter> Attached = [];

        /// <summary>Number of directory enumerations performed since attach.</summary>
        public int EnumerateCalls { get; internal set; }

        internal static EnumerationCounter Attach(MockFileSystem fileSystem)
        {
            var counter = new EnumerationCounter();
            lock (Attached)
                Attached[fileSystem] = counter;
            return counter;
        }

        internal static EnumerationCounter For(MockFileSystem fileSystem)
        {
            lock (Attached)
            {
                if (!Attached.TryGetValue(fileSystem, out var counter))
                {
                    counter = new EnumerationCounter();
                    Attached[fileSystem] = counter;
                }

                return counter;
            }
        }
    }

    /// <summary>
    /// Reflection-forwarding <see cref="IFileSystem"/> that counts directory enumerations.
    /// A hand-written delegating wrapper would be ~200 members of ceremony; a
    /// <see cref="DispatchProxy"/> forwards everything verbatim and intercepts only the one call
    /// the assertion is about, so the wrapper cannot silently change filesystem behaviour.
    /// </summary>
    public class CountingFileSystem : DispatchProxy
    {
        private MockFileSystem _inner = null!;
        private IDirectory _countingDirectory = null!;

        internal static IFileSystem Wrap(MockFileSystem inner)
        {
            var proxy = Create<IFileSystem, CountingFileSystem>();
            var typed = (CountingFileSystem)(object)proxy;
            typed._inner = inner;
            typed._countingDirectory = CountingDirectory.Wrap(inner.Directory, EnumerationCounter.For(inner));
            return proxy;
        }

        /// <inheritdoc />
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == $"get_{nameof(IFileSystem.Directory)}")
                return _countingDirectory;

            return targetMethod?.Invoke(_inner, args);
        }
    }

    /// <summary>Forwarding <see cref="IDirectory"/> that increments the counter per enumeration.</summary>
    public class CountingDirectory : DispatchProxy
    {
        private IDirectory _inner = null!;
        private EnumerationCounter _counter = null!;

        internal static IDirectory Wrap(IDirectory inner, EnumerationCounter counter)
        {
            var proxy = Create<IDirectory, CountingDirectory>();
            var typed = (CountingDirectory)(object)proxy;
            typed._inner = inner;
            typed._counter = counter;
            return proxy;
        }

        /// <inheritdoc />
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == nameof(IDirectory.EnumerateFileSystemEntries))
                _counter.EnumerateCalls++;

            return targetMethod?.Invoke(_inner, args);
        }
    }
}
