using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BotNexus.Agent.Providers.Core.Diagnostics;

/// <summary>
/// Shared ActivitySource for BotNexus provider instrumentation.
/// </summary>
public static class ProviderDiagnostics
{
    public const string SourceName = "BotNexus.Providers";

    public static readonly ActivitySource Source = new(SourceName);

    private static ILoggerFactory _loggerFactory = NullLoggerFactory.Instance;

    /// <summary>
    /// Ambient logger factory for provider code paths that are static conversion helpers and
    /// therefore have no logger injected (the message converters). Composition roots set this once
    /// so those seams can report anomalies — such as dropped image content parts (#2485) — instead of
    /// failing silently. Defaults to <see cref="NullLoggerFactory"/>.
    /// </summary>
    public static ILoggerFactory LoggerFactory
    {
        get => _loggerFactory;
        set => _loggerFactory = value ?? NullLoggerFactory.Instance;
    }

    /// <summary>
    /// Creates a logger from the ambient <see cref="LoggerFactory"/> for a static provider helper.
    /// </summary>
    public static ILogger CreateLogger(string categorySuffix) =>
        _loggerFactory.CreateLogger($"{SourceName}.{categorySuffix}");
}
