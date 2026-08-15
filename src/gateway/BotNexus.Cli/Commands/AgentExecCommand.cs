using System.CommandLine;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using BotNexus.Cli.Services;

namespace BotNexus.Cli.Commands;

/// <summary>
/// Exit codes for <c>botnexus agent exec</c> (#2396). A headless runner is only useful in a shell
/// pipeline if failure is machine-distinguishable, so the distinct failure modes named in the issue
/// each get their own code rather than collapsing into a generic <c>1</c>.
/// </summary>
internal static class AgentExecExitCode
{
    /// <summary>The run completed and no tool reported an error.</summary>
    public const int Success = 0;

    /// <summary>Usage error, unreachable gateway, or any failure not classified below.</summary>
    public const int Failure = 1;

    /// <summary>The named agent is not registered (gateway answered 404).</summary>
    public const int UnknownAgent = 2;

    /// <summary>The run exceeded <c>--timeout</c> and was abandoned client-side.</summary>
    public const int Timeout = 3;

    /// <summary>The run completed, but at least one tool call reported an error.</summary>
    public const int ToolFailure = 4;
}

/// <summary>
/// <c>botnexus agent exec &lt;agentId&gt; &lt;prompt&gt;</c> - a headless, one-shot agent run (#2396).
///
/// <para>WHY THIS DELEGATES RATHER THAN EXECUTES: the command is a new command <em>surface</em>, not a
/// second execution path. It POSTs to the gateway's <c>/api/chat</c> endpoint - the same endpoint
/// <c>prompt run</c> uses - so the run is subject to the identical supervisor, tool-policy and
/// approval posture as any other turn. Running the agent in-process from the CLI would create a
/// second execution path that necessarily re-derives (and therefore can silently omit) the
/// tool-policy and approval hooks the gateway installs. That would make this command an approval
/// bypass, which the issue names as a load-bearing constraint. There is deliberately no
/// <c>--yes</c>, <c>--auto-approve</c>, or <c>--no-approval</c> option: the CLI has no authority to
/// waive a policy decision that is made inside the gateway.</para>
///
/// <para>STDOUT/STDERR SEPARATION: the agent's answer (or the <c>--json</c> document) is the only
/// thing written to stdout, so <c>botnexus agent exec ... | jq</c> works. Diagnostics, warnings and
/// error text go to stderr. The output is written with <see cref="Console.Out"/> rather than Spectre
/// markup because the payload is agent-authored text that must not be re-interpreted as markup.</para>
/// </summary>
internal static class AgentExecCommand
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly JsonSerializerOptions ReadOptions = new(JsonSerializerDefaults.Web);

    /// <summary>Default wall-clock budget for a single headless run.</summary>
    public const int DefaultTimeoutSeconds = 300;

    /// <summary>
    /// Builds the <c>exec</c> subcommand. Kept separate from the configuration-only subcommands in
    /// <see cref="AgentCommands"/> because this is the one member of the group that causes an agent
    /// to actually run.
    /// </summary>
    public static Command Build(Option<bool> verboseOption)
    {
        var agentArgument = new Argument<string>("agentId", "The registered agent to run.");
        var promptArgument = new Argument<string>("prompt", "The task to give the agent.");

        var jsonOption = new Option<bool>("--json", () => false, "Emit a structured JSON result (text, tool calls, token usage, session id) on stdout.");
        var timeoutOption = new Option<int>("--timeout", () => DefaultTimeoutSeconds, "Wall-clock budget in seconds before the run is abandoned.");
        var modelOption = new Option<string?>("--model", () => null, "Per-run model override (model-id or provider/model-id).");
        var thinkingOption = new Option<string?>("--thinking", () => null, "Per-run thinking level: minimal, low, medium, high, xhigh, or max.");
        var conversationOption = new Option<string?>("--conversation", () => null, "Run inside an existing session/conversation instead of a fresh one.");
        var urlOption = new Option<string>("--url", () => GatewayClientFactory.DefaultUrl, "Gateway base URL.");
        var tokenOption = new Option<string?>("--token", () => null, "Gateway API credential. Required when --url is not the local gateway.");

        var command = new Command("exec", "Run an agent once, headlessly, and print its answer.")
        {
            agentArgument,
            promptArgument,
            jsonOption,
            timeoutOption,
            modelOption,
            thinkingOption,
            conversationOption,
            urlOption,
            tokenOption
        };

        command.SetHandler(async context =>
        {
            context.ExitCode = await ExecuteAsync(
                new AgentExecRequest(
                    AgentId: context.ParseResult.GetValueForArgument(agentArgument),
                    Prompt: context.ParseResult.GetValueForArgument(promptArgument),
                    AsJson: context.ParseResult.GetValueForOption(jsonOption),
                    TimeoutSeconds: context.ParseResult.GetValueForOption(timeoutOption),
                    Model: context.ParseResult.GetValueForOption(modelOption),
                    Thinking: context.ParseResult.GetValueForOption(thinkingOption),
                    ConversationId: context.ParseResult.GetValueForOption(conversationOption),
                    BaseUrl: context.ParseResult.GetValueForOption(urlOption) ?? GatewayClientFactory.DefaultUrl,
                    Token: context.ParseResult.GetValueForOption(tokenOption),
                    Verbose: context.ParseResult.GetValueForOption(verboseOption)),
                context.GetCancellationToken());
        });

        return command;
    }

    /// <summary>
    /// Runs one headless turn and returns the process exit code.
    /// </summary>
    /// <param name="request">The parsed invocation.</param>
    /// <param name="cancellationToken">Cancellation from the host (Ctrl-C).</param>
    /// <param name="stdout">Output sink; defaults to <see cref="Console.Out"/>. Injected by tests.</param>
    /// <param name="stderr">Diagnostic sink; defaults to <see cref="Console.Error"/>. Injected by tests.</param>
    /// <param name="handler">Optional transport for tests. Owned by the resolved client.</param>
    public static async Task<int> ExecuteAsync(
        AgentExecRequest request,
        CancellationToken cancellationToken,
        TextWriter? stdout = null,
        TextWriter? stderr = null,
        HttpMessageHandler? handler = null)
    {
        var output = stdout ?? Console.Out;
        var diagnostics = stderr ?? Console.Error;

        if (string.IsNullOrWhiteSpace(request.AgentId))
        {
            await diagnostics.WriteLineAsync("Error: agentId is required.");
            return AgentExecExitCode.Failure;
        }

        if (string.IsNullOrWhiteSpace(request.Prompt))
        {
            await diagnostics.WriteLineAsync("Error: prompt is required.");
            return AgentExecExitCode.Failure;
        }

        if (request.TimeoutSeconds <= 0)
        {
            await diagnostics.WriteLineAsync("Error: --timeout must be a positive number of seconds.");
            return AgentExecExitCode.Failure;
        }

        var timeout = TimeSpan.FromSeconds(request.TimeoutSeconds);

        // The credential policy lives in exactly one place (#2747). A remote --url without an
        // explicit --token is refused here rather than sent unauthenticated.
        var resolution = GatewayClientFactory.Resolve(
            request.BaseUrl,
            timeout,
            request.Token,
            GatewayClientFactory.DefaultCredentialSource(),
            handler);

        if (resolution.IsRefused)
        {
            await diagnostics.WriteLineAsync($"Error: {resolution.RefusalMessage}");
            return AgentExecExitCode.Failure;
        }

        using var client = resolution.Client!;

        if (request.Verbose)
            await diagnostics.WriteLineAsync($"Running agent '{request.AgentId}' against {request.BaseUrl} (timeout {request.TimeoutSeconds}s).");

        // A dedicated linked source so a timeout is distinguishable from a Ctrl-C: HttpClient's own
        // timeout surfaces as a bare TaskCanceledException that cannot be told apart from user
        // cancellation, which is precisely the ambiguity that would make the timeout exit code lie.
        using var timeoutSource = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);

        HttpResponseMessage response;
        try
        {
            response = await client.PostAsJsonAsync(
                "/api/chat",
                new AgentExecChatRequest(
                    request.AgentId,
                    request.Prompt,
                    Normalise(request.ConversationId),
                    Normalise(request.Model),
                    Normalise(request.Thinking)),
                linked.Token);
        }
        catch (OperationCanceledException) when (timeoutSource.IsCancellationRequested)
        {
            await diagnostics.WriteLineAsync($"Error: the run exceeded the {request.TimeoutSeconds}s timeout and was abandoned.");
            return AgentExecExitCode.Timeout;
        }
        catch (OperationCanceledException)
        {
            await diagnostics.WriteLineAsync("Error: the run was cancelled.");
            return AgentExecExitCode.Failure;
        }
        catch (HttpRequestException ex)
        {
            await diagnostics.WriteLineAsync($"Error: unable to reach the gateway at {request.BaseUrl}: {ex.Message}");
            return AgentExecExitCode.Failure;
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(CancellationToken.None);

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                await diagnostics.WriteLineAsync($"Error: agent '{request.AgentId}' is not registered on the gateway.");
                return AgentExecExitCode.UnknownAgent;
            }

            if (!response.IsSuccessStatusCode)
            {
                await diagnostics.WriteLineAsync($"Error: gateway returned {(int)response.StatusCode}.");
                if (!string.IsNullOrWhiteSpace(body))
                    await diagnostics.WriteLineAsync(body);
                return AgentExecExitCode.Failure;
            }

            AgentExecChatResponse? chat;
            try
            {
                chat = JsonSerializer.Deserialize<AgentExecChatResponse>(body, ReadOptions);
            }
            catch (JsonException ex)
            {
                await diagnostics.WriteLineAsync($"Error: the gateway returned a response that could not be parsed: {ex.Message}");
                return AgentExecExitCode.Failure;
            }

            if (chat is null)
            {
                await diagnostics.WriteLineAsync("Error: the gateway returned an empty response.");
                return AgentExecExitCode.Failure;
            }

            var toolCalls = chat.ToolCalls ?? [];
            var failedTools = toolCalls.Where(c => c.IsError).ToArray();

            if (request.AsJson)
            {
                var result = new AgentExecResult(
                    chat.SessionId,
                    request.AgentId,
                    chat.Content ?? string.Empty,
                    [.. toolCalls.Select(c => new AgentExecToolCall(c.ToolCallId, c.ToolName, c.IsError))],
                    chat.Usage,
                    failedTools.Length == 0 ? AgentExecExitCode.Success : AgentExecExitCode.ToolFailure);
                await output.WriteLineAsync(JsonSerializer.Serialize(result, JsonOptions));
            }
            else
            {
                await output.WriteLineAsync(chat.Content ?? string.Empty);
            }

            if (failedTools.Length > 0)
            {
                // Reported on stderr even in --json mode: stdout must stay a single parseable
                // document, and a non-zero exit with no explanation is a support ticket.
                await diagnostics.WriteLineAsync(
                    $"Error: {failedTools.Length} tool call(s) failed: {string.Join(", ", failedTools.Select(c => c.ToolName))}.");
                return AgentExecExitCode.ToolFailure;
            }

            return AgentExecExitCode.Success;
        }
    }

    private static string? Normalise(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private sealed record AgentExecChatRequest(
        string AgentId,
        string Message,
        string? SessionId,
        string? Model,
        string? Thinking);

    private sealed record AgentExecChatResponse(
        string SessionId,
        string? Content,
        AgentExecUsage? Usage,
        IReadOnlyList<AgentExecToolCall>? ToolCalls);
}

/// <summary>A parsed <c>agent exec</c> invocation. A record so tests can construct one directly.</summary>
/// <param name="AgentId">The agent to run.</param>
/// <param name="Prompt">The task text.</param>
/// <param name="AsJson">Whether to emit the structured document instead of plain text.</param>
/// <param name="TimeoutSeconds">Wall-clock budget in seconds.</param>
/// <param name="Model">Optional per-run model override.</param>
/// <param name="Thinking">Optional per-run thinking level.</param>
/// <param name="ConversationId">Optional existing session/conversation to run inside.</param>
/// <param name="BaseUrl">Gateway base URL.</param>
/// <param name="Token">Explicit gateway credential, required for a non-loopback target.</param>
/// <param name="Verbose">Whether to emit progress diagnostics on stderr.</param>
internal sealed record AgentExecRequest(
    string AgentId,
    string Prompt,
    bool AsJson = false,
    int TimeoutSeconds = AgentExecCommand.DefaultTimeoutSeconds,
    string? Model = null,
    string? Thinking = null,
    string? ConversationId = null,
    string BaseUrl = GatewayClientFactory.DefaultUrl,
    string? Token = null,
    bool Verbose = false);

/// <summary>Token usage as reported by the gateway, echoed verbatim into the <c>--json</c> document.</summary>
internal sealed record AgentExecUsage(int? InputTokens, int? OutputTokens, int? CacheRead, int? CacheWrite);

/// <summary>One tool invocation in the <c>--json</c> document.</summary>
internal sealed record AgentExecToolCall(string ToolCallId, string ToolName, bool IsError);

/// <summary>
/// The <c>--json</c> stdout document. <c>exitCode</c> is included so a caller that has already
/// captured stdout does not have to correlate it with <c>$LASTEXITCODE</c> to learn the outcome.
/// </summary>
internal sealed record AgentExecResult(
    string SessionId,
    string AgentId,
    string Text,
    IReadOnlyList<AgentExecToolCall> ToolCalls,
    AgentExecUsage? Usage,
    int ExitCode);
