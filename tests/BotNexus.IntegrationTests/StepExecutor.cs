using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;

namespace BotNexus.IntegrationTests;

public class StepExecutor
{
    private readonly TestSignalRClient _client;
    private readonly HttpClient _httpClient;
    private readonly TestLogger _log;
    private readonly ConcurrentDictionary<string, string> _stepSessions;
    private readonly ConcurrentDictionary<string, Stopwatch> _stepTimers;
    private string? _lastApiResponse;
    
    public StepExecutor(TestSignalRClient client, HttpClient httpClient, TestLogger log,
        StepExecutor? sharedState = null)
    {
        _client = client;
        _httpClient = httpClient;
        _log = log;
        // Track executors share session/timer state with the parent so
        // post-track assertions can reference labels from any track.
        _stepSessions = sharedState?._stepSessions ?? new();
        _stepTimers = sharedState?._stepTimers ?? new();
    }
    
    public async Task ExecuteStepsAsync(List<ScenarioStep> steps, CancellationToken ct)
    {
        foreach (var step in steps)
        {
            await ExecuteSingleStepAsync(step, ct);
        }
    }
    
    private async Task ExecuteSendAsync(ScenarioStep step, CancellationToken ct)
    {
        var timer = Stopwatch.StartNew();
        var result = await _client.SendMessageAsync(
            step.Agent ?? throw new InvalidOperationException("send_message requires agent"),
            step.Content ?? "hello",
            ct);
        
        if (step.Label is not null)
        {
            _stepSessions[step.Label] = result.SessionId;
            _stepTimers[step.Label] = timer;
        }
        
        _log.Write($"→ Sent to {step.Agent} (session: {result.SessionId[..8]}...)");
    }
    
    private async Task ExecuteWaitForEventAsync(ScenarioStep step, CancellationToken ct)
    {
        var sessionId = step.FromStep is not null ? _stepSessions[step.FromStep] : "";
        var timeout = TimeSpan.FromSeconds(step.TimeoutSeconds);
        var evt = await _client.WaitForEventAsync(sessionId, step.Type ?? "ContentDelta", timeout, ct);
        
        if (step.FromStep is not null && _stepTimers.TryGetValue(step.FromStep, out var timer))
        {
            timer.Stop();
            _log.Write($"← {step.Type} from {step.FromStep} ({timer.ElapsedMilliseconds}ms)");
        }
    }
    
    private async Task ExecuteWaitForEventsAsync(ScenarioStep step, CancellationToken ct)
    {
        var tasks = (step.Events ?? []).Select(async evt =>
        {
            var sessionId = evt.FromStep is not null ? _stepSessions[evt.FromStep] : "";
            var timeout = TimeSpan.FromSeconds(step.TimeoutSeconds);
            var received = await _client.WaitForEventAsync(sessionId, evt.Type, timeout, ct);
            
            if (evt.FromStep is not null && _stepTimers.TryGetValue(evt.FromStep, out var timer))
            {
                timer.Stop();
                _log.Write($"← {evt.Type} from {evt.FromStep} ({timer.ElapsedMilliseconds}ms)");
            }
        });
        
        await Task.WhenAll(tasks);
    }
    
    private async Task ExecuteResetAsync(ScenarioStep step, CancellationToken ct)
    {
        // Find the most recent session for this agent
        var agentId = step.Agent ?? throw new InvalidOperationException("reset_session requires agent");
        var sessionId = _stepSessions.Values.LastOrDefault() 
            ?? throw new InvalidOperationException("No session to reset");
        
        await _client.ResetSessionAsync(agentId, sessionId, ct);
        _log.Write($"↺ Reset session for {agentId}");
    }

    private string ResolvePath(string path)
    {
        foreach (var (label, sessionId) in _stepSessions)
            path = path.Replace($"{{{{session:{label}}}}}", sessionId);
        return path;
    }

    private async Task ExecuteApiGetAsync(ScenarioStep step, CancellationToken ct)
    {
        var path = ResolvePath(step.Path ?? throw new InvalidOperationException("api_get requires path"));
        var response = await _httpClient.GetAsync(path, ct);
        _lastApiResponse = await response.Content.ReadAsStringAsync(ct);
        _log.Write($"GET {path} → {(int)response.StatusCode}");

        ValidateStatus(step, response.StatusCode);
        ValidateContains(step, _lastApiResponse);
    }

    private async Task ExecuteApiPutAsync(ScenarioStep step, CancellationToken ct)
    {
        var path = ResolvePath(step.Path ?? throw new InvalidOperationException("api_put requires path"));
        var body = step.Body?.GetRawText() ?? "{}";
        var content = new StringContent(body, Encoding.UTF8, "application/json");
        var response = await _httpClient.PutAsync(path, content, ct);
        _lastApiResponse = await response.Content.ReadAsStringAsync(ct);
        _log.Write($"PUT {path} → {(int)response.StatusCode}");

        ValidateStatus(step, response.StatusCode);
        ValidateContains(step, _lastApiResponse);
    }

    private async Task ExecuteApiPostAsync(ScenarioStep step, CancellationToken ct)
    {
        var path = ResolvePath(step.Path ?? throw new InvalidOperationException("api_post requires path"));
        var body = step.Body?.GetRawText() ?? "{}";
        var content = new StringContent(body, Encoding.UTF8, "application/json");
        var response = await _httpClient.PostAsync(path, content, ct);
        _lastApiResponse = await response.Content.ReadAsStringAsync(ct);
        _log.Write($"POST {path} → {(int)response.StatusCode}");

        ValidateStatus(step, response.StatusCode);
        ValidateContains(step, _lastApiResponse);
    }

    private async Task ExecuteApiDeleteAsync(ScenarioStep step, CancellationToken ct)
    {
        var path = ResolvePath(step.Path ?? throw new InvalidOperationException("api_delete requires path"));
        var response = await _httpClient.DeleteAsync(path, ct);
        _lastApiResponse = await response.Content.ReadAsStringAsync(ct);
        _log.Write($"DELETE {path} → {(int)response.StatusCode}");

        ValidateStatus(step, response.StatusCode);
        ValidateContains(step, _lastApiResponse);
    }

    private void ExecuteAssertApiResponse(ScenarioStep step)
    {
        if (_lastApiResponse is null)
            throw new Exception("Assert failed: no prior API response to assert");
        ValidateContains(step, _lastApiResponse);
    }

    private static void ValidateStatus(ScenarioStep step, System.Net.HttpStatusCode actualStatus)
    {
        if (step.ExpectedStatus != 0 && (int)actualStatus != step.ExpectedStatus)
            throw new Exception($"Expected status {step.ExpectedStatus} but got {(int)actualStatus}");
    }

    private static void ValidateContains(ScenarioStep step, string responseBody)
    {
        if (step.ExpectedContains is not null &&
            !responseBody.Contains(step.ExpectedContains, StringComparison.Ordinal))
        {
            throw new Exception($"Response did not contain '{step.ExpectedContains}'");
        }
    }
    
    private void ExecuteAssert(ScenarioStep step)
    {
        switch (step.Condition)
        {
            case "responded":
                var sid = step.Step is not null ? _stepSessions[step.Step] : "";
                var events = _client.GetEvents(sid);
                if (!events.Any(e => e.Method is "ContentDelta" or "ToolStart" or "MessageStart"))
                    throw new Exception($"Assert failed: no activity for step '{step.Step}'");
                break;
                
            case "both_responded":
                foreach (var s in step.Steps ?? [])
                {
                    var sessionEvents = _client.GetEvents(_stepSessions[s]);
                    if (!sessionEvents.Any(e => e.Method is "ContentDelta" or "ToolStart" or "MessageStart"))
                        throw new Exception($"Assert failed: no activity for step '{s}'");
                }
                break;
                
            default:
                throw new NotSupportedException($"Unknown assert condition: {step.Condition}");
        }
    }

    private void ExecuteAssertTiming(ScenarioStep step)
    {
        var fastLabel = step.FastStep ?? throw new InvalidOperationException("assert_timing requires fast_step");
        var slowLabel = step.SlowStep ?? throw new InvalidOperationException("assert_timing requires slow_step");

        var fastSessionId = _stepSessions[fastLabel];
        var slowSessionId = _stepSessions[slowLabel];

        var fastEvents = _client.GetEvents(fastSessionId);
        var slowEvents = _client.GetEvents(slowSessionId);

        var fastFirstDelta = fastEvents.FirstOrDefault(e => e.Method == "ContentDelta");
        var slowLastTool = slowEvents.LastOrDefault(e => e.Method == "ToolEnd");
        var slowLastEvent = slowEvents.LastOrDefault(e => e.Method is "MessageEnd" or "ToolEnd");

        if (fastFirstDelta is null)
            throw new Exception($"assert_timing: fast agent '{fastLabel}' has no ContentDelta");

        _log.Write($"⏱️  Timing: fast '{fastLabel}' first ContentDelta at {fastFirstDelta.ReceivedAt:HH:mm:ss.fff}");

        if (slowLastTool is not null)
            _log.Write($"⏱️  Timing: slow '{slowLabel}' last ToolEnd at {slowLastTool.ReceivedAt:HH:mm:ss.fff}");
        if (slowLastEvent is not null)
            _log.Write($"⏱️  Timing: slow '{slowLabel}' final event at {slowLastEvent.ReceivedAt:HH:mm:ss.fff}");

        if (slowLastTool is not null && fastFirstDelta.ReceivedAt > slowLastTool.ReceivedAt)
        {
            var delay = fastFirstDelta.ReceivedAt - slowLastTool.ReceivedAt;
            throw new Exception(
                $"assert_timing FAILED: fast agent '{fastLabel}' first responded at {fastFirstDelta.ReceivedAt:HH:mm:ss.fff} " +
                $"which is {delay.TotalMilliseconds:F0}ms AFTER slow agent '{slowLabel}' ToolEnd at {slowLastTool.ReceivedAt:HH:mm:ss.fff}. " +
                $"Fast agent was blocked by slow agent's tool execution!");
        }

        _log.Write($"✅ assert_timing PASS: {step.Description ?? "fast responded during slow's tool execution"}");
    }

    /// <summary>
    /// Execute multiple steps concurrently — mirrors how a real user interacts with
    /// multiple agents simultaneously (switching tabs, sending messages in parallel).
    /// </summary>
    private async Task ExecuteParallelAsync(ScenarioStep step, CancellationToken ct)
    {
        var subSteps = step.ParallelSteps
            ?? throw new InvalidOperationException("parallel requires parallel_steps");

        _log.Write($"⚡ Parallel: launching {subSteps.Count} steps concurrently");

        var tasks = subSteps.Select(async subStep =>
        {
            try
            {
                await ExecuteSingleStepAsync(subStep, ct);
            }
            catch (Exception ex)
            {
                _log.Write($"❌ Parallel sub-step '{subStep.Action}' failed: {ex.Message}");
                throw;
            }
        });

        await Task.WhenAll(tasks);
        _log.Write($"⚡ Parallel: all {subSteps.Count} steps completed");
    }

    /// <summary>
    /// Execute a single step — extracted from the foreach loop so parallel can reuse it.
    /// </summary>
    private async Task ExecuteSingleStepAsync(ScenarioStep step, CancellationToken ct)
    {
        switch (step.Action)
        {
            case "send_message": await ExecuteSendAsync(step, ct); break;
            case "wait_for_event": await ExecuteWaitForEventAsync(step, ct); break;
            case "wait_for_events": await ExecuteWaitForEventsAsync(step, ct); break;
            case "reset_session": await ExecuteResetAsync(step, ct); break;
            case "assert": ExecuteAssert(step); break;
            case "delay": await Task.Delay(TimeSpan.FromSeconds(step.TimeoutSeconds), ct); break;
            case "api_get": await ExecuteApiGetAsync(step, ct); break;
            case "api_put": await ExecuteApiPutAsync(step, ct); break;
            case "api_post": await ExecuteApiPostAsync(step, ct); break;
            case "api_delete": await ExecuteApiDeleteAsync(step, ct); break;
            case "assert_api_response": ExecuteAssertApiResponse(step); break;
            case "assert_timing": ExecuteAssertTiming(step); break;
            case "parallel": await ExecuteParallelAsync(step, ct); break;
            default: throw new NotSupportedException($"Unknown action: {step.Action}");
        }
    }
}

