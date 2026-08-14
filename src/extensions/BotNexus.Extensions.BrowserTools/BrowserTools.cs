using System.Text.Json;
using BotNexus.Agent.Core.Tools;
using BotNexus.Agent.Core.Types;
using BotNexus.Agent.Providers.Core.Models;

namespace BotNexus.Extensions.BrowserTools;

/// <summary>
/// Shared plumbing for the five browser tools (#3031 AC2/AC3).
/// </summary>
/// <remarks>
/// <para>
/// The schema shape is a contract, not a style choice (AC3). Every tool here declares a top-level
/// <c>"type": "object"</c> with no root-level <c>anyOf</c> and no nested unions, because several
/// providers reject a union-rooted tool schema outright and the failure surfaces as the whole
/// tool list being dropped - the agent simply has no browser tools and nothing says why. A test
/// walks all five schemas so a future tool cannot quietly reintroduce the shape.
/// </para>
/// <para>
/// Errors are returned as tool RESULTS rather than thrown wherever the condition is one the agent
/// could act on. "Chrome is not installed" is information; a stack trace is not.
/// </para>
/// </remarks>
public abstract class BrowserToolBase : IAgentTool
{
    private readonly Func<CancellationToken, Task<BrowserToolSession>> _sessionFactory;

    /// <summary>Creates the tool over a lazily-materialised session.</summary>
    protected BrowserToolBase(Func<CancellationToken, Task<BrowserToolSession>> sessionFactory)
    {
        _sessionFactory = sessionFactory ?? throw new ArgumentNullException(nameof(sessionFactory));
    }

    /// <inheritdoc />
    public abstract string Name { get; }

    /// <inheritdoc />
    public abstract string Label { get; }

    /// <summary>The tool description surfaced to the model.</summary>
    protected abstract string Description { get; }

    /// <summary>The raw JSON Schema body for this tool's parameters.</summary>
    protected abstract string ParametersJson { get; }

    /// <inheritdoc />
    public Tool Definition => new(
        Name, Description, JsonDocument.Parse(ParametersJson).RootElement.Clone());

    /// <inheritdoc />
    public virtual Task<IReadOnlyDictionary<string, object?>> PrepareArgumentsAsync(
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(arguments);
    }

    /// <inheritdoc />
    public async Task<AgentToolResult> ExecuteAsync(
        string toolCallId,
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken = default,
        AgentToolUpdateCallback? onUpdate = null)
    {
        try
        {
            var session = await _sessionFactory(cancellationToken).ConfigureAwait(false);
            return await RunAsync(session, arguments, cancellationToken).ConfigureAwait(false);
        }
        catch (AgentBrowserUnavailableException ex)
        {
            // AC6: the actionable path. This is the ONLY place the unavailable condition becomes
            // agent-visible text, so the guidance the exception carries is not paraphrased here.
            return Text(ex.Message);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (ArgumentException ex)
        {
            return Text($"{Name} rejected its arguments: {ex.Message}");
        }
    }

    /// <summary>Runs the tool body against a materialised session.</summary>
    protected abstract Task<AgentToolResult> RunAsync(
        BrowserToolSession session,
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken);

    /// <summary>Wraps text in a tool result.</summary>
    protected static AgentToolResult Text(string text) =>
        new([new AgentToolContent(AgentToolContentType.Text, text)]);

    /// <summary>Reads a required string argument, tolerating <see cref="JsonElement"/> boxing.</summary>
    protected static string RequiredString(
        IReadOnlyDictionary<string, object?> args, string key)
    {
        var value = OptionalString(args, key);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException($"'{key}' is required.");
        }

        return value;
    }

    /// <summary>Reads an optional string argument.</summary>
    protected static string? OptionalString(
        IReadOnlyDictionary<string, object?> args, string key)
    {
        if (!args.TryGetValue(key, out var value) || value is null)
        {
            return null;
        }

        return value switch
        {
            string s => s,
            JsonElement { ValueKind: JsonValueKind.String } el => el.GetString(),
            JsonElement { ValueKind: JsonValueKind.Null } => null,
            JsonElement el => el.ToString(),
            _ => value.ToString(),
        };
    }

    /// <summary>Reads an optional boolean argument.</summary>
    protected static bool OptionalBool(
        IReadOnlyDictionary<string, object?> args, string key, bool fallback = false)
    {
        if (!args.TryGetValue(key, out var value) || value is null)
        {
            return fallback;
        }

        return value switch
        {
            bool b => b,
            JsonElement { ValueKind: JsonValueKind.True } => true,
            JsonElement { ValueKind: JsonValueKind.False } => false,
            string s when bool.TryParse(s, out var parsed) => parsed,
            _ => fallback,
        };
    }
}

/// <summary>
/// <c>browser_navigate</c>: opens a URL in the agent's isolated browser session (#3031).
/// </summary>
public sealed class BrowserNavigateTool(Func<CancellationToken, Task<BrowserToolSession>> sessionFactory)
    : BrowserToolBase(sessionFactory)
{
    /// <inheritdoc />
    public override string Name => "browser_navigate";

    /// <inheritdoc />
    public override string Label => "Browser Navigate";

    /// <inheritdoc />
    protected override string Description =>
        "Open a URL in a real Chrome browser and return the page's post-render text. Use this "
        + "instead of web_fetch when the page is JavaScript-rendered, requires interaction, or "
        + "must be observed as a user would see it. Public HTTP(S) hosts only: loopback, "
        + "private-range and cloud-metadata addresses are refused, and URLs carrying API keys or "
        + "credential-like query parameters are refused before the browser is touched.";

    /// <inheritdoc />
    protected override string ParametersJson => """
        {
          "type": "object",
          "properties": {
            "url": {
              "type": "string",
              "description": "Absolute http(s) URL to open."
            }
          },
          "required": ["url"]
        }
        """;

    /// <inheritdoc />
    protected override async Task<AgentToolResult> RunAsync(
        BrowserToolSession session,
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken)
    {
        var url = OptionalString(arguments, "url");

        // AC8: the guarded session, never the driver. A denial returns here having launched
        // nothing, which is what the "guarded rejection prevents subprocess launch" test asserts.
        var navigation = await session.Guarded.NavigateAsync(url, cancellationToken)
            .ConfigureAwait(false);

        if (!navigation.IsAllowed)
        {
            return Text(navigation.Reason!);
        }

        var snapshot = await session.Guarded.SnapshotAsync(cancellationToken).ConfigureAwait(false);
        return Text(snapshot.IsAllowed ? snapshot.Content! : snapshot.Reason!);
    }
}

/// <summary>
/// <c>browser_snapshot</c>: re-reads the current page through the guards (#3031).
/// </summary>
public sealed class BrowserSnapshotTool(Func<CancellationToken, Task<BrowserToolSession>> sessionFactory)
    : BrowserToolBase(sessionFactory)
{
    /// <inheritdoc />
    public override string Name => "browser_snapshot";

    /// <inheritdoc />
    public override string Label => "Browser Snapshot";

    /// <inheritdoc />
    protected override string Description =>
        "Read the current page's text as it stands now, after any clicks or typing. The page's "
        + "CURRENT location is re-validated before any content is returned, so a page that "
        + "redirects itself to a blocked address after load cannot be read back.";

    /// <inheritdoc />
    protected override string ParametersJson => """
        {
          "type": "object",
          "properties": {},
          "required": []
        }
        """;

    /// <inheritdoc />
    protected override async Task<AgentToolResult> RunAsync(
        BrowserToolSession session,
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken)
    {
        var snapshot = await session.Guarded.SnapshotAsync(cancellationToken).ConfigureAwait(false);
        return Text(snapshot.IsAllowed ? snapshot.Content! : snapshot.Reason!);
    }
}

/// <summary>
/// <c>browser_click</c>: clicks an element on the already-admitted page (#3031).
/// </summary>
public sealed class BrowserClickTool(Func<CancellationToken, Task<BrowserToolSession>> sessionFactory)
    : BrowserToolBase(sessionFactory)
{
    /// <inheritdoc />
    public override string Name => "browser_click";

    /// <inheritdoc />
    public override string Label => "Browser Click";

    /// <inheritdoc />
    protected override string Description =>
        "Click an element on the current page, identified by a CSS selector. Navigate first; a "
        + "click acts on whatever page the session is already showing.";

    /// <inheritdoc />
    protected override string ParametersJson => """
        {
          "type": "object",
          "properties": {
            "selector": {
              "type": "string",
              "description": "CSS selector of the element to click, e.g. 'button#submit'."
            }
          },
          "required": ["selector"]
        }
        """;

    /// <inheritdoc />
    protected override async Task<AgentToolResult> RunAsync(
        BrowserToolSession session,
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken)
    {
        var selector = RequiredString(arguments, "selector");
        await session.Interaction.ClickAsync(selector, cancellationToken).ConfigureAwait(false);
        return Text($"Clicked '{selector}'. Call browser_snapshot to read the resulting page.");
    }
}

/// <summary>
/// <c>browser_type</c>: types text into an element on the current page (#3031).
/// </summary>
public sealed class BrowserTypeTool(Func<CancellationToken, Task<BrowserToolSession>> sessionFactory)
    : BrowserToolBase(sessionFactory)
{
    /// <inheritdoc />
    public override string Name => "browser_type";

    /// <inheritdoc />
    public override string Label => "Browser Type";

    /// <inheritdoc />
    protected override string Description =>
        "Type text into an input on the current page, identified by a CSS selector. Do not type "
        + "credentials, API keys, or other secrets: the destination page is untrusted and "
        + "anything typed into it is disclosed to whoever controls it.";

    /// <inheritdoc />
    protected override string ParametersJson => """
        {
          "type": "object",
          "properties": {
            "selector": {
              "type": "string",
              "description": "CSS selector of the input to type into, e.g. 'input[name=q]'."
            },
            "text": {
              "type": "string",
              "description": "Literal text to type."
            },
            "submit": {
              "type": "boolean",
              "description": "Press Enter after typing. Default: false."
            }
          },
          "required": ["selector", "text"]
        }
        """;

    /// <inheritdoc />
    protected override async Task<AgentToolResult> RunAsync(
        BrowserToolSession session,
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken)
    {
        var selector = RequiredString(arguments, "selector");
        var text = OptionalString(arguments, "text") ?? string.Empty;
        var submit = OptionalBool(arguments, "submit");

        await session.Interaction.TypeAsync(selector, text, submit, cancellationToken)
            .ConfigureAwait(false);

        // The typed text is deliberately NOT echoed back. It lands in the transcript verbatim
        // otherwise, and the one thing a model is most likely to type is the thing least safe to
        // persist.
        return Text(
            $"Typed {text.Length} character(s) into '{selector}'"
            + (submit ? " and submitted." : ".")
            + " Call browser_snapshot to read the resulting page.");
    }
}

/// <summary>
/// <c>browser_screenshot</c>: captures the current page to the agent workspace (#3031).
/// </summary>
public sealed class BrowserScreenshotTool(Func<CancellationToken, Task<BrowserToolSession>> sessionFactory)
    : BrowserToolBase(sessionFactory)
{
    /// <inheritdoc />
    public override string Name => "browser_screenshot";

    /// <inheritdoc />
    public override string Label => "Browser Screenshot";

    /// <inheritdoc />
    protected override string Description =>
        "Capture a PNG screenshot of the current page into the agent workspace and return its "
        + "workspace-relative path. Returns a path rather than image bytes: the file can then be "
        + "read, attached, or discarded without the image occupying the context window.";

    /// <inheritdoc />
    protected override string ParametersJson => """
        {
          "type": "object",
          "properties": {
            "full_page": {
              "type": "boolean",
              "description": "Capture the entire scrollable page rather than the viewport. Default: false."
            }
          },
          "required": []
        }
        """;

    /// <inheritdoc />
    protected override async Task<AgentToolResult> RunAsync(
        BrowserToolSession session,
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken)
    {
        var fullPage = OptionalBool(arguments, "full_page");

        var absoluteDir = session.FileSystem.CombinePath(
            session.WorkspacePath, "tmp", "browser");
        session.FileSystem.CreateDirectory(absoluteDir);

        var name = $"screenshot-{DateTimeOffset.UtcNow.UtcDateTime:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}.png";
        var absolutePath = session.FileSystem.CombinePath(absoluteDir, name);

        await session.Interaction.ScreenshotAsync(absolutePath, fullPage, cancellationToken)
            .ConfigureAwait(false);

        // Workspace-relative for the same reason the snapshot spill is: it is what the agent's
        // read tool accepts, and an absolute path leaks the host's directory layout.
        return Text($"Screenshot written to tmp/browser/{name}");
    }
}
