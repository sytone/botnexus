using BotNexus.Extensions.Channels.SignalR.BlazorClient.Components;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;
using AskUserInputType = BotNexus.Gateway.Abstractions.Models.AskUserInputType;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

/// <summary>
/// Regression suite for #2792: the Submit button enabled itself from one derivation of
/// "is there an answer" while <c>SubmitAsync</c> built the payload from a second, independent
/// one. For <c>ChoiceOrFreeForm</c> the two disagreed and the client posted
/// <c>(freeFormText: null, selectedValues: null, wasCancelled: false)</c>, which the gateway
/// renders as <c>(no content provided)</c>.
///
/// These tests pin the observable contract at the seam the user actually drives: the rendered
/// DOM in, the emitted <see cref="AskUserPromptSubmission"/> out.
/// </summary>
public sealed class AskUserSubmissionPayloadTests : IDisposable
{
    private readonly BunitContext _ctx = new();

    public AskUserSubmissionPayloadTests() => _ctx.JSInterop.Mode = JSRuntimeMode.Loose;

    public void Dispose() => _ctx.Dispose();

    // AC1: choice_or_free_form + a single radio selected + NO text must carry the radio value.
    // This is the exact reported shape and the one the old ternary dropped on the floor.
    [Fact]
    public async Task ChoiceOrFreeForm_with_radio_selected_and_no_text_submits_the_selected_value()
    {
        AskUserPromptSubmission? submission = null;
        var cut = RenderPrompt(MakePrompt("ChoiceOrFreeForm"), payload => submission = payload);

        await cut.InvokeAsync(() => cut.FindAll("input[type='radio']")[1].Change(true));
        await cut.InvokeAsync(() => cut.Find(".ask-user-actions .send-btn").Click());

        Assert.NotNull(submission);
        Assert.False(submission.Cancelled);
        Assert.NotNull(submission.SelectedValues);
        Assert.Contains("b", submission.SelectedValues);
        Assert.Null(submission.FreeFormText);
    }

    // AC1 (companion): the button must not merely be enabled - the enable state and the payload
    // must move together. A prompt that enables Submit MUST produce a non-empty payload.
    [Fact]
    public async Task ChoiceOrFreeForm_submit_button_enables_only_when_a_payload_would_be_produced()
    {
        AskUserPromptSubmission? submission = null;
        var cut = RenderPrompt(MakePrompt("ChoiceOrFreeForm"), payload => submission = payload);

        var button = cut.Find(".ask-user-actions .send-btn");
        Assert.True(button.HasAttribute("disabled"));

        await cut.InvokeAsync(() => cut.FindAll("input[type='radio']")[0].Change(true));

        Assert.False(cut.Find(".ask-user-actions .send-btn").HasAttribute("disabled"));

        await cut.InvokeAsync(() => cut.Find(".ask-user-actions .send-btn").Click());

        Assert.NotNull(submission);
        Assert.True(HasContent(submission), "Submit was enabled but produced an empty payload.");
    }

    // AC2: single_choice with an option selected submits that option.
    [Fact]
    public async Task SingleChoice_with_option_selected_submits_that_option()
    {
        AskUserPromptSubmission? submission = null;
        var cut = RenderPrompt(MakePrompt("SingleChoice"), payload => submission = payload);

        await cut.InvokeAsync(() => cut.FindAll("input[type='radio']")[1].Change(true));
        await cut.InvokeAsync(() => cut.Find(".ask-user-actions .send-btn").Click());

        Assert.NotNull(submission);
        Assert.False(submission.Cancelled);
        Assert.NotNull(submission.SelectedValues);
        Assert.Equal(["b"], submission.SelectedValues);
    }

    [Fact]
    public async Task MultipleChoice_with_checkboxes_selected_submits_all_of_them()
    {
        AskUserPromptSubmission? submission = null;
        var cut = RenderPrompt(MakePrompt("MultipleChoice"), payload => submission = payload);

        var boxes = cut.FindAll("input[type='checkbox']");
        await cut.InvokeAsync(() => boxes[0].Change(true));
        await cut.InvokeAsync(() => cut.FindAll("input[type='checkbox']")[1].Change(true));
        await cut.InvokeAsync(() => cut.Find(".ask-user-actions .send-btn").Click());

        Assert.NotNull(submission);
        Assert.NotNull(submission.SelectedValues);
        Assert.Equal(["a", "b"], submission.SelectedValues.Order(StringComparer.Ordinal).ToArray());
    }

    /// <summary>
    /// AC3: no submission path may emit <c>(null, null, wasCancelled: false)</c>, asserted for
    /// EVERY input type. The type list is enumerated from <see cref="AskUserInputType"/> itself
    /// rather than hand-written: a hand-maintained list is precisely the drift this issue exists
    /// to remove, and a new enum member must fail here on the day it is added.
    ///
    /// The interaction is likewise derived from the rendered DOM, not from a per-type lookup
    /// table: whatever control the component chose to render is what a user can touch.
    /// </summary>
    public static TheoryData<string> AllInputTypes()
    {
        var data = new TheoryData<string>();
        foreach (var value in Enum.GetNames<AskUserInputType>())
            data.Add(value);
        return data;
    }

    [Theory]
    [MemberData(nameof(AllInputTypes))]
    public async Task No_input_type_can_submit_an_empty_answer(string inputType)
    {
        AskUserPromptSubmission? submission = null;
        var cut = RenderPrompt(MakePrompt(inputType), payload => submission = payload);

        // Drive the first control the component actually rendered, exactly as a user would.
        var radios = cut.FindAll("input[type='radio']");
        var boxes = cut.FindAll("input[type='checkbox']");
        if (radios.Count > 0)
            await cut.InvokeAsync(() => cut.FindAll("input[type='radio']")[0].Change(true));
        else if (boxes.Count > 0)
            await cut.InvokeAsync(() => cut.FindAll("input[type='checkbox']")[0].Change(true));
        else
            cut.Find(".ask-user-free-form").Input("typed answer");

        var button = cut.Find(".ask-user-actions .send-btn");
        Assert.False(button.HasAttribute("disabled"),
            $"Submit stayed disabled for '{inputType}' after the user answered the rendered control.");

        await cut.InvokeAsync(() => cut.Find(".ask-user-actions .send-btn").Click());

        Assert.NotNull(submission);
        Assert.False(submission.Cancelled);
        Assert.True(
            HasContent(submission),
            $"'{inputType}' produced freeFormText=null AND selectedValues=null with wasCancelled=false.");
    }

    [Theory]
    [MemberData(nameof(AllInputTypes))]
    public async Task No_input_type_can_submit_before_the_user_answers(string inputType)
    {
        AskUserPromptSubmission? submission = null;
        var cut = RenderPrompt(MakePrompt(inputType), payload => submission = payload);

        Assert.True(cut.Find(".ask-user-actions .send-btn").HasAttribute("disabled"),
            $"Submit was enabled for '{inputType}' with nothing answered.");

        // Cancel remains the only way out of an unanswered prompt, and it is explicitly flagged.
        await cut.InvokeAsync(() => cut.Find(".ask-user-actions .cancel-btn").Click());

        Assert.NotNull(submission);
        Assert.True(submission.Cancelled,
            $"'{inputType}' emitted an empty submission that was not marked cancelled.");
    }

    private static bool HasContent(AskUserPromptSubmission submission) =>
        !string.IsNullOrWhiteSpace(submission.FreeFormText)
        || submission.SelectedValues is { Length: > 0 };

    private static AskUserPromptState MakePrompt(string inputType) => new()
    {
        RequestId = "req-1",
        ConversationId = "conv-1",
        Prompt = "Merge to main and deploy the portal now?",
        InputType = inputType,
        AllowFreeForm = string.Equals(inputType, "FreeForm", StringComparison.Ordinal),
        AllowMultiple = string.Equals(inputType, "MultipleChoice", StringComparison.Ordinal),
        Choices =
        [
            new AskUserChoiceState("a", "Yes, deploy", null),
            new AskUserChoiceState("b", "No, hold", null)
        ]
    };

    private IRenderedComponent<AskUserPrompt> RenderPrompt(
        AskUserPromptState prompt,
        Action<AskUserPromptSubmission> onSubmit)
        => _ctx.Render<AskUserPrompt>(parameters =>
        {
            parameters.Add(component => component.Prompt, prompt);
            parameters.Add(component => component.OnSubmit,
                EventCallback.Factory.Create<AskUserPromptSubmission>(this, onSubmit));
        });
}
