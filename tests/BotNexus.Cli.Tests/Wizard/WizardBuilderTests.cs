using BotNexus.Cli.Wizard;
using BotNexus.Cli.Wizard.Steps;

namespace BotNexus.Cli.Tests.Wizard;

public class WizardBuilderTests
{
    [Fact]
    public async Task Builder_creates_runnable_wizard_with_check_and_action()
    {
        var wizard = new WizardBuilder()
            .Action("setup", (ctx, _) => { ctx.Set("ready", true); return Task.CompletedTask; })
            .Check("verify", (ctx, _) =>
            {
                return Task.FromResult(ctx.Get<bool>("ready")
                    ? StepResult.Continue()
                    : StepResult.Abort());
            })
            .Action("finish", (ctx, _) => { ctx.Set("done", true); return Task.CompletedTask; })
            .Build();

        var result = await wizard.RunAsync();
        result.Outcome.ShouldBe(WizardOutcome.Completed);
        result.Context.Get<bool>("done").ShouldBeTrue();
    }

    [Fact]
    public async Task Builder_supports_custom_steps()
    {
        var wizard = new WizardBuilder()
            .Step(new ActionStep("custom", (ctx, _) => { ctx.Set("custom", true); return Task.CompletedTask; }))
            .Build();

        var result = await wizard.RunAsync();
        result.Outcome.ShouldBe(WizardOutcome.Completed);
        result.Context.Get<bool>("custom").ShouldBeTrue();
    }
}
