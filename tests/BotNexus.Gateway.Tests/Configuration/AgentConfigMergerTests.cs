using System.Text.Json;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Configuration;

namespace BotNexus.Gateway.Tests.Configuration;

/// <summary>
/// Tests for <see cref="AgentConfigMerger"/> — field-level inheritance merge semantics.
/// All scenarios derived from Leela's design review (Issue #12).
/// </summary>
public sealed class AgentConfigMergerTests
{
    // -------------------------------------------------------------------------
    // Memory merge — full inherit (scenario 3)
    // -------------------------------------------------------------------------

    [Fact]
    public void Merge_AgentOmitsMemory_InheritsFullDefaultMemoryBlock()
    {
        // Arrange
        var defaults = new AgentDefaultsConfig
        {
            Memory = new MemoryAgentConfig
            {
                Enabled = true,
                Indexing = "auto",
                Search = new MemorySearchAgentConfig { DefaultTopK = 5 }
            }
        };
        var agent = new AgentDefinitionConfig { Provider = "copilot", Model = "gpt-4.1" };
        // No memory on agent, no raw JSON — treat all nulls as "inherit"

        // Act
        var result = AgentConfigMerger.Merge(defaults, agent, agentRawElement: null);

        // Assert
        result.Memory.ShouldNotBeNull();
        result.Memory!.Enabled.ShouldBeTrue();
        result.Memory.Indexing.ShouldBe("auto");
        result.Memory.Search.ShouldNotBeNull();
        result.Memory.Search!.DefaultTopK.ShouldBe(5);
    }

    [Fact]
    public void Merge_AgentOmitsMemory_WithRawJson_InheritsFullDefaultMemoryBlock()
    {
        // Arrange
        var defaults = new AgentDefaultsConfig
        {
            Memory = new MemoryAgentConfig { Enabled = true, Indexing = "semantic" }
        };
        var agent = new AgentDefinitionConfig { Provider = "copilot", Model = "gpt-4.1" };
        // Raw JSON that does NOT contain "memory" key
        var raw = JsonDocument.Parse("""{"provider":"copilot","model":"gpt-4.1","enabled":true}""").RootElement;

        // Act
        var result = AgentConfigMerger.Merge(defaults, agent, raw);

        // Assert
        result.Memory.ShouldNotBeNull();
        result.Memory!.Enabled.ShouldBeTrue();
        result.Memory.Indexing.ShouldBe("semantic");
    }

    // -------------------------------------------------------------------------
    // Memory merge — partial override (scenario 4)
    // -------------------------------------------------------------------------

    [Fact]
    public void Merge_AgentOverridesOneMemoryField_OtherFieldsInheritedFromDefaults()
    {
        // Arrange
        var defaults = new AgentDefaultsConfig
        {
            Memory = new MemoryAgentConfig { Enabled = true, Indexing = "auto" }
        };
        var agent = new AgentDefinitionConfig
        {
            Provider = "copilot",
            Model = "gpt-4.1",
            Memory = new MemoryAgentConfig { Indexing = "manual" }
        };
        // Raw JSON explicitly includes "memory" with only "indexing"
        var raw = JsonDocument.Parse("""{"provider":"copilot","model":"gpt-4.1","memory":{"indexing":"manual"}}""").RootElement;

        // Act
        var result = AgentConfigMerger.Merge(defaults, agent, raw);

        // Assert
        result.Memory.ShouldNotBeNull();
        result.Memory!.Indexing.ShouldBe("manual");        // agent override wins
        result.Memory.Enabled.ShouldBeTrue();              // inherited from defaults
    }

    // -------------------------------------------------------------------------
    // Memory merge — explicit false (scenario 5)
    // -------------------------------------------------------------------------

    [Fact]
    public void Merge_AgentSetsMemoryEnabledFalse_OverridesInheritedTrue()
    {
        // Arrange
        var defaults = new AgentDefaultsConfig
        {
            Memory = new MemoryAgentConfig { Enabled = true, Indexing = "auto" }
        };
        var agent = new AgentDefinitionConfig
        {
            Provider = "copilot",
            Model = "gpt-4.1",
            Memory = new MemoryAgentConfig { Enabled = false, Indexing = "auto" }
        };
        // Raw JSON explicitly sets memory.enabled = false
        var raw = JsonDocument.Parse("""{"provider":"copilot","model":"gpt-4.1","memory":{"enabled":false}}""").RootElement;

        // Act
        var result = AgentConfigMerger.Merge(defaults, agent, raw);

        // Assert
        result.Memory.ShouldNotBeNull();
        result.Memory!.Enabled.ShouldBeFalse();   // explicit false wins over inherited true
        result.Memory.Indexing.ShouldBe("auto");  // inherits when not in raw
    }

    // -------------------------------------------------------------------------
    // Heartbeat merge — partial override (scenario 6)
    // -------------------------------------------------------------------------

    [Fact]
    public void Merge_AgentOverridesHeartbeatIntervalOnly_InheritedEnabledRemains()
    {
        // Arrange
        var defaults = new AgentDefaultsConfig
        {
            Heartbeat = new HeartbeatAgentConfig { Enabled = true, IntervalMinutes = 30 }
        };
        var agent = new AgentDefinitionConfig
        {
            Provider = "copilot",
            Model = "gpt-4.1",
            Heartbeat = new HeartbeatAgentConfig { IntervalMinutes = 60 }
        };
        // Raw JSON: agent only sets intervalMinutes
        var raw = JsonDocument.Parse("""{"provider":"copilot","model":"gpt-4.1","heartbeat":{"intervalMinutes":60}}""").RootElement;

        // Act
        var result = AgentConfigMerger.Merge(defaults, agent, raw);

        // Assert
        result.Heartbeat.ShouldNotBeNull();
        result.Heartbeat!.IntervalMinutes.ShouldBe(60);   // agent override
        result.Heartbeat.Enabled.ShouldBeTrue();          // inherited from defaults
    }

    // -------------------------------------------------------------------------
    // FileAccess merge — list replacement (scenario 7)
    // -------------------------------------------------------------------------

    [Fact]
    public void Merge_AgentSuppliesAllowedReadPaths_ReplacesDefaultListNotUnion()
    {
        // Arrange
        var defaults = new AgentDefaultsConfig
        {
            FileAccess = new FileAccessPolicyConfig
            {
                AllowedReadPaths = ["/defaults/read1", "/defaults/read2"]
            }
        };
        var agent = new AgentDefinitionConfig
        {
            Provider = "copilot",
            Model = "gpt-4.1",
            FileAccess = new FileAccessPolicyConfig
            {
                AllowedReadPaths = ["/agent/read"]
            }
        };
        var raw = JsonDocument.Parse("""{"provider":"copilot","model":"gpt-4.1","fileAccess":{"allowedReadPaths":["/agent/read"]}}""").RootElement;

        // Act
        var result = AgentConfigMerger.Merge(defaults, agent, raw);

        // Assert
        result.FileAccess.ShouldNotBeNull();
        result.FileAccess!.AllowedReadPaths.ShouldBe(["/agent/read"]);  // replaced, not union
        result.FileAccess.AllowedReadPaths.ShouldNotContain("/defaults/read1");
    }

    // -------------------------------------------------------------------------
    // FileAccess merge — partial (scenario 8)
    // -------------------------------------------------------------------------

    [Fact]
    public void Merge_AgentOverridesOneFileAccessList_OtherListsRemainInherited()
    {
        // Arrange
        var defaults = new AgentDefaultsConfig
        {
            FileAccess = new FileAccessPolicyConfig
            {
                AllowedReadPaths = ["/defaults/read"],
                AllowedWritePaths = ["/defaults/write"],
                DeniedPaths = ["/defaults/denied"]
            }
        };
        var agent = new AgentDefinitionConfig
        {
            Provider = "copilot",
            Model = "gpt-4.1",
            FileAccess = new FileAccessPolicyConfig
            {
                AllowedWritePaths = ["/agent/write"]
            }
        };
        // Agent only sets allowedWritePaths
        var raw = JsonDocument.Parse("""{"provider":"copilot","model":"gpt-4.1","fileAccess":{"allowedWritePaths":["/agent/write"]}}""").RootElement;

        // Act
        var result = AgentConfigMerger.Merge(defaults, agent, raw);

        // Assert
        result.FileAccess.ShouldNotBeNull();
        result.FileAccess!.AllowedWritePaths.ShouldBe(["/agent/write"]);   // overridden
        result.FileAccess.AllowedReadPaths.ShouldBe(["/defaults/read"]);   // inherited
        result.FileAccess.DeniedPaths.ShouldBe(["/defaults/denied"]);      // inherited
    }

    // -------------------------------------------------------------------------
    // ToolIds replacement (scenario 9)
    // -------------------------------------------------------------------------

    [Fact]
    public void Merge_AgentOmitsToolIds_InheritsDefaultList()
    {
        // Arrange
        var defaults = new AgentDefaultsConfig
        {
            ToolIds = ["tool-a", "tool-b"]
        };
        var agent = new AgentDefinitionConfig { Provider = "copilot", Model = "gpt-4.1" };
        var raw = JsonDocument.Parse("""{"provider":"copilot","model":"gpt-4.1"}""").RootElement;

        // Act
        var result = AgentConfigMerger.Merge(defaults, agent, raw);

        // Assert
        result.ToolIds.ShouldBe(["tool-a", "tool-b"]);
    }

    [Fact]
    public void Merge_AgentSetsToolIds_ReplacesDefaultListEntirely()
    {
        // Arrange
        var defaults = new AgentDefaultsConfig
        {
            ToolIds = ["tool-a", "tool-b"]
        };
        var agent = new AgentDefinitionConfig
        {
            Provider = "copilot",
            Model = "gpt-4.1",
            ToolIds = ["tool-c"]
        };
        var raw = JsonDocument.Parse("""{"provider":"copilot","model":"gpt-4.1","toolIds":["tool-c"]}""").RootElement;

        // Act
        var result = AgentConfigMerger.Merge(defaults, agent, raw);

        // Assert
        result.ToolIds.ShouldBe(["tool-c"]);      // replaced entirely
        result.ToolIds.ShouldNotContain("tool-a");
    }

    [Fact]
    public void Merge_AgentSetsEmptyToolIds_ReplacesDefaultListWithEmpty()
    {
        // Arrange — agent explicitly sets empty list (replacement semantics)
        var defaults = new AgentDefaultsConfig { ToolIds = ["tool-a"] };
        var agent = new AgentDefinitionConfig
        {
            Provider = "copilot",
            Model = "gpt-4.1",
            ToolIds = []
        };
        var raw = JsonDocument.Parse("""{"provider":"copilot","model":"gpt-4.1","toolIds":[]}""").RootElement;

        // Act
        var result = AgentConfigMerger.Merge(defaults, agent, raw);

        // Assert
        result.ToolIds.ShouldNotBeNull();
        result.ToolIds!.ShouldBeEmpty();  // empty list replacement wins
    }

    // -------------------------------------------------------------------------
    // No defaults — passthrough (scenario 1 — merger side)
    // -------------------------------------------------------------------------

    [Fact]
    public void Merge_NullDefaults_ReturnsOriginalAgentConfigUnchanged()
    {
        // Arrange
        var agent = new AgentDefinitionConfig
        {
            Provider = "copilot",
            Model = "gpt-4.1",
            ToolIds = ["tool-x"],
            Memory = new MemoryAgentConfig { Enabled = true }
        };

        // Act
        var result = AgentConfigMerger.Merge(defaults: null, agent: agent, agentRawElement: null);

        // Assert — exact same instance returned
        result.ShouldBeSameAs(agent);
        result.ToolIds.ShouldBe(["tool-x"]);
        result.Memory!.Enabled.ShouldBeTrue();
    }

    // -------------------------------------------------------------------------
    // MergeMemory internal — direct tests for presence-aware logic
    // -------------------------------------------------------------------------

    [Fact]
    public void MergeMemory_BothNull_ReturnsNull()
    {
        var result = AgentConfigMerger.MergeMemory(null, null, null);
        result.ShouldBeNull();
    }

    [Fact]
    public void MergeMemory_DefaultsOnlyNoAgentKey_ReturnsCloneOfDefaults()
    {
        var defaults = new MemoryAgentConfig { Enabled = true, Indexing = "auto" };
        var agentObj = JsonDocument.Parse("""{"provider":"copilot"}""").RootElement;

        var result = AgentConfigMerger.MergeMemory(defaults, null, agentObj);

        result.ShouldNotBeNull();
        result!.Enabled.ShouldBeTrue();
        result.Indexing.ShouldBe("auto");
        result.ShouldNotBeSameAs(defaults);  // must be a clone
    }

    [Fact]
    public void MergeHeartbeat_AgentOmitsBlock_InheritsDefaults()
    {
        var defaults = new HeartbeatAgentConfig { Enabled = true, IntervalMinutes = 15 };
        var agentObj = JsonDocument.Parse("""{"provider":"copilot"}""").RootElement;

        var result = AgentConfigMerger.MergeHeartbeat(defaults, null, agentObj);

        result.ShouldNotBeNull();
        result!.Enabled.ShouldBeTrue();
        result.IntervalMinutes.ShouldBe(15);
    }
}
