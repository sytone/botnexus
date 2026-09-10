using BotNexus.Gateway.Agents;

namespace BotNexus.Gateway.Tests.Agents;

/// <summary>
/// The agent persona in the system prompt.
/// </summary>
/// <remarks>
/// Before this, <c>AgentDescriptor.Description</c> was display-only: it appeared on portal cards and
/// changed nothing about behaviour, so two agents with different descriptions produced identical
/// prompts. These pin the opposite — that what an operator writes about an agent is what the agent
/// is told, and that an agent with nothing written keeps the prompt it had.
/// </remarks>
public sealed class AgentPersonaPromptTests
{
    private static string BuildWith(AgentPersona? persona) =>
        SystemPromptBuilder.Build(new SystemPromptParams
        {
            WorkspaceDir = Path.Combine(Path.GetTempPath(), "persona-test-workspace"),
            ToolNames = ["read", "write"],
            PromptMode = PromptMode.Full,
            Persona = persona,
            Runtime = new RuntimeInfo { AgentId = "test-agent", Channel = "signalr" },
        });

    [Fact]
    public void A_full_persona_reaches_the_prompt()
    {
        var prompt = BuildWith(new AgentPersona(
            "Gantry Manager",
            "keeps the deployment gantry healthy",
            "Watches deploys and reports failures with the failing step.",
            "Never restarts production without being asked."));

        prompt.ShouldContain("Gantry Manager");
        prompt.ShouldContain("keeps the deployment gantry healthy");
        prompt.ShouldContain("Watches deploys and reports failures with the failing step.");
        prompt.ShouldContain("Never restarts production without being asked.");
    }

    [Fact]
    public void Boundaries_are_stated_as_limits_not_folded_into_the_description()
    {
        // The point of a separate field: "what it must not do" is the part most likely to be skimmed
        // past when buried in prose, and the part where skimming costs most.
        var prompt = BuildWith(new AgentPersona(
            Boundaries: "Never delete a volume."));

        prompt.ShouldContain("Stay inside these limits:");
        prompt.ShouldContain("Never delete a volume.");
    }

    [Fact]
    public void An_agent_with_no_persona_renders_no_persona_block()
    {
        // Every agent that nobody configures must keep exactly the prompt it had.
        var prompt = BuildWith(null);

        prompt.ShouldNotContain("## Who you are");
    }

    [Fact]
    public void A_persona_of_only_blank_strings_renders_nothing()
    {
        // A config field someone cleared should not leave an empty heading behind.
        var prompt = BuildWith(new AgentPersona("   ", "", null, "  "));

        prompt.ShouldNotContain("## Who you are");
    }

    [Fact]
    public void A_partial_persona_renders_only_what_was_written()
    {
        var prompt = BuildWith(new AgentPersona("Harbor Relay", Responsibility: "routes inbound mail"));

        prompt.ShouldContain("Harbor Relay");
        prompt.ShouldContain("routes inbound mail");
        prompt.ShouldNotContain("Stay inside these limits:");
    }

    [Fact]
    public void The_persona_frames_the_prompt_rather_than_trailing_it()
    {
        // It is registered first deliberately: everything after it is guidance about HOW to work,
        // and that should be read in the light of what this agent is for.
        var prompt = BuildWith(new AgentPersona("Gantry Manager", "keeps the gantry healthy"));

        var persona = prompt.IndexOf("Who you are", StringComparison.Ordinal);
        var tooling = prompt.IndexOf("<tooling>", StringComparison.Ordinal);

        persona.ShouldBeGreaterThanOrEqualTo(0);
        tooling.ShouldBeGreaterThanOrEqualTo(0);
        persona.ShouldBeLessThan(tooling);
    }

    [Fact]
    public void Two_agents_with_different_personas_get_different_prompts()
    {
        // The property that was false before this existed.
        var first = BuildWith(new AgentPersona("Gantry Manager", "keeps the gantry healthy"));
        var second = BuildWith(new AgentPersona("Harbor Relay", "routes inbound mail"));

        first.ShouldNotBe(second);
    }

    [Fact]
    public void Surrounding_whitespace_is_trimmed()
    {
        var prompt = BuildWith(new AgentPersona("  Gantry Manager  ", "  keeps the gantry healthy  "));

        prompt.ShouldContain("You are Gantry Manager.");
        prompt.ShouldContain("You own: keeps the gantry healthy");
    }

    // ── precedence against an agent's own authored prompt ─────────────────────
    // The plan that introduced the persona asked for this to be DECIDED rather than left to fall
    // out of section ordering. It is decided: the persona goes first, the hand-written prompt goes
    // last, so on disagreement the prompt file wins.

    [Fact]
    public void The_persona_comes_before_the_agents_own_authored_prompt()
    {
        // Ordering IS the precedence rule here, so it is worth pinning rather than trusting two
        // integer constants to stay on the right side of each other.
        var prompt = SystemPromptBuilder.Build(new SystemPromptParams
        {
            WorkspaceDir = Path.Combine(Path.GetTempPath(), "persona-precedence-workspace"),
            ToolNames = ["read"],
            PromptMode = PromptMode.Full,
            Persona = new AgentPersona("Gantry Manager", "keeps the gantry healthy", null, null),
            ExtraSystemPrompt = "AUTHORED-PROMPT-MARKER: you are actually a release auditor.",
            Runtime = new RuntimeInfo { AgentId = "test-agent", Channel = "signalr" },
        });

        var personaAt = prompt.IndexOf("Gantry Manager", StringComparison.Ordinal);
        var authoredAt = prompt.IndexOf("AUTHORED-PROMPT-MARKER", StringComparison.Ordinal);

        personaAt.ShouldBeGreaterThanOrEqualTo(0);
        authoredAt.ShouldBeGreaterThanOrEqualTo(0);
        personaAt.ShouldBeLessThan(
            authoredAt,
            "the persona is three fields an operator changes in seconds; an authored prompt is "
            + "something someone sat down and wrote, so it comes later and wins on disagreement");
    }

    [Fact]
    public void An_authored_prompt_survives_alongside_a_persona_rather_than_being_replaced_by_it()
    {
        // The failure this guards against is the persona quietly displacing a prompt file, which is
        // exactly what the plan warned about.
        var prompt = SystemPromptBuilder.Build(new SystemPromptParams
        {
            WorkspaceDir = Path.Combine(Path.GetTempPath(), "persona-precedence-workspace"),
            ToolNames = ["read"],
            PromptMode = PromptMode.Full,
            Persona = new AgentPersona("Gantry Manager", "keeps the gantry healthy", null, null),
            ExtraSystemPrompt = "AUTHORED-PROMPT-MARKER",
            Runtime = new RuntimeInfo { AgentId = "test-agent", Channel = "signalr" },
        });

        prompt.ShouldContain("AUTHORED-PROMPT-MARKER");
        prompt.ShouldContain("Gantry Manager");
    }
}
