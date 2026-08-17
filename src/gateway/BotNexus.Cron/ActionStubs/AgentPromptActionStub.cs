using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Triggers;
using BotNexus.Domain.Primitives;
using BotNexus.Domain.Text;
using BotNexus.Cron.Prompts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BotNexus.Cron.Actions;

#pragma warning disable CS1591 // Public members implement framework contracts

/// <summary>
/// Executes a cron job by triggering an internal gateway session.
/// </summary>
public sealed class AgentPromptAction : ICronAction
{
    public string ActionType => "agent-prompt";

    public async Task ExecuteAsync(CronExecutionContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var agentId = context.Job.AgentId
            ?? throw new InvalidOperationException("Cron job must define an agent id for agent-prompt actions.");

        var message = context.Job.Message;
        if (!string.IsNullOrWhiteSpace(context.Job.TemplateName))
        {
            var resolver = context.Services.GetService<IPromptTemplateResolver>()
                ?? throw new InvalidOperationException("Prompt template resolver is not registered.");

            if (!resolver.TryRender(agentId, context.Job.TemplateName, context.Job.TemplateParameters, out var renderedPrompt, out var error))
                throw new InvalidOperationException(error ?? $"Unable to render prompt template '{context.Job.TemplateName}'.");

            message = renderedPrompt;
        }

        if (string.IsNullOrWhiteSpace(message))
            throw new InvalidOperationException("Cron job must define either a message or a templateName for agent-prompt actions.");

        // #2373: classify an unresolvable model override before dispatch so the run records the
        // real reason instead of an opaque provider error raised deep inside the agent turn.
        CronModelPreflight.EnsureResolvable(
            context.Services.GetService<BotNexus.Agent.Providers.Core.Registry.ModelRegistry>(),
            context.Job.Model);

        // #3210: classify an unresolvable agent before dispatch. A null descriptor (agent deleted,
        // renamed, or never registered) used to be indistinguishable from a live agent with soul
        // disabled - both fell through to TriggerType.Cron and dispatched anyway, producing a
        // recurring opaque failure once per fire. The registry being absent from DI is a distinct,
        // deliberately non-rejecting condition: "cannot know", not "agent missing".
        var registry = context.Services.GetService<IAgentRegistry>();
        var descriptor = CronAgentPreflight.EnsureResolvable(registry, agentId);

        var preferredTriggerType = descriptor?.Soul?.Enabled == true
            ? TriggerType.Soul
            : TriggerType.Cron;

        var trigger = context.Services.GetServices<IInternalTrigger>()
            .FirstOrDefault(candidate => candidate.Type.Equals(preferredTriggerType))
            ?? throw new InvalidOperationException(
                preferredTriggerType.Equals(TriggerType.Soul)
                    ? "Soul internal trigger is not registered."
                    : "Cron internal trigger is not registered.");

        var triggerRequest = new InternalTriggerRequest
        {
            CronJobId = context.Job.Id,
            JobName = ExternalText.Sanitize(context.Job.Name, ExternalText.DefaultDisplayLength),
            ModelOverride = context.Job.Model,
            ConversationId = context.Job.ConversationId,
            CreatedBy = context.Job.CreatedBy
        };
        SessionId sessionId;
        try
        {
            sessionId = await trigger
                .CreateSessionAsync(
                    agentId,
                    message,
                    cancellationToken,
                    triggerRequest)
                .ConfigureAwait(false);
        }
        finally
        {
            // #2641 AC1: cost is recorded in a FINALLY, so a run that timed out or was cancelled
            // mid-turn still records the work it did before failing. The trigger stamps the request
            // before re-surfacing the cancellation; without this finally that measurement would
            // unwind with the exception and precisely the most expensive runs on the platform -
            // the ones that ran long enough to hit their timeout - would be the only ones recorded
            // as costing nothing.
            RecordCost(context, triggerRequest);
        }

        context.RecordSessionId(sessionId);

        // Surface the resolved conversation ID back to the execution context so the
        // scheduler can persist it to the job record, eliminating the lookup on future runs.
        if (triggerRequest.ResolvedConversationId is { } resolvedConversationId)
            context.RecordConversationId(resolvedConversationId);

        // #3161: forward a primary-delivery failure the trigger observed (e.g. the job's pinned
        // destination conversation no longer resolves) so the scheduler records a non-success
        // terminal status. Before #3161 there was no channel at all for this: the trigger silently
        // re-routed the output and the run recorded 'ok', so a job whose destination was deleted
        // produced an unbroken streak of green runs indefinitely.
        if (triggerRequest.DeliveryError is { } deliveryError)
            context.RecordDeliveryFailure(deliveryError);
    }

    /// <summary>
    /// Copies the trigger's reported tool count (#2985) and cost measurements (#2641) onto the
    /// execution context. Extracted so the success and failure paths cannot drift: both run it,
    /// exactly once, from the same finally.
    /// </summary>
    private static void RecordCost(CronExecutionContext context, InternalTriggerRequest triggerRequest)
    {
        // #2985: forward the turn's tool-invocation count to the execution context so the
        // scheduler can apply the execution-class zero-tool rule at the existing run-outcome
        // seam. Only agent-prompt reports a count; command/webhook actions leave it null, which
        // the scheduler reads as "not applicable" rather than as zero.
        if (triggerRequest.ToolInvocationCount is { } toolInvocationCount)
            context.RecordToolInvocationCount(toolInvocationCount);

        // #2641: forward the run's cost measurements. Every field is passed through exactly as the
        // trigger reported it, including nulls - an unmeasured field must reach the store as NULL
        // ("not measured"), never as a coerced zero that would present the run as free. The tool
        // count is reused rather than re-derived so the cost row and the #2985 outcome decision can
        // never disagree about how many tools ran. Duration is left null here because the SCHEDULER
        // owns the clock that brackets the whole action invocation.
        context.RecordCost(new CronRunCost(
            TurnCount: triggerRequest.TurnCount,
            ToolCallCount: triggerRequest.ToolInvocationCount,
            DurationMs: null,
            PromptTokens: triggerRequest.PromptTokens,
            CompletionTokens: triggerRequest.CompletionTokens));
    }

    private static bool IsInQuietHours(QuietHoursConfig config, string timezoneId)
    {
        var tz = ResolveTimeZone(timezoneId);
        var localNow = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, tz);
        var currentTime = localNow.TimeOfDay;

        if (!TimeSpan.TryParse(config.Start, out var start) ||
            !TimeSpan.TryParse(config.End, out var end))
            return false;

        if (start <= end)
            return currentTime >= start && currentTime < end;

        return currentTime >= start || currentTime < end;
    }

    private static TimeZoneInfo ResolveTimeZone(string timezoneId)
        => CronTimeZoneResolver.Resolve(timezoneId);
}
