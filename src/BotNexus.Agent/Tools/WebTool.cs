using BotNexus.Core.Models;
using Microsoft.Extensions.Logging;

namespace BotNexus.Agent.Tools;

/// <summary>Tool for basic HTTP web requests.</summary>
public sealed class WebTool : ToolBase
{
    private readonly HttpClient _httpClient;

    public WebTool(HttpClient? httpClient = null, ILogger? logger = null)
        : base(logger)
    {
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        _httpClient.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "BotNexus/1.0");
    }

    /// <inheritdoc/>
    public override ToolDefinition Definition => new(
        "web_fetch",
        "Fetch the content of a URL via HTTP GET.",
        new Dictionary<string, ToolParameterSchema>
        {
            ["url"] = new("string", "The URL to fetch", Required: true)
        });

    /// <inheritdoc/>
    protected override async Task<string> ExecuteCoreAsync(IReadOnlyDictionary<string, object?> arguments, CancellationToken cancellationToken)
    {
        var url = GetRequiredString(arguments, "url");
        var response = await _httpClient.GetStringAsync(url, cancellationToken).ConfigureAwait(false);
        return response.Length > 10000 ? response[..10000] + "\n... (truncated)" : response;
    }
}
