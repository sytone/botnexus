using BotNexus.Extensions.Channels.SignalR.BlazorClient.Components;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;
using Microsoft.AspNetCore.Components;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

public sealed class AskUserPromptTests : IDisposable
{
    private readonly BunitContext _ctx = new();

    public void Dispose() => _ctx.Dispose();

    [Fact]
    public void Renders_free_form_prompt_with_textarea()
    {
        var cut = RenderPrompt(new AskUserPromptState
        {
            RequestId = "req-1",
            ConversationId = "conv-1",
            Prompt = "Tell me more",
            InputType = "FreeForm",
            AllowFreeForm = true
        });

        cut.Find(".ask-user-free-form");
        Assert.Contains("Tell me more", cut.Markup);
    }

    [Fact]
    public void Renders_single_choice_prompt_with_radio_buttons()
    {
        var cut = RenderPrompt(new AskUserPromptState
        {
            RequestId = "req-1",
            ConversationId = "conv-1",
            Prompt = "Pick one",
            InputType = "SingleChoice",
            Choices =
            [
                new AskUserChoiceState("a", "A", null),
                new AskUserChoiceState("b", "B", null)
            ]
        });

        Assert.Equal(2, cut.FindAll("input[type='radio']").Count);
    }

    [Fact]
    public async Task Submit_emits_payload()
    {
        AskUserPromptSubmission? submission = null;
        var cut = RenderPrompt(new AskUserPromptState
        {
            RequestId = "req-1",
            ConversationId = "conv-1",
            Prompt = "Tell me more",
            InputType = "FreeForm",
            AllowFreeForm = true
        }, onSubmit: payload => submission = payload);

        cut.Find(".ask-user-free-form").Input("Details");
        await cut.InvokeAsync(() => cut.Find(".ask-user-actions .send-btn").Click());

        Assert.NotNull(submission);
        Assert.False(submission.Cancelled);
        Assert.Equal("Details", submission.FreeFormText);
    }

    [Fact]
    public async Task Cancel_emits_cancelled_payload()
    {
        AskUserPromptSubmission? submission = null;
        var cut = RenderPrompt(new AskUserPromptState
        {
            RequestId = "req-1",
            ConversationId = "conv-1",
            Prompt = "Tell me more",
            InputType = "FreeForm",
            AllowFreeForm = true
        }, onSubmit: payload => submission = payload);

        await cut.InvokeAsync(() => cut.Find(".ask-user-actions .cancel-btn").Click());

        Assert.NotNull(submission);
        Assert.True(submission.Cancelled);
    }

    private IRenderedComponent<AskUserPrompt> RenderPrompt(
        AskUserPromptState prompt,
        Action<AskUserPromptSubmission>? onSubmit = null)
        => _ctx.Render<AskUserPrompt>(parameters =>
        {
            parameters.Add(component => component.Prompt, prompt);
            if (onSubmit is not null)
            {
                parameters.Add(component => component.OnSubmit,
                    EventCallback.Factory.Create<AskUserPromptSubmission>(this, onSubmit));
            }
        });
}
