using System.Net;
using System.Text;

namespace BotNexus.Extensions.WebTools.Tests.Helpers;

public sealed class MockHttpMessageHandler : HttpMessageHandler
{
    private readonly Queue<ResponsePlan> _plans = new();
    private readonly List<CapturedHttpRequest> _requests = [];
    private readonly object _gate = new();
    private Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? _responder;

    public IReadOnlyList<CapturedHttpRequest> Requests
    {
        get
        {
            lock (_gate)
            {
                return _requests.ToList();
            }
        }
    }

    public void SetResponder(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder)
    {
        _responder = responder;
    }

    public void EnqueueResponse(
        HttpStatusCode statusCode,
        string body = "",
        string mediaType = "application/json",
        TimeSpan? delay = null,
        IReadOnlyDictionary<string, string>? headers = null)
    {
        lock (_gate)
        {
            _plans.Enqueue(ResponsePlan.ForResponse(statusCode, body, mediaType, delay, headers));
        }
    }

    public void EnqueueException(Exception exception, TimeSpan? delay = null)
    {
        lock (_gate)
        {
            _plans.Enqueue(ResponsePlan.ForException(exception, delay));
        }
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var requestBody = request.Content is null
            ? null
            : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        lock (_gate)
        {
            _requests.Add(new CapturedHttpRequest(
                request.Method,
                request.RequestUri,
                request.Headers.ToDictionary(h => h.Key, h => h.Value.ToArray(), StringComparer.OrdinalIgnoreCase),
                requestBody));
        }

        if (_responder is not null)
            return await _responder(request, cancellationToken).ConfigureAwait(false);

        ResponsePlan plan;
        lock (_gate)
        {
            if (_plans.Count == 0)
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound)
                {
                    Content = new StringContent("No mocked response configured.", Encoding.UTF8, "text/plain")
                };
            }

            plan = _plans.Dequeue();
        }

        if (plan.Delay is { } delay && delay > TimeSpan.Zero)
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);

        if (plan.Exception is not null)
            throw plan.Exception;

        var response = new HttpResponseMessage(plan.StatusCode)
        {
            Content = new StringContent(plan.Body ?? string.Empty, Encoding.UTF8, plan.MediaType ?? "application/json")
        };

        if (plan.Headers is not null)
        {
            foreach (var (key, value) in plan.Headers)
            {
                response.Headers.TryAddWithoutValidation(key, value);
            }
        }

        return response;
    }

    private sealed record ResponsePlan(
        HttpStatusCode StatusCode,
        string? Body,
        string? MediaType,
        Exception? Exception,
        TimeSpan? Delay,
        IReadOnlyDictionary<string, string>? Headers)
    {
        public static ResponsePlan ForResponse(
            HttpStatusCode statusCode,
            string body,
            string mediaType,
            TimeSpan? delay,
            IReadOnlyDictionary<string, string>? headers) =>
            new(statusCode, body, mediaType, null, delay, headers);

        public static ResponsePlan ForException(Exception exception, TimeSpan? delay) =>
            new(HttpStatusCode.InternalServerError, null, null, exception, delay, null);
    }
}

public sealed record CapturedHttpRequest(
    HttpMethod Method,
    Uri? RequestUri,
    IReadOnlyDictionary<string, string[]> Headers,
    string? Body);
