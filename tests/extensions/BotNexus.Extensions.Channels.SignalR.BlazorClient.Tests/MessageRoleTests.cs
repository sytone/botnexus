using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;
using Shouldly;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

/// <summary>
/// The single table test for the role vocabulary owner extracted in #3456.
/// </summary>
/// <remarks>
/// <para>
/// This file is deliberately the ONLY mapping table in the repository. Before the extraction the
/// same rule was re-tested in four places implicitly and re-derived by hand in both surfaces; the
/// point of the collapse is defeated if a near-duplicate table reappears in either surface suite.
/// Those suites keep their rendering assertions only.
/// </para>
/// <para>
/// Rows tagged <c>PARITY</c> pin behaviour that all four pre-existing mappers already agreed on.
/// Rows tagged <c>DELTA</c> pin the two intentional changes, asserted explicitly so neither can
/// arrive silently.
/// </para>
/// </remarks>
public sealed class MessageRoleTests
{
    // PARITY: every pre-existing mapper produced exactly this casing for the four known roles,
    // case-insensitively.
    [Theory]
    [InlineData("user", "User")]
    [InlineData("User", "User")]
    [InlineData("USER", "User")]
    [InlineData("assistant", "Assistant")]
    [InlineData("Assistant", "Assistant")]
    [InlineData("ASSISTANT", "Assistant")]
    [InlineData("tool", "Tool")]
    [InlineData("Tool", "Tool")]
    [InlineData("system", "System")]
    [InlineData("System", "System")]
    [InlineData("error", "Error")]
    [InlineData("Error", "Error")]
    public void Normalize_maps_known_roles_to_canonical_casing(string input, string expected) =>
        MessageRole.Normalize(input).ShouldBe(expected);

    // PARITY: GatewayEventHandler.ResolveFlushRole and ClientStateStore's inline switch both
    // mapped a null or blank pending role to Assistant, because the pending role is absent for
    // every ordinary streamed reply and such a reply is an agent message.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    public void Normalize_maps_blank_to_assistant(string? input) =>
        MessageRole.Normalize(input).ShouldBe(MessageRole.Assistant);

    // DELTA 1 (intentional, #3456): AgentInteractionService.MapRole and PortalLoadService.MapRole
    // previously returned "" verbatim for a blank role, because neither carried the "" arm the
    // streaming mappers had. Both history-replay paths now share the assistant default. This test
    // exists so that unification is a recorded decision rather than a silent side effect.
    [Fact]
    public void Normalize_unifies_the_blank_arm_the_history_replay_mappers_previously_lacked()
    {
        // Pre-#3456 AgentInteractionService.MapRole("") and PortalLoadService.MapRole("")
        // both returned string.Empty via their `_ => role` fallback.
        MessageRole.Normalize(string.Empty).ShouldNotBe(string.Empty);
        MessageRole.Normalize(string.Empty).ShouldBe(MessageRole.Assistant);
    }

    // DELTA 2 (intentional, #3456): the four mappers disagreed on an UNRECOGNISED role.
    // AgentInteractionService/PortalLoadService returned the original string untouched, while
    // GatewayEventHandler/ClientStateStore returned the LOWER-CASED, TRIMMED string, because their
    // `var other => other` arm bound the already-transformed switch subject. The owner keeps the
    // caller's casing. Unknown roles are NOT collapsed to Assistant: a future gateway role
    // rendered as an agent bubble would misattribute a platform message to the agent, whereas an
    // unstyled bubble is merely cosmetic. See MessageRole's type-level remarks.
    [Theory]
    [InlineData("notification", "notification")]
    [InlineData("Notification", "Notification")]
    [InlineData("MODERATION", "MODERATION")]
    [InlineData("thinking", "thinking")]
    public void Normalize_preserves_an_unrecognised_role_verbatim(string input, string expected)
    {
        MessageRole.Normalize(input).ShouldBe(expected);
        MessageRole.Normalize(input).ShouldNotBe(MessageRole.Assistant);
    }

    [Theory]
    [InlineData("  assistant  ", "Assistant")]
    [InlineData(" user ", "User")]
    public void Normalize_ignores_surrounding_whitespace(string input, string expected) =>
        MessageRole.Normalize(input).ShouldBe(expected);

    [Fact]
    public void Normalize_is_idempotent_over_the_whole_vocabulary()
    {
        foreach (var raw in new[] { "user", "assistant", "system", "tool", "error", "unknown", "", "  " })
        {
            var once = MessageRole.Normalize(raw);
            MessageRole.Normalize(once).ShouldBe(once);
        }
    }

    [Theory]
    [InlineData("Assistant", true)]
    [InlineData("assistant", true)]
    [InlineData("ASSISTANT", true)]
    [InlineData("User", false)]
    [InlineData("System", false)]
    [InlineData("Tool", false)]
    [InlineData("notification", false)]
    public void IsAssistant_tests_the_stored_token(string role, bool expected) =>
        MessageRole.IsAssistant(role).ShouldBe(expected);

    [Theory]
    [InlineData("User", true)]
    [InlineData("user", true)]
    [InlineData("USER", true)]
    [InlineData("Assistant", false)]
    [InlineData("System", false)]
    public void IsUser_tests_the_stored_token(string role, bool expected) =>
        MessageRole.IsUser(role).ShouldBe(expected);

    // The predicates are deliberately NOT Normalize composed with equality. Normalize picks the
    // role to STORE on a message being created, where blank means "an ordinary streamed reply";
    // the predicates ask what an ALREADY STORED message is, where blank means "no role recorded".
    // Collapsing the two would flip mobile's ShouldRenderAsMarkdown("") from false to true and
    // push a role-less message through the Markdown pipeline.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Predicates_reject_a_blank_role_even_though_Normalize_defaults_it_to_assistant(string? role)
    {
        MessageRole.IsAssistant(role).ShouldBeFalse();
        MessageRole.IsUser(role).ShouldBeFalse();
        MessageRole.Normalize(role).ShouldBe(MessageRole.Assistant);
    }

    // PARITY with mobile Chat.razor:1018, which returned "user" for a user message and
    // "assistant" for literally everything else -- the bubble has two sides.
    [Theory]
    [InlineData("User", "user")]
    [InlineData("user", "user")]
    [InlineData("Assistant", "assistant")]
    [InlineData("System", "assistant")]
    [InlineData("Tool", "assistant")]
    [InlineData("Error", "assistant")]
    [InlineData("notification", "assistant")]
    [InlineData(null, "assistant")]
    [InlineData("", "assistant")]
    public void CssRole_returns_the_bubble_side_token(string? role, string expected) =>
        MessageRole.CssRole(role).ShouldBe(expected);

    [Fact]
    public void Canonical_constants_are_the_normalized_form_of_themselves()
    {
        foreach (var canonical in new[]
                 {
                     MessageRole.Assistant, MessageRole.User, MessageRole.System,
                     MessageRole.Tool, MessageRole.Error,
                 })
        {
            MessageRole.Normalize(canonical).ShouldBe(canonical);
        }
    }
}
