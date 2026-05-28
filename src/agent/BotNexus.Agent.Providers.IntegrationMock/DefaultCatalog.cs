namespace BotNexus.Agent.Providers.IntegrationMock;

/// <summary>
/// Built-in scripts that ship with the integration-mock provider. Always loaded as a fallback
/// so well-known keys (currently <see cref="HelloWorldKey"/>) are available without any
/// configuration or external catalog file.
/// </summary>
public static class DefaultCatalog
{
    /// <summary>The well-known smoke-test key — produces a small "Hello, world!" stream.</summary>
    public const string HelloWorldKey = "HELLO_WORLD";

    /// <summary>
    /// The default catalog. Currently contains a single script for <see cref="HelloWorldKey"/>
    /// that emits four text deltas with 20ms gaps followed by a normal stop.
    /// </summary>
    public static readonly MockCatalog Catalog = new(
        new Dictionary<string, IReadOnlyList<ScriptedResponseStep>>(StringComparer.Ordinal)
        {
            [HelloWorldKey] = new List<ScriptedResponseStep>
            {
                new("text_delta", Delta: "Hello", DelayMs: 20),
                new("text_delta", Delta: ", ", DelayMs: 20),
                new("text_delta", Delta: "world", DelayMs: 20),
                new("text_delta", Delta: "!", DelayMs: 20),
                new("text_end"),
                new("done", StopReason: "stop")
            }
        });
}
