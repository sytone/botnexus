using System.Globalization;
using System.Text.Json;
using BotNexus.Agent.Core.Tools;
using BotNexus.Agent.Core.Types;
using BotNexus.Agent.Providers.Core.Models;

namespace BotNexus.Gateway.Tools;

/// <summary>
/// Provides agents with current UTC and timezone-aware local date/time details.
/// </summary>
public sealed class DateTimeTool : IAgentTool
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string? _defaultTimezone;
    private readonly Func<DateTimeOffset> _utcNow;

    /// <summary>
    /// Creates a datetime tool using the agent's configured timezone as the default.
    /// </summary>
    /// <param name="defaultTimezone">Agent-configured timezone used when the call omits a timezone.</param>
    public DateTimeTool(string? defaultTimezone = null)
        : this(defaultTimezone, () => DateTimeOffset.UtcNow)
    {
    }

    internal DateTimeTool(string? defaultTimezone, Func<DateTimeOffset> utcNow)
    {
        _defaultTimezone = defaultTimezone;
        _utcNow = utcNow;
    }

    /// <inheritdoc />
    public string Name => "get_datetime";

    /// <inheritdoc />
    public string Label => "Get Date/Time";

    /// <inheritdoc />
    public Tool Definition => new(
        Name,
        "Get the current UTC datetime and timezone-aware local date/time details.",
        JsonDocument.Parse("""
            {
              "type": "object",
              "properties": {
                "timezone": {
                  "type": "string",
                  "description": "Optional IANA timezone ID (for example, 'America/Los_Angeles'). Defaults to the agent timezone, or UTC when no agent timezone is configured."
                }
              }
            }
            """).RootElement.Clone());

    /// <inheritdoc />
    public Task<IReadOnlyDictionary<string, object?>> PrepareArgumentsAsync(
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var prepared = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        var timezone = ReadString(arguments, "timezone");
        if (!string.IsNullOrWhiteSpace(timezone))
            prepared["timezone"] = timezone;

        return Task.FromResult<IReadOnlyDictionary<string, object?>>(prepared);
    }

    /// <inheritdoc />
    public Task<AgentToolResult> ExecuteAsync(
        string toolCallId,
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken = default,
        AgentToolUpdateCallback? onUpdate = null)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var requestedTimezone = ReadString(arguments, "timezone");
        var (timeZone, displayTimeZone) = ResolveTimeZone(
            string.IsNullOrWhiteSpace(requestedTimezone) ? _defaultTimezone : requestedTimezone);

        var utcNow = _utcNow().ToUniversalTime();
        var localNow = TimeZoneInfo.ConvertTime(utcNow, timeZone);
        var localDate = DateOnly.FromDateTime(localNow.DateTime);
        var thisMonday = localDate.AddDays(-DaysSinceMonday(localNow.DayOfWeek));
        var nextMonday = localDate.AddDays(DaysUntilNextMonday(localNow.DayOfWeek));

        var response = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["utc"] = utcNow.ToString("O", CultureInfo.InvariantCulture),
            ["local_date"] = localDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["local_time"] = localNow.ToString("HH:mm:ss", CultureInfo.InvariantCulture),
            ["timezone"] = displayTimeZone,
            ["day_of_week"] = localNow.DayOfWeek.ToString(),
            ["week_number"] = ISOWeek.GetWeekOfYear(localNow.DateTime),
            ["unix_timestamp"] = utcNow.ToUnixTimeSeconds(),
            ["offset"] = FormatOffset(localNow.Offset),
            ["next_monday"] = nextMonday.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["this_monday"] = thisMonday.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
        };

        return Task.FromResult(TextResult(JsonSerializer.Serialize(response, JsonOptions)));
    }

    private static (TimeZoneInfo TimeZone, string DisplayId) ResolveTimeZone(string? timezoneId)
    {
        if (string.IsNullOrWhiteSpace(timezoneId) ||
            timezoneId.Equals("UTC", StringComparison.OrdinalIgnoreCase))
            return (TimeZoneInfo.Utc, "UTC");

        var trimmed = timezoneId.Trim();
        if (TryFindTimeZone(trimmed, out var direct))
            return (direct, trimmed);

        if (TimeZoneInfo.TryConvertIanaIdToWindowsId(trimmed, out var windowsTimeZone) &&
            TryFindTimeZone(windowsTimeZone, out var windowsResolved))
        {
            return (windowsResolved, trimmed);
        }

        if (TimeZoneInfo.TryConvertWindowsIdToIanaId(trimmed, out var ianaTimeZone) &&
            TryFindTimeZone(ianaTimeZone, out var ianaResolved))
        {
            return (ianaResolved, ianaTimeZone);
        }

        return (TimeZoneInfo.Utc, "UTC");
    }

    private static bool TryFindTimeZone(string timezoneId, out TimeZoneInfo timeZone)
    {
        try
        {
            timeZone = TimeZoneInfo.FindSystemTimeZoneById(timezoneId);
            return true;
        }
        catch (TimeZoneNotFoundException)
        {
        }
        catch (InvalidTimeZoneException)
        {
        }

        timeZone = TimeZoneInfo.Utc;
        return false;
    }

    private static int DaysSinceMonday(DayOfWeek dayOfWeek)
        => ((int)dayOfWeek - (int)DayOfWeek.Monday + 7) % 7;

    private static int DaysUntilNextMonday(DayOfWeek dayOfWeek)
    {
        var days = ((int)DayOfWeek.Monday - (int)dayOfWeek + 7) % 7;
        return days == 0 ? 7 : days;
    }

    private static string FormatOffset(TimeSpan offset)
    {
        var sign = offset < TimeSpan.Zero ? "-" : "";
        var absolute = offset.Duration();
        return FormattableString.Invariant($"{sign}{absolute:hh\\:mm\\:ss}");
    }

    private static string? ReadString(IReadOnlyDictionary<string, object?> args, string key)
    {
        if (!args.TryGetValue(key, out var value) || value is null)
            return null;

        return value switch
        {
            JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
            JsonElement element => element.ToString(),
            _ => value.ToString()
        };
    }

    private static AgentToolResult TextResult(string text)
        => new([new AgentToolContent(AgentToolContentType.Text, text)]);
}
