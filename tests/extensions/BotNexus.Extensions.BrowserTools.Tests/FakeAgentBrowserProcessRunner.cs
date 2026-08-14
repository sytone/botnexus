namespace BotNexus.Extensions.BrowserTools.Tests;

/// <summary>
/// The only <see cref="IAgentBrowserProcessRunner"/> any test uses (#3031 AC9).
/// </summary>
/// <remarks>
/// <para>
/// Nothing here starts a process. That is the mechanism by which AC9 holds - not a convention
/// about what tests should avoid, but the fact that the production path has exactly one route to
/// <c>System.Diagnostics.Process</c> and every test substitutes this in its place.
/// </para>
/// <para>
/// It records the full argument vector, the child environment, and the timeout of every
/// invocation, because those three are what AC4, AC5 and AC7 are assertions ABOUT. A fake that
/// recorded only "was I called" would leave all three untestable.
/// </para>
/// </remarks>
internal sealed class FakeAgentBrowserProcessRunner : IAgentBrowserProcessRunner
{
    /// <summary>One recorded invocation.</summary>
    internal sealed record Invocation(
        string BinaryPath,
        IReadOnlyList<string> Arguments,
        IReadOnlyDictionary<string, string> Environment,
        TimeSpan Timeout);

    /// <summary>Every invocation, in order.</summary>
    public List<Invocation> Invocations { get; } = [];

    /// <summary>Result returned when no per-command override matches.</summary>
    public AgentBrowserProcessResult DefaultResult { get; set; } =
        new(0, string.Empty, string.Empty);

    private readonly Dictionary<string, AgentBrowserProcessResult> _byCommand =
        new(StringComparer.Ordinal);

    /// <summary>Arranges the result for a specific agent-browser sub-command.</summary>
    public FakeAgentBrowserProcessRunner ForCommand(string command, AgentBrowserProcessResult result)
    {
        _byCommand[command] = result;
        return this;
    }

    /// <summary>The <c>--session</c> value of each invocation, in order.</summary>
    public IReadOnlyList<string> SessionArguments => Invocations
        .Select(i =>
        {
            var index = i.Arguments.ToList().IndexOf("--session");
            return index >= 0 && index + 1 < i.Arguments.Count ? i.Arguments[index + 1] : string.Empty;
        })
        .ToList();

    /// <summary>The sub-command (first non-flag argument after the session pair) of each call.</summary>
    public IReadOnlyList<string> Commands => Invocations
        .Select(i => CommandOf(i.Arguments))
        .ToList();

    private static string CommandOf(IReadOnlyList<string> arguments)
    {
        for (var i = 0; i < arguments.Count; i++)
        {
            if (arguments[i] == "--session")
            {
                i++;
                continue;
            }

            if (!arguments[i].StartsWith("--", StringComparison.Ordinal))
            {
                return arguments[i];
            }
        }

        return string.Empty;
    }

    public Task<AgentBrowserProcessResult> RunAsync(
        string binaryPath,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string> environment,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        Invocations.Add(new Invocation(binaryPath, arguments, environment, timeout));

        var command = CommandOf(arguments);
        return Task.FromResult(
            _byCommand.TryGetValue(command, out var arranged) ? arranged : DefaultResult);
    }
}
