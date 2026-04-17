using System.Diagnostics;
using System.Text;

namespace BotNexus.IntegrationTests;

public class StepExecutor
{
    private readonly TestSignalRClient _client;
    private readonly HttpClient _httpClient;
    private readonly Dictionary<string, string> _stepSessions = new();
    private readonly Dictionary<string, Stopwatch> _stepTimers = new();
    private string? _lastApiResponse;
    
    public StepExecutor(TestSignalRClient client, HttpClient httpClient)
    {
        _client = client;
        _httpClient = httpClient;
    }
    
    public async Task ExecuteStepsAsync(List<ScenarioStep> steps, CancellationToken ct)
    {
        foreach (var step in steps)
        {
            switch (step.Action)
            {
                case "send_message":
                    await ExecuteSendAsync(step, ct);
                    break;
                case "wait_for_event":
                    await ExecuteWaitForEventAsync(step, ct);
                    break;
                case "wait_for_events":
                    await ExecuteWaitForEventsAsync(step, ct);
                    break;
                case "reset_session":
                    await ExecuteResetAsync(step, ct);
                    break;
                case "assert":
                    ExecuteAssert(step);
                    break;
                case "delay":
                    await Task.Delay(TimeSpan.FromSeconds(step.TimeoutSeconds), ct);
                    break;
                case "api_get":
                    await ExecuteApiGetAsync(step, ct);
                    break;
                case "api_put":
                    await ExecuteApiPutAsync(step, ct);
                    break;
                case "api_post":
                    await ExecuteApiPostAsync(step, ct);
                    break;
                case "api_delete":
                    await ExecuteApiDeleteAsync(step, ct);
                    break;
                case "assert_api_response":
                    ExecuteAssertApiResponse(step);
                    break;
                default:
                    throw new NotSupportedException($"Unknown action: {step.Action}");
            }
        }
    }
    
    private async Task ExecuteSendAsync(ScenarioStep step, CancellationToken ct)
    {
        var timer = Stopwatch.StartNew();
        var sessionId = await _client.SendMessageAsync(
            step.Agent ?? throw new InvalidOperationException("send_message requires agent"),
            step.Content ?? "hello",
            ct);
        
        if (step.Label is not null)
        {
            _stepSessions[step.Label] = sessionId;
            _stepTimers[step.Label] = timer;
        }
        
        Console.WriteLine($"    → Sent to {step.Agent} (session: {sessionId[..8]}...)");
    }
    
    private async Task ExecuteWaitForEventAsync(ScenarioStep step, CancellationToken ct)
    {
        var sessionId = step.FromStep is not null ? _stepSessions[step.FromStep] : "";
        var timeout = TimeSpan.FromSeconds(step.TimeoutSeconds);
        var evt = await _client.WaitForEventAsync(sessionId, step.Type ?? "ContentDelta", timeout, ct);
        
        if (step.FromStep is not null && _stepTimers.TryGetValue(step.FromStep, out var timer))
        {
            timer.Stop();
            Console.WriteLine($"    ← {step.Type} from {step.FromStep} ({timer.ElapsedMilliseconds}ms)");
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
                Console.WriteLine($"    ← {evt.Type} from {evt.FromStep} ({timer.ElapsedMilliseconds}ms)");
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
        Console.WriteLine($"    ↺ Reset session for {agentId}");
    }

    private async Task ExecuteApiGetAsync(ScenarioStep step, CancellationToken ct)
    {
        var path = step.Path ?? throw new InvalidOperationException("api_get requires path");
        var response = await _httpClient.GetAsync(path, ct);
        _lastApiResponse = await response.Content.ReadAsStringAsync(ct);
        Console.WriteLine($"    GET {path} → {(int)response.StatusCode}");

        ValidateStatus(step, response.StatusCode);
        ValidateContains(step, _lastApiResponse);
    }

    private async Task ExecuteApiPutAsync(ScenarioStep step, CancellationToken ct)
    {
        var path = step.Path ?? throw new InvalidOperationException("api_put requires path");
        var body = step.Body?.GetRawText() ?? "{}";
        var content = new StringContent(body, Encoding.UTF8, "application/json");
        var response = await _httpClient.PutAsync(path, content, ct);
        _lastApiResponse = await response.Content.ReadAsStringAsync(ct);
        Console.WriteLine($"    PUT {path} → {(int)response.StatusCode}");

        ValidateStatus(step, response.StatusCode);
        ValidateContains(step, _lastApiResponse);
    }

    private async Task ExecuteApiPostAsync(ScenarioStep step, CancellationToken ct)
    {
        var path = step.Path ?? throw new InvalidOperationException("api_post requires path");
        var body = step.Body?.GetRawText() ?? "{}";
        var content = new StringContent(body, Encoding.UTF8, "application/json");
        var response = await _httpClient.PostAsync(path, content, ct);
        _lastApiResponse = await response.Content.ReadAsStringAsync(ct);
        Console.WriteLine($"    POST {path} → {(int)response.StatusCode}");

        ValidateStatus(step, response.StatusCode);
        ValidateContains(step, _lastApiResponse);
    }

    private async Task ExecuteApiDeleteAsync(ScenarioStep step, CancellationToken ct)
    {
        var path = step.Path ?? throw new InvalidOperationException("api_delete requires path");
        var response = await _httpClient.DeleteAsync(path, ct);
        _lastApiResponse = await response.Content.ReadAsStringAsync(ct);
        Console.WriteLine($"    DELETE {path} → {(int)response.StatusCode}");

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
                if (!events.Any(e => e.Method == "ContentDelta"))
                    throw new Exception($"Assert failed: no ContentDelta for step '{step.Step}'");
                break;
                
            case "both_responded":
                foreach (var s in step.Steps ?? [])
                {
                    var sessionEvents = _client.GetEvents(_stepSessions[s]);
                    if (!sessionEvents.Any(e => e.Method == "ContentDelta"))
                        throw new Exception($"Assert failed: no ContentDelta for step '{s}'");
                }
                break;
                
            default:
                throw new NotSupportedException($"Unknown assert condition: {step.Condition}");
        }
    }
}
