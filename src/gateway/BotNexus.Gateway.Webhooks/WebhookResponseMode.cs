namespace BotNexus.Gateway.Webhooks;

/// <summary>
/// How the gateway responds to an inbound webhook POST.
/// </summary>
public enum WebhookResponseMode
{
    /// <summary>
    /// Return <c>202 Accepted</c> immediately with a <c>Location</c> poll URL.
    /// The agent run continues in the background. Callers poll
    /// <c>GET /api/webhooks/runs/{runId}</c> for completion.
    /// This is the default — LLM runs can take 30–120 seconds, which exceeds
    /// the 3–10 second timeout most external systems enforce on outbound webhooks.
    /// </summary>
    Async,

    /// <summary>
    /// Hold the HTTP connection open until the agent completes, then return
    /// <c>200 OK</c> with the full agent response inline. Opt-in only — will
    /// break external systems that enforce short timeouts. Safe for internal
    /// tooling and low-latency integrations.
    /// </summary>
    Sync,

    /// <summary>
    /// Return <c>202 Accepted</c> immediately. When the agent completes, POST
    /// the result to the <c>callbackUrl</c> supplied in the request body.
    /// </summary>
    Callback
}
