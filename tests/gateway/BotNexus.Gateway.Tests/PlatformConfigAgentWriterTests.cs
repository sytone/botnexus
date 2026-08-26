using BotNexus.Domain.Primitives;
using BotNexus.Domain.World;
using System.Text.Json;
using System.Text.Json.Nodes;
using BotNexus.Gateway.Abstractions.Configuration;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Security;
using BotNexus.Gateway.Configuration;
using System.IO.Abstractions.TestingHelpers;

namespace BotNexus.Gateway.Tests;

public sealed class PlatformConfigAgentWriterTests : IDisposable
{
    private readonly string _rootPath = Path.Combine(Path.GetTempPath(), "botnexus-platform-agent-writer-tests", Guid.NewGuid().ToString("N"));
    private readonly string _configPath;
    private readonly BotNexusHome _home;
    private readonly MockFileSystem _fileSystem;

    public PlatformConfigAgentWriterTests()
    {
        _fileSystem = new MockFileSystem();
        _home = new BotNexusHome(_fileSystem, _rootPath);
        _fileSystem.Directory.CreateDirectory(_rootPath);
        _configPath = Path.Combine(_rootPath, "config.json");
    }

    [Fact]
    public async Task SaveAsync_WritesAgentIntoConfigAndCreatesWorkspace()
    {
        var writer = new PlatformConfigAgentWriter(new PlatformConfigWriter(_configPath, _fileSystem), _home);
        var descriptor = CreateDescriptor("test-agent") with
        {
            AllowedModelIds = ["claude-sonnet-4.5"],
            ToolIds = ["read"],
            SubAgentIds = ["helper"],
            Metadata = new Dictionary<string, object?> { ["owner"] = "gateway" },
            IsolationOptions = new Dictionary<string, object?> { ["timeoutMs"] = 1000 }
        };

        await writer.SaveAsync(descriptor);

        var root = await ReadConfigAsync();
        var agent = root["agents"]!["test-agent"]!;

        agent["provider"]!.GetValue<string>().ShouldBe("github-copilot");
        agent["model"]!.GetValue<string>().ShouldBe("claude-sonnet-4.5");
        agent["displayName"]!.GetValue<string>().ShouldBe("test-agent");
        agent["enabled"]!.GetValue<bool>().ShouldBeTrue();
        agent["allowedModels"]!.AsArray().ShouldHaveSingleItem()!.GetValue<string>().ShouldBe("claude-sonnet-4.5");
        agent["toolIds"]!.AsArray().ShouldHaveSingleItem()!.GetValue<string>().ShouldBe("read");
        agent["subAgents"]!.AsArray().ShouldHaveSingleItem()!.GetValue<string>().ShouldBe("helper");
        agent["metadata"]!["owner"]!.GetValue<string>().ShouldBe("gateway");
        agent["isolationOptions"]!["timeoutMs"]!.GetValue<int>().ShouldBe(1000);

        _fileSystem.Directory.Exists(Path.Combine(_home.AgentsPath, "test-agent")).ShouldBeTrue();
        _fileSystem.File.Exists(Path.Combine(_home.AgentsPath, "test-agent", "workspace", "SOUL.md")).ShouldBeTrue();
    }

    /// <summary>
    /// #2649 guard for the reported "apiProvider/modelId serialise as null" premise. That premise
    /// does NOT reproduce as data loss: <see cref="AgentDescriptor.ApiProvider"/> and
    /// <see cref="AgentDescriptor.ModelId"/> are persisted under the config-file names
    /// <c>provider</c> / <c>model</c> and read back into the same descriptor fields. What the
    /// reporter saw was purely the name change across that boundary - looking for an
    /// <c>apiProvider</c> key in config.json finds nothing, because the key is <c>provider</c>.
    /// This test pins the round trip so a future rename cannot quietly turn the cosmetic
    /// asymmetry into a real one.
    /// </summary>
    [Fact]
    public async Task SaveAsync_RoundTripsProviderAndModel_UnderTheirConfigFileNames()
    {
        var writer = new PlatformConfigAgentWriter(new PlatformConfigWriter(_configPath, _fileSystem), _home);
        await writer.SaveAsync(CreateDescriptor("roundtrip-agent"));

        // Persisted under the config-file vocabulary, not the descriptor property names.
        var root = await ReadConfigAsync();
        var agent = root["agents"]!["roundtrip-agent"]!;
        agent["provider"]!.GetValue<string>().ShouldBe("github-copilot");
        agent["model"]!.GetValue<string>().ShouldBe("claude-sonnet-4.5");
        agent["apiProvider"].ShouldBeNull();
        agent["modelId"].ShouldBeNull();

        // ...and read back into the descriptor fields with no loss, so the value the tool
        // validated is the value the runtime later resolves.
        var config = await PlatformConfigLoader.LoadAsync(
            _configPath,
            CancellationToken.None,
            validateOnLoad: true,
            fileSystem: _fileSystem);

        config.Agents!["roundtrip-agent"].Provider.ShouldBe("github-copilot");
        config.Agents["roundtrip-agent"].Model.ShouldBe("claude-sonnet-4.5");
    }

    [Fact]
    public async Task SaveAsync_WithWireThinkingLevel_ProducesReloadableConfig()
    {
        var writer = new PlatformConfigAgentWriter(new PlatformConfigWriter(_configPath, _fileSystem), _home);
        await writer.SaveAsync(CreateDescriptor("thinking-agent") with { Thinking = "xhigh" });

        var config = await PlatformConfigLoader.LoadAsync(
            _configPath,
            CancellationToken.None,
            validateOnLoad: true,
            fileSystem: _fileSystem);

        config.Agents!["thinking-agent"].Thinking.ShouldBe("xhigh");
    }

    [Fact]
    public async Task SaveAsync_PreservesUnknownFieldsAndOmitsEmptyOptionalValues()
    {
        await _fileSystem.File.WriteAllTextAsync(_configPath, """
            {
              "version": 1,
              "customRootField": "preserve-me",
              "agents": {
                "test-agent": {
                  "customAgentField": "keep"
                }
              }
            }
            """);

        var writer = new PlatformConfigAgentWriter(new PlatformConfigWriter(_configPath, _fileSystem), _home);
        await writer.SaveAsync(CreateDescriptor("test-agent") with
        {
            Description = null,
            SystemPromptFile = null,
            AllowedModelIds = [],
            ToolIds = [],
            SubAgentIds = [],
            MaxConcurrentSessions = 0
        });

        var root = await ReadConfigAsync();
        var agent = root["agents"]!["test-agent"]!;

        root["customRootField"]!.GetValue<string>().ShouldBe("preserve-me");
        agent["customAgentField"]!.GetValue<string>().ShouldBe("keep");
        agent["description"].ShouldBeNull();
        agent["systemPromptFile"].ShouldBeNull();
        agent["allowedModels"].ShouldBeNull();
        agent["toolIds"].ShouldBeNull();
        agent["subAgents"].ShouldBeNull();
        agent["maxConcurrentSessions"].ShouldBeNull();
    }

    [Fact]
    public async Task DeleteAsync_RemovesAgentFromConfig()
    {
        await _fileSystem.File.WriteAllTextAsync(_configPath, """
            {
              "agents": {
                "test-agent": { "provider": "github-copilot", "model": "gpt-4.1" },
                "other": { "provider": "openai", "model": "gpt-4.1" }
              }
            }
            """);

        var writer = new PlatformConfigAgentWriter(new PlatformConfigWriter(_configPath, _fileSystem), _home);
        await writer.DeleteAsync("test-agent");

        var root = await ReadConfigAsync();
        root["agents"]!["test-agent"].ShouldBeNull();
        root["agents"]!["other"].ShouldNotBeNull();
    }

    /// <summary>
    /// #3547 regression corpus: a real portal edit (PUT /api/agents/aurum setting only
    /// <c>thinking</c>) deleted 11 keys from the live config. The stored shape below is the actual
    /// one that was damaged, reduced to the affected keys.
    /// </summary>
    /// <remarks>
    /// The mechanism was that <c>SaveAsync</c> projects a whole <see cref="AgentDescriptor"/> over
    /// the stored entry, and the descriptor is a LOSSY projection of that entry: the config source
    /// cannot round-trip every extension, so every setter treating "absent from the descriptor" as
    /// "delete from config" destroyed keys the caller never mentioned.
    /// </remarks>
    [Fact]
    public async Task SaveAsync_EditingOneField_PreservesEveryUnmodelledStoredKey()
    {
        await _fileSystem.File.WriteAllTextAsync(_configPath, """
            {
              "agents": {
                "aurum": {
                  "provider": "github-copilot",
                  "model": "claude-sonnet-4.5",
                  "displayName": "aurum",
                  "maxConcurrentSessions": 0,
                  "extensions": {
                    "botnexus-exec": { "enabled": true },
                    "botnexus-process": { "enabled": false },
                    "botnexus-skills": {
                      "maxLoadedSkills": 12,
                      "allowSkillCreation": true,
                      "allowed": null
                    }
                  }
                }
              }
            }
            """);

        var writer = new PlatformConfigAgentWriter(new PlatformConfigWriter(_configPath, _fileSystem), _home);

        // The descriptor the REST layer produces for a "set thinking = high" edit: the extension
        // bag is empty because the source could not round-trip it, and the count reads as 0.
        await writer.SaveAsync(CreateDescriptor("aurum") with { Thinking = "high" });

        var agent = (await ReadConfigAsync())["agents"]!["aurum"]!;

        // The intended edit landed.
        agent["thinking"]!.GetValue<string>().ShouldBe("high");

        // ...and nothing else was destroyed.
        agent["maxConcurrentSessions"]!.GetValue<int>().ShouldBe(0);
        var extensions = agent["extensions"]!.AsObject();
        extensions["botnexus-exec"]!["enabled"]!.GetValue<bool>().ShouldBeTrue();
        extensions["botnexus-process"]!["enabled"]!.GetValue<bool>().ShouldBeFalse();
        extensions["botnexus-skills"]!["maxLoadedSkills"]!.GetValue<int>().ShouldBe(12);
        extensions["botnexus-skills"]!["allowSkillCreation"]!.GetValue<bool>().ShouldBeTrue();

        // An explicit null is a distinct state from an absent key and must survive as one.
        extensions["botnexus-skills"]!.AsObject().ContainsKey("allowed").ShouldBeTrue();
        extensions["botnexus-skills"]!["allowed"].ShouldBeNull();
    }

    /// <summary>
    /// #3547: an extension the descriptor DOES carry must still be written, and must merge into the
    /// stored bag rather than replacing it. Without this the fix would degenerate into "extensions
    /// are never writable", which is a different defect.
    /// </summary>
    [Fact]
    public async Task SaveAsync_MergesSuppliedExtension_WithoutDroppingStoredSiblings()
    {
        await _fileSystem.File.WriteAllTextAsync(_configPath, """
            {
              "agents": {
                "test-agent": {
                  "extensions": {
                    "botnexus-exec": { "enabled": true },
                    "botnexus-skills": { "maxLoadedSkills": 12 }
                  }
                }
              }
            }
            """);

        var writer = new PlatformConfigAgentWriter(new PlatformConfigWriter(_configPath, _fileSystem), _home);
        await writer.SaveAsync(CreateDescriptor("test-agent") with
        {
            ExtensionConfig = new Dictionary<string, JsonElement>
            {
                ["botnexus-skills"] = JsonDocument.Parse("""{"maxLoadedSkills":30}""").RootElement
            }
        });

        var extensions = (await ReadConfigAsync())["agents"]!["test-agent"]!["extensions"]!.AsObject();

        // The supplied extension is updated...
        extensions["botnexus-skills"]!["maxLoadedSkills"]!.GetValue<int>().ShouldBe(30);
        // ...and the sibling the descriptor never mentioned is untouched.
        extensions["botnexus-exec"]!["enabled"]!.GetValue<bool>().ShouldBeTrue();
    }

    /// <summary>
    /// #3547: a stored <c>maxConcurrentSessions: 0</c> means "unlimited" - a deliberate value, not
    /// an absent one. The old <c>value &lt;= 0</c> sentinel could not tell the two apart and deleted it.
    /// </summary>
    [Fact]
    public async Task SaveAsync_WithZeroCount_PreservesStoredZeroInsteadOfDeletingIt()
    {
        await _fileSystem.File.WriteAllTextAsync(_configPath, """
            { "agents": { "test-agent": { "maxConcurrentSessions": 0 } } }
            """);

        var writer = new PlatformConfigAgentWriter(new PlatformConfigWriter(_configPath, _fileSystem), _home);
        await writer.SaveAsync(CreateDescriptor("test-agent") with { MaxConcurrentSessions = 0 });

        var agent = (await ReadConfigAsync())["agents"]!["test-agent"]!;
        agent["maxConcurrentSessions"].ShouldNotBeNull();
        agent["maxConcurrentSessions"]!.GetValue<int>().ShouldBe(0);
    }

    /// <summary>
    /// #3547: <see cref="PlatformConfigAgentSource"/> resolves '@location' aliases to absolute paths
    /// on read, so a descriptor that merely round-tripped carries resolved paths. Writing those back
    /// verbatim replaced portable aliases with machine-specific absolute paths.
    /// </summary>
    [Fact]
    public async Task SaveAsync_WithResolvedPaths_WritesBackTheStoredAlias()
    {
        await _fileSystem.File.WriteAllTextAsync(_configPath, """
            {
              "agents": {
                "test-agent": {
                  "fileAccess": { "allowedReadPaths": ["@botnexus-repo", "/literal/path"] }
                }
              }
            }
            """);

        var resolver = new StubLocationResolver(new Dictionary<string, string>
        {
            ["botnexus-repo"] = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "repo"))
        });
        var writer = new PlatformConfigAgentWriter(
            new PlatformConfigWriter(_configPath, _fileSystem), _home, resolver);

        // Exactly what the source produced on read: the alias resolved, the literal untouched.
        await writer.SaveAsync(CreateDescriptor("test-agent") with
        {
            FileAccess = new FileAccessPolicy
            {
                AllowedReadPaths =
                [
                    Path.GetFullPath(Path.Combine(Path.GetTempPath(), "repo")),
                    "/literal/path"
                ]
            }
        });

        var paths = (await ReadConfigAsync())["agents"]!["test-agent"]!["fileAccess"]!["allowedReadPaths"]!.AsArray();
        paths[0]!.GetValue<string>().ShouldBe("@botnexus-repo");
        paths[1]!.GetValue<string>().ShouldBe("/literal/path");
    }

    /// <summary>
    /// #3547 control: alias preservation must not swallow a GENUINE path change. An incoming path
    /// the stored alias no longer resolves to is the caller's edit and must be written through.
    /// </summary>
    [Fact]
    public async Task SaveAsync_WithGenuinelyChangedPath_DoesNotReAliasIt()
    {
        await _fileSystem.File.WriteAllTextAsync(_configPath, """
            {
              "agents": {
                "test-agent": {
                  "fileAccess": { "allowedReadPaths": ["@botnexus-repo"] }
                }
              }
            }
            """);

        var resolver = new StubLocationResolver(new Dictionary<string, string>
        {
            ["botnexus-repo"] = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "repo"))
        });
        var writer = new PlatformConfigAgentWriter(
            new PlatformConfigWriter(_configPath, _fileSystem), _home, resolver);

        var newPath = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "somewhere-else"));
        await writer.SaveAsync(CreateDescriptor("test-agent") with
        {
            FileAccess = new FileAccessPolicy { AllowedReadPaths = [newPath] }
        });

        var paths = (await ReadConfigAsync())["agents"]!["test-agent"]!["fileAccess"]!["allowedReadPaths"]!.AsArray();
        paths.Count.ShouldBe(1);
        paths[0]!.GetValue<string>().ShouldBe(newPath);
    }

    /// <summary>
    /// #3547: a descriptor carrying no file-access policy is not an instruction to delete a stored
    /// one - the descriptor simply does not model it on this edit.
    /// </summary>
    [Fact]
    public async Task SaveAsync_WithNoFileAccessOnDescriptor_KeepsTheStoredSection()
    {
        await _fileSystem.File.WriteAllTextAsync(_configPath, """
            {
              "agents": {
                "test-agent": {
                  "fileAccess": { "allowedReadPaths": ["@botnexus-repo"] }
                }
              }
            }
            """);

        var writer = new PlatformConfigAgentWriter(new PlatformConfigWriter(_configPath, _fileSystem), _home);
        await writer.SaveAsync(CreateDescriptor("test-agent") with { FileAccess = null });

        var fileAccess = (await ReadConfigAsync())["agents"]!["test-agent"]!["fileAccess"];
        fileAccess.ShouldNotBeNull();
        fileAccess!["allowedReadPaths"]!.AsArray()[0]!.GetValue<string>().ShouldBe("@botnexus-repo");
    }

    public void Dispose()
    {
        if (_fileSystem.Directory.Exists(_rootPath))
            _fileSystem.Directory.Delete(_rootPath, recursive: true);
    }

    private async Task<JsonObject> ReadConfigAsync()
    {
        await using var stream = _fileSystem.File.OpenRead(_configPath);
        var node = await JsonNode.ParseAsync(stream);
        return node!.AsObject();
    }

    private static AgentDescriptor CreateDescriptor(string agentId)
        => new()
        {
            AgentId = AgentId.From(agentId),
            DisplayName = agentId,
            ModelId = "claude-sonnet-4.5",
            ApiProvider = "github-copilot",
            IsolationStrategy = "in-process",
            MaxConcurrentSessions = 0
        };

    /// <summary>
    /// Minimal <see cref="ILocationResolver"/> over a name-to-path map, so the alias round-trip can
    /// be asserted without standing up a world descriptor.
    /// </summary>
    private sealed class StubLocationResolver(IReadOnlyDictionary<string, string> paths) : ILocationResolver
    {
        public Location? Resolve(string locationName) => null;

        public string? ResolvePath(string locationName)
            => paths.TryGetValue(locationName, out var path) ? path : null;

        public IReadOnlyList<Location> GetAll() => [];
    }
}
