using BotNexus.Cli.Wizard;
using BotNexus.Cli.Wizard.Steps;

namespace BotNexus.Cli.Tests.Wizard;

public class WizardRunnerTests
{
    [Fact]
    public async Task Empty_wizard_completes_immediately()
    {
        var runner = new WizardRunner();
        var result = await runner.RunAsync();
        result.Outcome.ShouldBe(WizardOutcome.Completed);
    }

    [Fact]
    public async Task Runs_steps_in_order()
    {
        var order = new List<string>();

        var runner = new WizardRunner();
        runner.AddStep(new ActionStep("step1", (_, _) => { order.Add("step1"); return Task.CompletedTask; }));
        runner.AddStep(new ActionStep("step2", (_, _) => { order.Add("step2"); return Task.CompletedTask; }));
        runner.AddStep(new ActionStep("step3", (_, _) => { order.Add("step3"); return Task.CompletedTask; }));

        var result = await runner.RunAsync();
        result.Outcome.ShouldBe(WizardOutcome.Completed);
        order.ShouldBe(["step1", "step2", "step3"]);
    }

    [Fact]
    public async Task Check_step_can_abort()
    {
        var order = new List<string>();

        var runner = new WizardRunner();
        runner.AddStep(new ActionStep("step1", (_, _) => { order.Add("step1"); return Task.CompletedTask; }));
        runner.AddStep(new CheckStep("check", (_, _) => Task.FromResult(StepResult.Abort())));
        runner.AddStep(new ActionStep("step3", (_, _) => { order.Add("step3"); return Task.CompletedTask; }));

        var result = await runner.RunAsync();
        result.Outcome.ShouldBe(WizardOutcome.Aborted);
        order.ShouldBe(["step1"]);
    }

    [Fact]
    public async Task GoTo_jumps_to_named_step()
    {
        var order = new List<string>();

        var runner = new WizardRunner();
        runner.AddStep(new ActionStep("step1", (_, _) => { order.Add("step1"); return Task.CompletedTask; }));
        runner.AddStep(new CheckStep("branch", (_, _) => Task.FromResult(StepResult.GoTo("step4"))));
        runner.AddStep(new ActionStep("step3", (_, _) => { order.Add("step3"); return Task.CompletedTask; }));
        runner.AddStep(new ActionStep("step4", (_, _) => { order.Add("step4"); return Task.CompletedTask; }));

        var result = await runner.RunAsync();
        result.Outcome.ShouldBe(WizardOutcome.Completed);
        order.ShouldBe(["step1", "step4"]);
    }

    [Fact]
    public async Task GoTo_invalid_step_throws()
    {
        var runner = new WizardRunner();
        runner.AddStep(new CheckStep("branch", (_, _) => Task.FromResult(StepResult.GoTo("nonexistent"))));

        var ex = await Should.ThrowAsync<InvalidOperationException>(() => runner.RunAsync());
        ex.Message.ShouldContain("nonexistent");
    }

    [Fact]
    public async Task Context_is_shared_across_steps()
    {
        var runner = new WizardRunner();
        runner.AddStep(new ActionStep("set", (ctx, _) => { ctx.Set("key", "value"); return Task.CompletedTask; }));
        runner.AddStep(new CheckStep("read", (ctx, _) =>
        {
            ctx.Get<string>("key").ShouldBe("value");
            return Task.FromResult(StepResult.Continue());
        }));

        var result = await runner.RunAsync();
        result.Outcome.ShouldBe(WizardOutcome.Completed);
        result.Context.Get<string>("key").ShouldBe("value");
    }

    [Fact]
    public async Task Cancellation_is_respected()
    {
        var cts = new CancellationTokenSource();
        cts.Cancel();

        var runner = new WizardRunner();
        runner.AddStep(new ActionStep("step1", (_, _) => Task.CompletedTask));

        await Should.ThrowAsync<OperationCanceledException>(() => runner.RunAsync(cancellationToken: cts.Token));
    }

    [Fact]
    public async Task Existing_context_is_used_when_provided()
    {
        var ctx = new WizardContext();
        ctx.Set("pre", "existing");

        var runner = new WizardRunner();
        runner.AddStep(new CheckStep("verify", (c, _) =>
        {
            c.Get<string>("pre").ShouldBe("existing");
            return Task.FromResult(StepResult.Continue());
        }));

        var result = await runner.RunAsync(ctx);
        result.Outcome.ShouldBe(WizardOutcome.Completed);
    }

    [Fact]
    public async Task GoTo_can_loop_with_exit_condition()
    {
        var runner = new WizardRunner();
        runner.AddStep(new ActionStep("init", (ctx, _) => { ctx.Set("count", 0); return Task.CompletedTask; }));
        runner.AddStep(new ActionStep("increment", (ctx, _) =>
        {
            ctx.Set("count", ctx.Get<int>("count") + 1);
            return Task.CompletedTask;
        }));
        runner.AddStep(new CheckStep("loop-check", (ctx, _) =>
        {
            return Task.FromResult(ctx.Get<int>("count") < 3
                ? StepResult.GoTo("increment")
                : StepResult.Continue());
        }));

        var result = await runner.RunAsync();
        result.Outcome.ShouldBe(WizardOutcome.Completed);
        result.Context.Get<int>("count").ShouldBe(3);
    }
}
