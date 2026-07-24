using System.Text.Json;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Models;

namespace BotNexus.Gateway.Tests.Services;

/// <summary>
/// Covers the shared, channel-agnostic <c>ask_user</c> prompt reconciliation moved out of the
/// SignalR Blazor client by #2322. These assertions mirror the behaviour the client factory
/// tests already pin, but exercise it through the domain surface any channel can reach.
/// </summary>
public sealed class AskUserPromptNormalizerTests
{
    private static IReadOnlyDictionary<string, JsonElement> Metadata(string json)
        => JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;

    [Fact]
    public void Reconcile_prefers_flattened_metadata_over_the_structured_fallback()
    {
        var fallback = new AskUserPrompt
        {
            RequestId = "from-payload",
            ConversationId = "conv-payload",
            Prompt = "payload prompt",
            InputType = "FreeForm"
        };

        var metadata = Metadata("""
            {"requestId":"from-metadata","conversationId":"conv-metadata","prompt":"metadata prompt","inputType":"SingleChoice"}
            """);

        AskUserPromptNormalizer.TryReconcile(metadata, fallback, out var prompt).ShouldBeTrue();
        prompt!.RequestId.ShouldBe("from-metadata");
        prompt.ConversationId.ShouldBe("conv-metadata");
        prompt.Prompt.ShouldBe("metadata prompt");
        prompt.InputType.ShouldBe("SingleChoice");
    }

    [Fact]
    public void Reconcile_falls_back_to_the_structured_payload_when_metadata_is_absent()
    {
        var fallback = new AskUserPrompt
        {
            RequestId = "req-1",
            ConversationId = "conv-1",
            Prompt = "Pick one",
            InputType = "SingleChoice",
            Choices = [new AskUserPromptChoice("a", "Option A")],
            AllowFreeForm = true
        };

        AskUserPromptNormalizer.TryReconcile(metadata: null, fallback, out var prompt).ShouldBeTrue();
        prompt!.RequestId.ShouldBe("req-1");
        prompt.Choices.ShouldNotBeNull();
        prompt.Choices!.ShouldHaveSingleItem().Value.ShouldBe("a");
        prompt.AllowFreeForm.ShouldBeTrue();
    }

    [Fact]
    public void Reconcile_returns_false_when_required_fields_are_missing_from_both_sources()
    {
        AskUserPromptNormalizer.TryReconcile(
            Metadata("""{"prompt":"no request id, no input type"}"""),
            fallback: null,
            out var prompt).ShouldBeFalse();

        prompt.ShouldBeNull();
    }

    [Fact]
    public void Reconcile_parses_choices_supplied_as_an_embedded_json_string()
    {
        var metadata = Metadata("""
            {"requestId":"r","prompt":"p","inputType":"SingleChoice",
             "choices":"[{\"value\":\"prod\",\"label\":\"Production\"},{\"value\":\"staging\"}]"}
            """);

        AskUserPromptNormalizer.TryReconcile(metadata, fallback: null, out var prompt).ShouldBeTrue();
        prompt!.Choices!.Count.ShouldBe(2);
        prompt.Choices[0].Label.ShouldBe("Production");
        // A choice without a label falls back to its value so a channel never renders an empty button.
        prompt.Choices[1].Label.ShouldBe("staging");
    }

    [Fact]
    public void PersistedJson_rehydrates_a_prompt_for_a_channel_that_missed_the_live_event()
    {
        const string json = """
            {"requestId":"req-9","conversationId":"conv-9","prompt":"Continue?","inputType":"SingleChoice",
             "choices":[{"value":"yes","label":"Yes"},{"value":"no"}],"allowMultiple":false,
             "allowFreeForm":true,"timeout":"00:05:00"}
            """;

        AskUserPromptNormalizer.TryBuildFromPersistedJson(json, "conv-fallback", out var prompt).ShouldBeTrue();
        prompt!.RequestId.ShouldBe("req-9");
        prompt.ConversationId.ShouldBe("conv-9");
        prompt.Choices!.Count.ShouldBe(2);
        prompt.Choices[1].Label.ShouldBe("no");
        prompt.AllowFreeForm.ShouldBeTrue();
        prompt.ExpiresAt.ShouldNotBeNull();
    }

    [Fact]
    public void PersistedJson_binds_to_the_hydrating_conversation_when_the_payload_omits_its_own_id()
    {
        const string json = """{"requestId":"r","prompt":"p","inputType":"FreeForm"}""";

        AskUserPromptNormalizer.TryBuildFromPersistedJson(json, "conv-hydrating", out var prompt).ShouldBeTrue();
        prompt!.ConversationId.ShouldBe("conv-hydrating");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json at all")]
    [InlineData("""{"prompt":"missing request id and input type"}""")]
    public void PersistedJson_returns_false_for_missing_or_unusable_payloads(string? json)
        => AskUserPromptNormalizer.TryBuildFromPersistedJson(json, "conv-1", out _).ShouldBeFalse();

    [Fact]
    public void AskUserRequest_projects_onto_the_shared_prompt_model()
    {
        var request = new AskUserRequest
        {
            RequestId = "req-p",
            ConversationId = ConversationId.From("conv-p"),
            SessionId = SessionId.From("sess-p"),
            AgentId = AgentId.From("agent-p"),
            Prompt = "Pick",
            InputType = AskUserInputType.MultipleChoice,
            Choices = [new AskUserChoice { Value = "a" }, new AskUserChoice { Value = "b", Label = "Bee" }],
            AllowMultiple = true
        };

        var prompt = request.ToPrompt();

        prompt.RequestId.ShouldBe("req-p");
        prompt.ConversationId.ShouldBe("conv-p");
        prompt.InputType.ShouldBe("MultipleChoice");
        prompt.AllowMultiple.ShouldBeTrue();
        prompt.HasChoices.ShouldBeTrue();
        prompt.Choices![0].Label.ShouldBe("a");
        prompt.Choices[1].Label.ShouldBe("Bee");
    }

    [Fact]
    public void TextRenderer_matches_a_reply_by_ordinal_value_or_label()
    {
        var prompt = new AskUserPrompt
        {
            RequestId = "r",
            ConversationId = "c",
            Prompt = "Pick",
            InputType = "SingleChoice",
            Choices = [new AskUserPromptChoice("staging", "Staging"), new AskUserPromptChoice("prod", "Production")]
        };

        AskUserPromptTextRenderer.MatchChoice(prompt, "2").ShouldBe("prod");
        AskUserPromptTextRenderer.MatchChoice(prompt, "staging").ShouldBe("staging");
        AskUserPromptTextRenderer.MatchChoice(prompt, "production").ShouldBe("prod");
        AskUserPromptTextRenderer.MatchChoice(prompt, "99").ShouldBeNull();
        AskUserPromptTextRenderer.MatchChoice(prompt, "something else entirely").ShouldBeNull();
    }

    [Fact]
    public void TextRenderer_offers_multi_select_and_free_form_hints_when_the_prompt_allows_them()
    {
        var prompt = new AskUserPrompt
        {
            RequestId = "r",
            ConversationId = "c",
            Prompt = "Pick any",
            InputType = "MultipleChoice",
            Choices = [new AskUserPromptChoice("a", "A"), new AskUserPromptChoice("b", "B")],
            AllowMultiple = true,
            AllowFreeForm = true
        };

        var rendered = AskUserPromptTextRenderer.Render(prompt);

        rendered.ShouldContain("comma separated");
        rendered.ShouldContain("or type your own answer");
    }

    [Fact]
    public void TextRenderer_renders_a_bare_prompt_when_there_are_no_choices()
    {
        var prompt = new AskUserPrompt
        {
            RequestId = "r",
            ConversationId = "c",
            Prompt = "What is the deploy tag?",
            InputType = "FreeForm"
        };

        AskUserPromptTextRenderer.Render(prompt).ShouldBe("What is the deploy tag?");
    }
}
