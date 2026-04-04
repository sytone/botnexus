using System.Text;
using System.Text.Json;
using BotNexus.AgentCore.Tools;
using BotNexus.AgentCore.Types;
using BotNexus.CodingAgent.Utils;
using BotNexus.Providers.Core.Models;

namespace BotNexus.CodingAgent.Tools;

/// <summary>
/// Applies a single exact-string replacement to a file.
/// </summary>
/// <remarks>
/// <para>
/// Contract: this tool is intentionally strict to prevent ambiguous edits. The <c>old_str</c> token must
/// appear exactly once after line-ending normalization. Zero or multiple matches throw, forcing the caller
/// to provide a more precise replacement window.
/// </para>
/// <para>
/// Line endings are normalized to <c>\n</c> for matching and replacement, then converted back to the file's
/// dominant style so repository conventions remain stable.
/// </para>
/// </remarks>
public sealed class EditTool : IAgentTool
{
    private readonly string _workingDirectory;

    /// <summary>
    /// Initializes the edit tool.
    /// </summary>
    /// <param name="workingDirectory">Repository root used for secure file resolution.</param>
    public EditTool(string workingDirectory)
    {
        _workingDirectory = string.IsNullOrWhiteSpace(workingDirectory)
            ? throw new ArgumentException("Working directory cannot be empty.", nameof(workingDirectory))
            : Path.GetFullPath(workingDirectory);
    }

    /// <inheritdoc />
    public string Name => "edit";

    /// <inheritdoc />
    public string Label => "Edit File";

    /// <inheritdoc />
    public Tool Definition => new(
        Name,
        "Replace a single exact string in an existing file.",
        JsonDocument.Parse("""
            {
              "type": "object",
              "properties": {
                "path": {
                  "type": "string",
                  "description": "File path relative to working directory."
                },
                "old_str": {
                  "type": "string",
                  "description": "Exact text to replace."
                },
                "new_str": {
                  "type": "string",
                  "description": "Replacement text."
                }
              },
              "required": ["path", "old_str", "new_str"]
            }
            """).RootElement.Clone());

    /// <inheritdoc />
    public Task<IReadOnlyDictionary<string, object?>> PrepareArgumentsAsync(
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        IReadOnlyDictionary<string, object?> prepared = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["path"] = ReadRequiredString(arguments, "path"),
            ["old_str"] = ReadRequiredString(arguments, "old_str"),
            ["new_str"] = ReadRequiredString(arguments, "new_str")
        };

        return Task.FromResult(prepared);
    }

    /// <inheritdoc />
    public async Task<AgentToolResult> ExecuteAsync(
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken = default)
    {
        var rawPath = arguments["path"]?.ToString()
                      ?? throw new ArgumentException("Missing required argument: path.");
        var oldStr = arguments["old_str"]?.ToString()
                     ?? throw new ArgumentException("Missing required argument: old_str.");
        var newStr = arguments["new_str"]?.ToString()
                     ?? throw new ArgumentException("Missing required argument: new_str.");

        var fullPath = PathUtils.ResolvePath(rawPath, _workingDirectory);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException($"File '{rawPath}' does not exist.", fullPath);
        }

        var original = await File.ReadAllTextAsync(fullPath, cancellationToken).ConfigureAwait(false);
        var originalLineEnding = DetectLineEnding(original);
        var normalizedOriginal = NormalizeLineEndings(original);
        var normalizedOld = NormalizeLineEndings(oldStr);
        var normalizedNew = NormalizeLineEndings(newStr);

        var matchCount = CountOccurrences(normalizedOriginal, normalizedOld);
        if (matchCount != 1)
        {
            throw new InvalidOperationException(
                $"Expected exactly one match for old_str, but found {matchCount}.");
        }

        var matchIndex = normalizedOriginal.IndexOf(normalizedOld, StringComparison.Ordinal);
        var updatedNormalized = ReplaceFirst(normalizedOriginal, normalizedOld, normalizedNew, matchIndex);
        var updatedText = ApplyLineEnding(updatedNormalized, originalLineEnding);

        await File.WriteAllTextAsync(fullPath, updatedText, Encoding.UTF8, cancellationToken).ConfigureAwait(false);

        var snippet = BuildSnippet(updatedNormalized, matchIndex, normalizedNew.Length);
        var relativePath = PathUtils.GetRelativePath(fullPath, _workingDirectory);
        var message = $"Applied replacement in '{relativePath}'.\nContext:\n{snippet}";

        return new AgentToolResult([new AgentToolContent(AgentToolContentType.Text, message)]);
    }

    private static string ReadRequiredString(IReadOnlyDictionary<string, object?> arguments, string key)
    {
        if (!arguments.TryGetValue(key, out var value) || value is null)
        {
            throw new ArgumentException($"Missing required argument: {key}.");
        }

        return value switch
        {
            JsonElement { ValueKind: JsonValueKind.String } element => element.GetString()
                ?? throw new ArgumentException($"Argument '{key}' cannot be null."),
            JsonElement element => element.ToString(),
            _ => value.ToString() ?? throw new ArgumentException($"Argument '{key}' is invalid.")
        };
    }

    private static string NormalizeLineEndings(string value)
    {
        return value.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
    }

    private static string DetectLineEnding(string value)
    {
        return value.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
    }

    private static string ApplyLineEnding(string value, string lineEnding)
    {
        return lineEnding == "\r\n"
            ? value.Replace("\n", "\r\n", StringComparison.Ordinal)
            : value;
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        if (string.IsNullOrEmpty(needle))
        {
            throw new ArgumentException("old_str cannot be empty.");
        }

        var count = 0;
        var index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }

    private static string ReplaceFirst(string source, string oldValue, string newValue, int matchIndex)
    {
        var builder = new StringBuilder(source.Length - oldValue.Length + newValue.Length);
        builder.Append(source.AsSpan(0, matchIndex));
        builder.Append(newValue);
        builder.Append(source.AsSpan(matchIndex + oldValue.Length));
        return builder.ToString();
    }

    private static string BuildSnippet(string content, int replacementIndex, int replacementLength)
    {
        const int contextWindow = 120;
        var snippetStart = Math.Max(0, replacementIndex - contextWindow);
        var snippetEnd = Math.Min(content.Length, replacementIndex + replacementLength + contextWindow);
        return content[snippetStart..snippetEnd];
    }
}
