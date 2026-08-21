using System.Text.Json;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Configuration;

namespace BotNexus.Gateway.Tests.Configuration;

/// <summary>
/// Agent inheritance behaviour, executed through the shared engine (#3485 D2).
/// All scenarios derived from Leela's design review (Issue #12).
/// </summary>
public sealed class AgentInheritanceBehaviourTests
{
    [Fact]
    public void MemoryAgentConfig_DefaultPromptInjection_IsFull()
    {
        var memory = new MemoryAgentConfig();
        GetPromptInjection(memory).ShouldBe("full");
    }

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
        var result = MergeVia.Engine(defaults, agent, agentRawElement: null);

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
        var result = MergeVia.Engine(defaults, agent, raw);

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
        var result = MergeVia.Engine(defaults, agent, raw);

        // Assert
        result.Memory.ShouldNotBeNull();
        result.Memory!.Indexing.ShouldBe("manual");        // agent override wins
        result.Memory.Enabled.ShouldBeTrue();              // inherited from defaults
    }

    [Fact]
    public void Merge_AgentOmitsMemoryPromptInjection_InheritsDefaults()
    {
        var defaultsMemory = new MemoryAgentConfig { Enabled = true, Indexing = "auto" };
        SetPromptInjection(defaultsMemory, "full");
        var defaults = new AgentDefaultsConfig
        {
            Memory = defaultsMemory
        };
        var agent = new AgentDefinitionConfig
        {
            Provider = "copilot",
            Model = "gpt-4.1",
            Memory = new MemoryAgentConfig { Enabled = true, Indexing = "auto" }
        };
        var raw = JsonDocument.Parse("""{"provider":"copilot","model":"gpt-4.1","memory":{"enabled":true}}""").RootElement;

        var result = MergeVia.Engine(defaults, agent, raw);

        result.Memory.ShouldNotBeNull();
        GetPromptInjection(result.Memory!).ShouldBe("full");
    }

    [Fact]
    public void Merge_AgentOverridesMemoryPromptInjection_UsesAgentValue()
    {
        var defaultsMemory = new MemoryAgentConfig { Enabled = true, Indexing = "auto" };
        SetPromptInjection(defaultsMemory, "full");
        var agentMemory = new MemoryAgentConfig { Enabled = true, Indexing = "auto" };
        SetPromptInjection(agentMemory, "summary");
        var defaults = new AgentDefaultsConfig { Memory = defaultsMemory };
        var agent = new AgentDefinitionConfig
        {
            Provider = "copilot",
            Model = "gpt-4.1",
            Memory = agentMemory
        };
        var raw = JsonDocument.Parse("""{"provider":"copilot","model":"gpt-4.1","memory":{"promptInjection":"summary"}}""").RootElement;

        var result = MergeVia.Engine(defaults, agent, raw);

        result.Memory.ShouldNotBeNull();
        GetPromptInjection(result.Memory!).ShouldBe("summary");
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
        var result = MergeVia.Engine(defaults, agent, raw);

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
        var result = MergeVia.Engine(defaults, agent, raw);

        // Assert
        result.Heartbeat.ShouldNotBeNull();
        result.Heartbeat!.IntervalMinutes.ShouldBe(60);   // agent override
        result.Heartbeat.Enabled.ShouldBeTrue();          // inherited from defaults
    }

    // -------------------------------------------------------------------------
    // Heartbeat merge — activeHours / ackMaxChars inheritance (#2423)
    //
    // Both properties are valid HeartbeatAgentConfig members that MergeHeartbeat and
    // CloneHeartbeat previously omitted entirely, so a value set in agents.defaults or by
    // an agent override was silently dropped from the effective descriptor.
    // -------------------------------------------------------------------------

    [Fact]
    public void Merge_ActiveHoursAndAckMaxCharsInDefaultsOnly_ReachEffectiveDescriptor()
    {
        // Arrange — defaults supply both properties; the agent's heartbeat block mentions neither.
        var defaults = new AgentDefaultsConfig
        {
            Heartbeat = new HeartbeatAgentConfig
            {
                Enabled = true,
                IntervalMinutes = 30,
                AckMaxChars = 500,
                ActiveHours = new ActiveHoursConfig { Start = "09:00", End = "17:00", Timezone = "Europe/London" }
            }
        };
        var agent = new AgentDefinitionConfig
        {
            Provider = "copilot",
            Model = "gpt-4.1",
            Heartbeat = new HeartbeatAgentConfig { IntervalMinutes = 60 }
        };
        var raw = JsonDocument.Parse("""{"provider":"copilot","model":"gpt-4.1","heartbeat":{"intervalMinutes":60}}""").RootElement;

        // Act
        var result = MergeVia.Engine(defaults, agent, raw);

        // Assert
        result.Heartbeat.ShouldNotBeNull();
        result.Heartbeat!.AckMaxChars.ShouldBe(500);              // inherited from defaults
        result.Heartbeat.ActiveHours.ShouldNotBeNull();
        result.Heartbeat.ActiveHours!.Start.ShouldBe("09:00");
        result.Heartbeat.ActiveHours.End.ShouldBe("17:00");
        result.Heartbeat.ActiveHours.Timezone.ShouldBe("Europe/London");
        result.Heartbeat.IntervalMinutes.ShouldBe(60);            // unrelated override still honoured
    }

    [Fact]
    public void Merge_ActiveHoursAndAckMaxCharsOnAgentOnly_SurviveWithNoDefaultsBlock()
    {
        // Arrange — no heartbeat defaults at all; the agent supplies both properties itself.
        var defaults = new AgentDefaultsConfig();
        var agent = new AgentDefinitionConfig
        {
            Provider = "copilot",
            Model = "gpt-4.1",
            Heartbeat = new HeartbeatAgentConfig
            {
                AckMaxChars = 120,
                ActiveHours = new ActiveHoursConfig { Start = "06:30", End = "22:15", Timezone = "America/Los_Angeles" }
            }
        };
        var raw = JsonDocument.Parse(
            """{"provider":"copilot","model":"gpt-4.1","heartbeat":{"ackMaxChars":120,"activeHours":{"start":"06:30","end":"22:15","timezone":"America/Los_Angeles"}}}""").RootElement;

        // Act
        var result = MergeVia.Engine(defaults, agent, raw);

        // Assert
        result.Heartbeat.ShouldNotBeNull();
        result.Heartbeat!.AckMaxChars.ShouldBe(120);
        result.Heartbeat.ActiveHours.ShouldNotBeNull();
        result.Heartbeat.ActiveHours!.Start.ShouldBe("06:30");
        result.Heartbeat.ActiveHours.End.ShouldBe("22:15");
        result.Heartbeat.ActiveHours.Timezone.ShouldBe("America/Los_Angeles");
    }

    [Fact]
    public void Merge_ActiveHoursAndAckMaxCharsInBoth_AgentOverrideWins()
    {
        // Arrange — both sides supply both properties; the agent must win field by field.
        var defaults = new AgentDefaultsConfig
        {
            Heartbeat = new HeartbeatAgentConfig
            {
                Enabled = true,
                AckMaxChars = 500,
                ActiveHours = new ActiveHoursConfig { Start = "09:00", End = "17:00", Timezone = "Europe/London" }
            }
        };
        var agent = new AgentDefinitionConfig
        {
            Provider = "copilot",
            Model = "gpt-4.1",
            Heartbeat = new HeartbeatAgentConfig
            {
                AckMaxChars = 80,
                ActiveHours = new ActiveHoursConfig { Start = "07:00", End = "17:00", Timezone = "Europe/London" }
            }
        };
        // The agent's raw JSON overrides ackMaxChars and only activeHours.start.
        var raw = JsonDocument.Parse(
            """{"provider":"copilot","model":"gpt-4.1","heartbeat":{"ackMaxChars":80,"activeHours":{"start":"07:00"}}}""").RootElement;

        // Act
        var result = MergeVia.Engine(defaults, agent, raw);

        // Assert
        result.Heartbeat.ShouldNotBeNull();
        result.Heartbeat!.AckMaxChars.ShouldBe(80);               // agent override wins
        result.Heartbeat.ActiveHours.ShouldNotBeNull();
        result.Heartbeat.ActiveHours!.Start.ShouldBe("07:00");    // agent override wins
        result.Heartbeat.ActiveHours.End.ShouldBe("17:00");       // inherited: absent from agent raw JSON
        result.Heartbeat.ActiveHours.Timezone.ShouldBe("Europe/London");
        result.Heartbeat.Enabled.ShouldBeTrue();                  // inherited
    }

    [Fact]
    public void Merge_AgentExplicitlyNullsActiveHours_SuppressesInheritedWindow()
    {
        // Arrange — an explicit JSON null must mean "no active window", not "inherit".
        var defaults = new AgentDefaultsConfig
        {
            Heartbeat = new HeartbeatAgentConfig
            {
                ActiveHours = new ActiveHoursConfig { Start = "09:00", End = "17:00" }
            }
        };
        var agent = new AgentDefinitionConfig
        {
            Provider = "copilot",
            Model = "gpt-4.1",
            Heartbeat = new HeartbeatAgentConfig { IntervalMinutes = 15 }
        };
        var raw = JsonDocument.Parse(
            """{"provider":"copilot","model":"gpt-4.1","heartbeat":{"intervalMinutes":15,"activeHours":null}}""").RootElement;

        // Act
        var result = MergeVia.Engine(defaults, agent, raw);

        // Assert
        result.Heartbeat.ShouldNotBeNull();
        result.Heartbeat!.ActiveHours.ShouldBeNull();
    }

    /// <summary>
    /// Categorical guard against the #2423 defect class: <c>CloneHeartbeat</c> (reached via
    /// <c>the merge</c> when only the defaults carry a heartbeat block) must
    /// round-trip EVERY settable property of <see cref="HeartbeatAgentConfig"/>. A property added
    /// later and forgotten in the clone fails here by name rather than silently losing data.
    /// </summary>
    [Fact]
    public void Merge_InheritsDefaultsHeartbeat_CloneRoundTripsEveryProperty()
    {
        // Arrange — every property set to a non-default, distinguishable value.
        var source = new HeartbeatAgentConfig
        {
            Enabled = false,
            IntervalMinutes = 17,
            Prompt = "Custom heartbeat prompt.",
            AckMaxChars = 77,
            QuietHours = new QuietHoursConfig { Enabled = true, Start = "21:30", End = "05:45", Timezone = "UTC" },
            ActiveHours = new ActiveHoursConfig { Start = "08:15", End = "19:45", Timezone = "Asia/Tokyo" }
        };
        var defaults = new AgentDefaultsConfig { Heartbeat = source };
        var agent = new AgentDefinitionConfig { Provider = "copilot", Model = "gpt-4.1" };

        // Act — agent has no heartbeat block, so the whole default block is cloned.
        var clone = MergeVia.Engine(defaults, agent, agentRawElement: null).Heartbeat;

        // Assert — a real clone, not the same instance, carrying identical values everywhere.
        clone.ShouldNotBeNull();
        clone.ShouldNotBeSameAs(source);
        clone!.ActiveHours.ShouldNotBeSameAs(source.ActiveHours);
        clone.QuietHours.ShouldNotBeSameAs(source.QuietHours);

        foreach (var property in typeof(HeartbeatAgentConfig).GetProperties().Where(p => p.CanRead && p.CanWrite))
        {
            var expected = JsonSerializer.Serialize(property.GetValue(source));
            var actual = JsonSerializer.Serialize(property.GetValue(clone));
            actual.ShouldBe(
                expected,
                $"HeartbeatAgentConfig.{property.Name} was dropped by CloneHeartbeat (regression of #2423).");
        }
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
        var result = MergeVia.Engine(defaults, agent, raw);

        // Assert
        result.FileAccess.ShouldNotBeNull();
        var fileAccess = result.FileAccess ?? throw new InvalidOperationException("Expected file access policy.");
        fileAccess.AllowedReadPaths.ShouldNotBeNull();
        var allowedReadPaths = fileAccess.AllowedReadPaths ?? throw new InvalidOperationException("Expected read paths.");
        allowedReadPaths.ShouldBe(["/agent/read"]);  // replaced, not union
        allowedReadPaths.ShouldNotContain("/defaults/read1");
    }

    // -------------------------------------------------------------------------
    // FileAccess merge - REPLACE AS UNIT (scenario 8, revised by #3485 D2)
    // -------------------------------------------------------------------------

    /// <summary>
    /// An agent that sets any part of <c>fileAccess</c> replaces the whole policy; it does NOT
    /// inherit the sibling path lists.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This test previously asserted the opposite</b>, because the hand-written merger
    /// deep-merged <c>fileAccess</c> field by field. That behaviour is a security defect, and
    /// <c>AgentDefinitionConfig.FileAccess</c> is declared <c>ReplaceAsUnit</c> for exactly this
    /// reason (#3235):
    /// </para>
    /// <para>
    /// <i>"Allow and deny path lists are one policy. Unioning an inherited allow list with a child's
    /// narrower one grants filesystem access the child was written to withhold."</i>
    /// </para>
    /// <para>
    /// Under the old behaviour an agent that narrowed its write paths silently kept the world's read
    /// allowlist - access neither layer authorised as a whole. The architecture fence
    /// <c>SecurityBoundaryBlocks_AreClassifiedReplaceAsUnit</c> already pinned the intended policy;
    /// the merger simply did not implement it, and nothing detected the disagreement because the two
    /// lived in different files with no shared execution path. Routing through the engine is what
    /// makes the declared policy and the executed behaviour the same thing.
    /// </para>
    /// <para>
    /// The practical consequence for operators is stated in the migration note in
    /// <c>docs/configuration.md</c>: an agent overriding one path list must now restate the others.
    /// That is the intended trade - an explicit, auditable policy over a silently widened one.
    /// </para>
    /// </remarks>
    [Fact]
    public void Merge_AgentOverridesOneFileAccessList_ReplacesTheWholePolicy()
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
        var result = MergeVia.Engine(defaults, agent, raw);

        // Assert - the agent's policy stands alone.
        result.FileAccess.ShouldNotBeNull();
        result.FileAccess!.AllowedWritePaths.ShouldBe(["/agent/write"]);
        result.FileAccess.AllowedReadPaths.ShouldBeNull(
            "a security boundary replaces as a unit; inheriting the world read allowlist would grant " +
            "access this agent did not authorise");
        result.FileAccess.DeniedPaths.ShouldBeNull(
            "likewise the deny list - a partially inherited policy is neither layer's policy");
    }

    /// <summary>
    /// An agent that does not mention <c>fileAccess</c> at all still inherits the world policy whole.
    /// Without this, the case above could pass against an implementation that never inherits.
    /// </summary>
    [Fact]
    public void Merge_AgentOmitsFileAccess_InheritsTheWholeWorldPolicy()
    {
        var defaults = new AgentDefaultsConfig
        {
            FileAccess = new FileAccessPolicyConfig
            {
                AllowedReadPaths = ["/defaults/read"],
                AllowedWritePaths = ["/defaults/write"],
                DeniedPaths = ["/defaults/denied"]
            }
        };
        var agent = new AgentDefinitionConfig { Provider = "copilot", Model = "gpt-4.1" };
        var raw = JsonDocument.Parse("""{"provider":"copilot","model":"gpt-4.1"}""").RootElement;

        var result = MergeVia.Engine(defaults, agent, raw);

        result.FileAccess.ShouldNotBeNull();
        result.FileAccess!.AllowedReadPaths.ShouldBe(["/defaults/read"]);
        result.FileAccess.AllowedWritePaths.ShouldBe(["/defaults/write"]);
        result.FileAccess.DeniedPaths.ShouldBe(["/defaults/denied"]);
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
        var result = MergeVia.Engine(defaults, agent, raw);

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
        var result = MergeVia.Engine(defaults, agent, raw);

        // Assert
        result.ToolIds.ShouldBe(["tool-c"]);      // replaced entirely
        var toolIds = result.ToolIds ?? throw new InvalidOperationException("Expected tool IDs.");
        toolIds.ShouldNotContain("tool-a");
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
        var result = MergeVia.Engine(defaults, agent, raw);

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
        var result = MergeVia.Engine(defaults: null, agent: agent, agentRawElement: null);

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
        var result = MergeVia.Memory(null, null, null);
        result.ShouldBeNull();
    }

    [Fact]
    public void MergeMemory_DefaultsOnlyNoAgentKey_ReturnsCloneOfDefaults()
    {
        var defaults = new MemoryAgentConfig { Enabled = true, Indexing = "auto" };
        SetPromptInjection(defaults, "full");
        var agentObj = JsonDocument.Parse("""{"provider":"copilot"}""").RootElement;

        var result = MergeVia.Memory(defaults, null, agentObj);

        result.ShouldNotBeNull();
        result!.Enabled.ShouldBeTrue();
        result.Indexing.ShouldBe("auto");
        GetPromptInjection(result).ShouldBe("full");
        result.ShouldNotBeSameAs(defaults);  // must be a clone
    }

    [Fact]
    public void MergeHeartbeat_AgentOmitsBlock_InheritsDefaults()
    {
        var defaults = new HeartbeatAgentConfig { Enabled = true, IntervalMinutes = 15 };
        var agentObj = JsonDocument.Parse("""{"provider":"copilot"}""").RootElement;

        var result = MergeVia.Heartbeat(defaults, null, agentObj);

        result.ShouldNotBeNull();
        result!.Enabled.ShouldBeTrue();
        result.IntervalMinutes.ShouldBe(15);
    }

    // -------------------------------------------------------------------------
    // Issue #2213 - pass-through fields must survive merge when defaults present
    // -------------------------------------------------------------------------

    [Fact]
    public void Merge_WithDefaults_PreservesEmoji()
    {
        var defaults = new AgentDefaultsConfig
        {
            Memory = new MemoryAgentConfig { Enabled = true, Indexing = "auto" }
        };
        var agent = new AgentDefinitionConfig
        {
            Provider = "copilot",
            Model = "gpt-4.1",
            Emoji = "\uD83D\uDD2E"
        };
        var raw = JsonDocument.Parse("""{"provider":"copilot","model":"gpt-4.1","emoji":"\uD83D\uDD2E"}""").RootElement;

        var result = MergeVia.Engine(defaults, agent, raw);

        result.Emoji.ShouldBe("\uD83D\uDD2E");
    }

    [Fact]
    public void Merge_WithDefaults_PreservesCacheRetentionDateTimeInjectionKindAndShellCommand()
    {
        var defaults = new AgentDefaultsConfig
        {
            Memory = new MemoryAgentConfig { Enabled = true, Indexing = "auto" }
        };
        var agent = new AgentDefinitionConfig
        {
            Provider = "copilot",
            Model = "gpt-4.1",
            CacheRetention = BotNexus.Agent.Providers.Core.Models.CacheRetention.Long,
            DateTimeInjection = new DateTimeInjectionConfig(),
            Kind = BotNexus.Domain.World.AgentKind.SubAgent,
            ShellCommand = ["pwsh", "-NoProfile"]
        };

        var result = MergeVia.Engine(defaults, agent, agentRawElement: null);

        result.CacheRetention.ShouldBe(BotNexus.Agent.Providers.Core.Models.CacheRetention.Long);
        // Value equality, not reference identity: the engine merges DOCUMENTS and binds once at the
        // end, so every nested object is a fresh instance. The old merger copied object references
        // through for pass-through fields, which is why this asserted ShouldBeSameAs. What the test
        // is actually protecting - that the field is not dropped - is unchanged.
        result.DateTimeInjection.ShouldNotBeNull();
        result.Kind.ShouldBe(BotNexus.Domain.World.AgentKind.SubAgent);
        result.ShellCommand.ShouldBe(["pwsh", "-NoProfile"]);
    }

    /// <summary>
    /// Reflection guard (#2213 proposed fix step 2): every simple pass-through property on
    /// <see cref="AgentDefinitionConfig"/> must survive Merge() when a non-null defaults object
    /// is supplied. Prevents future field additions from silently regressing the allow-list.
    /// Deep-merged structural fields (memory, heartbeat, fileAccess, toolIds, toolTimeoutSeconds)
    /// have dedicated scenario tests above and are excluded here.
    /// </summary>
    [Fact]
    public void Merge_DoesNotDropAnyAgentPassThroughField()
    {
        var mergedFields = new HashSet<string>
        {
            nameof(AgentDefinitionConfig.Memory),
            nameof(AgentDefinitionConfig.Heartbeat),
            nameof(AgentDefinitionConfig.FileAccess),
            nameof(AgentDefinitionConfig.ToolIds),
            nameof(AgentDefinitionConfig.ToolTimeoutSeconds),
        };

        var agent = new AgentDefinitionConfig();
        var props = typeof(AgentDefinitionConfig).GetProperties(
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);

        // Assign a distinctive non-default value to every writable property so a dropped
        // field surfaces as null/default after merge.
        foreach (var p in props)
        {
            if (!p.CanWrite || mergedFields.Contains(p.Name))
                continue;
            p.SetValue(agent, SampleValueFor(p.PropertyType));
        }

        var defaults = new AgentDefaultsConfig
        {
            Memory = new MemoryAgentConfig { Enabled = true, Indexing = "auto" }
        };

        var result = MergeVia.Engine(defaults, agent, agentRawElement: null);

        foreach (var p in props)
        {
            if (!p.CanWrite || mergedFields.Contains(p.Name))
                continue;
            var expected = p.GetValue(agent);
            var actual = p.GetValue(result);

            // The engine merges DOCUMENTS and binds once at the end, so every reference-typed value
            // is a fresh instance rather than the caller's object passed through. The old merger
            // copied references for pass-through fields, which is why a plain ShouldBe worked.
            //
            // Compare serialised JSON for anything without value equality - JsonElement and the
            // nested config classes alike. That is what this guard actually protects: the field
            // SURVIVED the merge rather than being silently dropped (#2213). Comparing identity
            // would now assert an implementation detail of the old merger, not the property.
            if (expected is not null && actual is not null && !expected.Equals(actual))
            {
                var expectedJson = JsonSerializer.Serialize(expected);
                var actualJson = JsonSerializer.Serialize(actual);
                actualJson.ShouldBe(
                    expectedJson,
                    $"Field '{p.Name}' was dropped or altered by the merge (regression of #2213).");
                continue;
            }

            actual.ShouldBe(expected, $"Field '{p.Name}' was dropped by Merge() (regression of #2213).");
        }
    }

    private static object? SampleValueFor(Type t)
    {
        var underlying = Nullable.GetUnderlyingType(t) ?? t;
        if (underlying == typeof(string)) return "sample";
        if (underlying == typeof(bool)) return true;
        if (underlying == typeof(int)) return 42;
        if (underlying == typeof(string[])) return new[] { "a", "b" };
        if (underlying == typeof(List<string>)) return new List<string> { "a", "b" };
        if (underlying.IsEnum) return Enum.GetValues(underlying).GetValue(underlying == typeof(BotNexus.Domain.World.AgentKind) ? 1 : 0);
        if (underlying == typeof(JsonElement)) return JsonDocument.Parse("""{"k":"v"}""").RootElement;
        // Reference config sub-objects: any distinct instance is enough to detect a drop.
        return Activator.CreateInstance(underlying);
    }
    private static void SetPromptInjection(MemoryAgentConfig config, string value)
    {
        var property = typeof(MemoryAgentConfig).GetProperty("PromptInjection");
        property.ShouldNotBeNull("MemoryAgentConfig.PromptInjection should exist for memory prompt-injection merge behavior.");
        property!.SetValue(config, value);
    }

    private static string GetPromptInjection(MemoryAgentConfig config)
    {
        var property = typeof(MemoryAgentConfig).GetProperty("PromptInjection");
        property.ShouldNotBeNull("MemoryAgentConfig.PromptInjection should exist for memory prompt-injection merge behavior.");
        return property!.GetValue(config)?.ToString() ?? string.Empty;
    }
}



