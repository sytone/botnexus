using Serilog.Core;
using Serilog.Events;
using Serilog.Parsing;

namespace BotNexus.Gateway.Api.Logging;

/// <summary>
/// Makes every string in a Serilog event safe for sinks that render events as plain text.
/// </summary>
/// <remarks>
/// This decorator is the assembly-boundary safety seam for externally derived structured
/// properties. It wraps the complete gateway sink fan-out, so the rolling file, console, and
/// recent-log buffer cannot diverge when a new caller-controlled property is introduced.
/// Property names and non-string values are preserved; string values retain ordinary text exactly
/// while C0, DEL, and C1 controls become printable escapes through <see cref="RequestLogText.Safe"/>.
/// </remarks>
public sealed class LogTextSanitizingSink(ILogEventSink inner) : ILogEventSink, IDisposable
{
    private readonly ILogEventSink _inner = inner ?? throw new ArgumentNullException(nameof(inner));

    /// <inheritdoc />
    public void Emit(LogEvent logEvent)
    {
        if (logEvent is null)
            return;

        _inner.Emit(Sanitize(logEvent));
    }

    /// <summary>Returns the original event when it contains no control-bearing strings.</summary>
    public static LogEvent Sanitize(LogEvent logEvent)
    {
        ArgumentNullException.ThrowIfNull(logEvent);

        var template = SanitizeTemplate(logEvent.MessageTemplate);
        List<LogEventProperty>? properties = null;

        foreach (var property in logEvent.Properties)
        {
            var sanitized = SanitizeValue(property.Value);
            if (sanitized is null)
            {
                properties?.Add(new LogEventProperty(property.Key, property.Value));
                continue;
            }

            properties ??= BuildPrefix(logEvent, property.Key);
            properties.Add(new LogEventProperty(property.Key, sanitized));
        }

        if (template is null && properties is null)
            return logEvent;

        return new LogEvent(
            logEvent.Timestamp,
            logEvent.Level,
            logEvent.Exception,
            template ?? logEvent.MessageTemplate,
            properties ?? logEvent.Properties.Select(pair => new LogEventProperty(pair.Key, pair.Value)));
    }

    private static MessageTemplate? SanitizeTemplate(MessageTemplate template)
    {
        List<MessageTemplateToken>? rewritten = null;
        List<MessageTemplateToken>? seen = null;

        foreach (var token in template.Tokens)
        {
            if (token is not TextToken text)
            {
                if (rewritten is not null)
                    rewritten.Add(token);
                else
                    (seen ??= []).Add(token);
                continue;
            }

            var sanitized = RequestLogText.Safe(text.Text);
            if (string.Equals(sanitized, text.Text, StringComparison.Ordinal))
            {
                if (rewritten is not null)
                    rewritten.Add(token);
                else
                    (seen ??= []).Add(token);
                continue;
            }

            rewritten ??= seen ?? [];
            rewritten.Add(new TextToken(sanitized));
        }

        return rewritten is null ? null : new MessageTemplate(rewritten);
    }

    private static List<LogEventProperty> BuildPrefix(LogEvent logEvent, string firstDirtyKey)
    {
        var prefix = new List<LogEventProperty>(logEvent.Properties.Count);
        foreach (var property in logEvent.Properties)
        {
            if (string.Equals(property.Key, firstDirtyKey, StringComparison.Ordinal))
                break;

            prefix.Add(new LogEventProperty(property.Key, property.Value));
        }

        return prefix;
    }

    private static LogEventPropertyValue? SanitizeValue(LogEventPropertyValue value)
    {
        switch (value)
        {
            case ScalarValue { Value: string text }:
            {
                var sanitized = RequestLogText.Safe(text);
                return string.Equals(sanitized, text, StringComparison.Ordinal)
                    ? null
                    : new ScalarValue(sanitized);
            }
            case SequenceValue sequence:
            {
                List<LogEventPropertyValue>? elements = null;
                for (var index = 0; index < sequence.Elements.Count; index++)
                {
                    var sanitized = SanitizeValue(sequence.Elements[index]);
                    if (sanitized is null)
                    {
                        elements?.Add(sequence.Elements[index]);
                        continue;
                    }

                    elements ??= [.. sequence.Elements.Take(index)];
                    elements.Add(sanitized);
                }

                return elements is null ? null : new SequenceValue(elements);
            }
            case StructureValue structure:
            {
                List<LogEventProperty>? properties = null;
                for (var index = 0; index < structure.Properties.Count; index++)
                {
                    var property = structure.Properties[index];
                    var sanitized = SanitizeValue(property.Value);
                    if (sanitized is null)
                    {
                        properties?.Add(property);
                        continue;
                    }

                    properties ??= [.. structure.Properties.Take(index)];
                    properties.Add(new LogEventProperty(property.Name, sanitized));
                }

                return properties is null ? null : new StructureValue(properties, structure.TypeTag);
            }
            case DictionaryValue dictionary:
            {
                List<KeyValuePair<ScalarValue, LogEventPropertyValue>>? entries = null;
                var index = 0;
                foreach (var entry in dictionary.Elements)
                {
                    var sanitizedKey = SanitizeValue(entry.Key) as ScalarValue;
                    var sanitizedValue = SanitizeValue(entry.Value);
                    if (sanitizedKey is null && sanitizedValue is null)
                    {
                        entries?.Add(entry);
                        index++;
                        continue;
                    }

                    entries ??= [.. dictionary.Elements.Take(index)];
                    entries.Add(new KeyValuePair<ScalarValue, LogEventPropertyValue>(
                        sanitizedKey ?? entry.Key,
                        sanitizedValue ?? entry.Value));
                    index++;
                }

                return entries is null ? null : new DictionaryValue(entries);
            }
            default:
                return null;
        }
    }

    /// <inheritdoc />
    public void Dispose() => (_inner as IDisposable)?.Dispose();
}
