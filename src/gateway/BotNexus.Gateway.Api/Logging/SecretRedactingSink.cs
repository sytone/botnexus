using BotNexus.Gateway.Abstractions.Security;
using Serilog.Core;
using Serilog.Events;
using Serilog.Parsing;

namespace BotNexus.Gateway.Api.Logging;

/// <summary>
/// Serilog sink decorator that routes every event through <see cref="ISecretRedactor"/> before it
/// reaches the wrapped sink (issue #3276).
/// <para>
/// <b>Why a sink decorator rather than an enricher.</b> An <see cref="ILogEventEnricher"/> can only
/// add properties; it cannot rewrite the message template or replace an existing property value, so
/// it cannot remove a secret that is already in the event. Redaction therefore has to sit on the
/// write path, wrapping the sinks that serialize the event.
/// </para>
/// <para>
/// <b>Why both the template and the properties.</b> The motivating leak -
/// <c>Start processing HTTP request POST https://api.telegram.org/bot&lt;token&gt;/getUpdates</c> -
/// carries the credential in the <c>Uri</c> <i>property</i>, not in literal template text. Redacting
/// only the rendered message would still hand the full token to any JSON formatter, so scalar
/// strings are redacted recursively through sequences, structures and dictionaries as well.
/// </para>
/// <para>
/// <b>Cost on the common case.</b> <see cref="ISecretRedactor.Redact"/> returns the input string
/// unchanged when no pattern matches, so a secret-free event produces no rewritten tokens, no
/// rewritten properties and no new <see cref="LogEvent"/> - the original instance is forwarded
/// as-is. The rewrite is paid for only by events that actually contain a secret.
/// </para>
/// <para>
/// <b>Known limit.</b> <see cref="LogEvent.Exception"/> is forwarded untouched: an exception's
/// message and stack are not reconstructable without changing its type, so a credential
/// interpolated into an exception message is out of scope here and stays the responsibility of the
/// code that builds the exception (see #2881).
/// </para>
/// </summary>
public sealed class SecretRedactingSink : ILogEventSink, IDisposable
{
    private readonly ILogEventSink _inner;
    private readonly ISecretRedactor _redactor;

    /// <summary>
    /// Wraps <paramref name="inner"/> so every event it receives has already been redacted.
    /// </summary>
    /// <param name="inner">The sink that ultimately serializes the event.</param>
    /// <param name="redactor">The redactor applied to template text and property values.</param>
    public SecretRedactingSink(ILogEventSink inner, ISecretRedactor redactor)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(redactor);
        _inner = inner;
        _redactor = redactor;
    }

    /// <inheritdoc />
    public void Emit(LogEvent logEvent)
    {
        if (logEvent is null)
            return;

        _inner.Emit(Redact(logEvent, _redactor));
    }

    /// <summary>
    /// Returns a redacted copy of <paramref name="logEvent"/>, or the very same instance when
    /// nothing in it matched a secret pattern.
    /// </summary>
    /// <param name="logEvent">The event to inspect.</param>
    /// <param name="redactor">The redactor to apply.</param>
    /// <returns>The original event when clean; otherwise a rewritten event.</returns>
    public static LogEvent Redact(LogEvent logEvent, ISecretRedactor redactor)
    {
        ArgumentNullException.ThrowIfNull(logEvent);
        ArgumentNullException.ThrowIfNull(redactor);

        var template = RedactTemplate(logEvent.MessageTemplate, redactor);
        var properties = RedactProperties(logEvent, redactor);

        if (template is null && properties is null)
            return logEvent;

        return new LogEvent(
            logEvent.Timestamp,
            logEvent.Level,
            logEvent.Exception,
            template ?? logEvent.MessageTemplate,
            properties ?? logEvent.Properties.Select(pair => new LogEventProperty(pair.Key, pair.Value)));
    }

    // Returns null when no literal text in the template contained a secret, so the caller can keep
    // the original template instance and skip the token-list allocation entirely.
    private static MessageTemplate? RedactTemplate(MessageTemplate template, ISecretRedactor redactor)
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

            var redacted = redactor.Redact(text.Text);
            if (string.Equals(redacted, text.Text, StringComparison.Ordinal))
            {
                if (rewritten is not null)
                    rewritten.Add(token);
                else
                    (seen ??= []).Add(token);
                continue;
            }

            rewritten ??= seen ?? [];
            rewritten.Add(new TextToken(redacted));
        }

        return rewritten is null ? null : new MessageTemplate(rewritten);
    }

    // Returns null when every property value was already clean.
    private static List<LogEventProperty>? RedactProperties(LogEvent logEvent, ISecretRedactor redactor)
    {
        List<LogEventProperty>? rewritten = null;

        foreach (var property in logEvent.Properties)
        {
            var redacted = RedactValue(property.Value, redactor);
            if (redacted is null)
            {
                rewritten?.Add(new LogEventProperty(property.Key, property.Value));
                continue;
            }

            rewritten ??= BuildPrefix(logEvent, property.Key);
            rewritten.Add(new LogEventProperty(property.Key, redacted));
        }

        return rewritten;
    }

    // Copies the already-inspected (and therefore clean) properties that precede the first dirty
    // one. Dictionary enumeration order is stable within a single enumeration, so taking everything
    // up to the first hit reproduces exactly the properties already skipped.
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

    // Returns null when the value is unchanged, which is what lets the whole rewrite be skipped for
    // a clean event rather than rebuilding an identical object graph.
    private static LogEventPropertyValue? RedactValue(LogEventPropertyValue value, ISecretRedactor redactor)
    {
        switch (value)
        {
            case ScalarValue { Value: string text }:
            {
                var redacted = redactor.Redact(text);
                return string.Equals(redacted, text, StringComparison.Ordinal) ? null : new ScalarValue(redacted);
            }

            case SequenceValue sequence:
            {
                List<LogEventPropertyValue>? elements = null;
                for (var index = 0; index < sequence.Elements.Count; index++)
                {
                    var redacted = RedactValue(sequence.Elements[index], redactor);
                    if (redacted is null)
                    {
                        elements?.Add(sequence.Elements[index]);
                        continue;
                    }

                    elements ??= [.. sequence.Elements.Take(index)];
                    elements.Add(redacted);
                }

                return elements is null ? null : new SequenceValue(elements);
            }

            case StructureValue structure:
            {
                List<LogEventProperty>? properties = null;
                for (var index = 0; index < structure.Properties.Count; index++)
                {
                    var property = structure.Properties[index];
                    var redacted = RedactValue(property.Value, redactor);
                    if (redacted is null)
                    {
                        properties?.Add(property);
                        continue;
                    }

                    properties ??= [.. structure.Properties.Take(index)];
                    properties.Add(new LogEventProperty(property.Name, redacted));
                }

                return properties is null ? null : new StructureValue(properties, structure.TypeTag);
            }

            case DictionaryValue dictionary:
            {
                List<KeyValuePair<ScalarValue, LogEventPropertyValue>>? entries = null;
                var index = 0;
                foreach (var entry in dictionary.Elements)
                {
                    var redactedKey = RedactValue(entry.Key, redactor) as ScalarValue;
                    var redactedValue = RedactValue(entry.Value, redactor);
                    if (redactedKey is null && redactedValue is null)
                    {
                        entries?.Add(entry);
                        index++;
                        continue;
                    }

                    entries ??= [.. dictionary.Elements.Take(index)];
                    entries.Add(new KeyValuePair<ScalarValue, LogEventPropertyValue>(
                        redactedKey ?? entry.Key,
                        redactedValue ?? entry.Value));
                    index++;
                }

                return entries is null ? null : new DictionaryValue(entries);
            }

            default:
                return null;
        }
    }

    /// <summary>
    /// Disposes the wrapped sink when it owns disposable resources (file handles, sub-loggers).
    /// </summary>
    public void Dispose() => (_inner as IDisposable)?.Dispose();
}
