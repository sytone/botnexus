using System.Text;
using System.Text.Json;
using BotNexus.AgentCore.Tools;
using BotNexus.AgentCore.Types;
using BotNexus.CodingAgent.Utils;
using BotNexus.Providers.Core.Models;

namespace BotNexus.CodingAgent.Tools;

public sealed class EditTool : IAgentTool
{
    private readonly string _workingDirectory;
    private readonly FileMutationQueue _fileMutationQueue;

    public EditTool(string workingDirectory)
    {
        _workingDirectory = string.IsNullOrWhiteSpace(workingDirectory)
            ? throw new ArgumentException("Working directory cannot be empty.", nameof(workingDirectory))
            : Path.GetFullPath(workingDirectory);
        _fileMutationQueue = FileMutationQueue.Shared;
    }

    public string Name => "edit";

    public string Label => "Edit File";

    public Tool Definition => new(
        Name,
        "Edit a single file using exact text replacement with one or more targeted edits.",
        JsonDocument.Parse("""
            {
              "type": "object",
              "additionalProperties": false,
              "properties": {
                "path": {
                  "type": "string",
                  "description": "Path to the file to edit (relative or absolute)."
                },
                "edits": {
                  "type": "array",
                  "description": "One or more targeted replacements. Each edit is matched against the original file, not incrementally.",
                  "items": {
                    "type": "object",
                    "additionalProperties": false,
                    "properties": {
                      "oldText": {
                        "type": "string",
                        "description": "Exact text for one targeted replacement."
                      },
                      "newText": {
                        "type": "string",
                        "description": "Replacement text for this targeted edit."
                      }
                    },
                    "required": ["oldText", "newText"]
                  }
                }
              },
              "required": ["path", "edits"]
            }
            """).RootElement.Clone());

    public Task<IReadOnlyDictionary<string, object?>> PrepareArgumentsAsync(
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        IReadOnlyDictionary<string, object?> prepared = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["path"] = ReadRequiredString(arguments, "path"),
            ["edits"] = ReadEdits(arguments)
        };

        return Task.FromResult(prepared);
    }

    public async Task<AgentToolResult> ExecuteAsync(
        string toolCallId,
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken = default,
        AgentToolUpdateCallback? onUpdate = null)
    {
        var rawPath = arguments["path"]?.ToString()
                      ?? throw new ArgumentException("Missing required argument: path.");
        var edits = ReadEdits(arguments);

        var fullPath = PathUtils.ResolvePath(rawPath, _workingDirectory);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException($"File '{rawPath}' does not exist.", fullPath);
        }

        return await _fileMutationQueue.WithFileLockAsync(fullPath, async () =>
        {
            var original = await File.ReadAllTextAsync(fullPath, cancellationToken).ConfigureAwait(false);
            original = original.TrimStart('\uFEFF');
            var originalLineEnding = DetectLineEnding(original);
            var normalizedOriginal = NormalizeLineEndings(original);
            var replacements = ResolveReplacements(normalizedOriginal, edits);
            var updatedNormalized = ApplyReplacements(normalizedOriginal, replacements);
            var updatedText = ApplyLineEnding(updatedNormalized, originalLineEnding);
            if (string.Equals(updatedText, original, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Edit produced no change — the replacement text is identical to the original.");
            }

            await File.WriteAllTextAsync(fullPath, updatedText, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);

            var firstChange = replacements[0];
            var snippet = BuildSnippet(updatedNormalized, firstChange.StartIndex, firstChange.NewText.Length);
            var relativePath = PathUtils.GetRelativePath(fullPath, _workingDirectory);
            var message = $"Successfully replaced {replacements.Count} block(s) in '{relativePath}'.\nContext:\n{snippet}";

            return new AgentToolResult([new AgentToolContent(AgentToolContentType.Text, message)]);
        }).ConfigureAwait(false);
    }

    private static IReadOnlyList<EditEntry> ReadEdits(IReadOnlyDictionary<string, object?> arguments)
    {
        if (arguments.TryGetValue("edits", out var editsValue) && editsValue is not null)
        {
            var edits = ParseEdits(editsValue);
            if (edits.Count == 0)
            {
                throw new ArgumentException("edits must contain at least one replacement.");
            }

            return edits;
        }

        if (TryReadLegacyEdit(arguments, "oldText", "newText", out var legacyEdit) ||
            TryReadLegacyEdit(arguments, "old_str", "new_str", out legacyEdit))
        {
            return [legacyEdit!];
        }

        throw new ArgumentException("Missing required argument: edits.");
    }

    private static List<EditEntry> ParseEdits(object value)
    {
        if (value is JsonElement element)
        {
            if (element.ValueKind != JsonValueKind.Array)
            {
                throw new ArgumentException("Argument 'edits' must be an array.");
            }

            return element.EnumerateArray().Select(ParseEditElement).ToList();
        }

        if (value is IEnumerable<object?> enumerable)
        {
            return enumerable.Select(ParseEditObject).ToList();
        }

        throw new ArgumentException("Argument 'edits' must be an array.");
    }

    private static EditEntry ParseEditObject(object? value)
    {
        return value switch
        {
            JsonElement element => ParseEditElement(element),
            IReadOnlyDictionary<string, object?> dict => new EditEntry(
                ReadRequiredString(dict, "oldText"),
                ReadRequiredString(dict, "newText")),
            _ => throw new ArgumentException("Each edits entry must be an object.")
        };
    }

    private static EditEntry ParseEditElement(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("Each edits entry must be an object.");
        }

        if (!element.TryGetProperty("oldText", out var oldTextElement) || oldTextElement.ValueKind != JsonValueKind.String)
        {
            throw new ArgumentException("Each edits entry must include oldText.");
        }

        if (!element.TryGetProperty("newText", out var newTextElement) || newTextElement.ValueKind != JsonValueKind.String)
        {
            throw new ArgumentException("Each edits entry must include newText.");
        }

        return new EditEntry(
            oldTextElement.GetString() ?? throw new ArgumentException("oldText cannot be null."),
            newTextElement.GetString() ?? throw new ArgumentException("newText cannot be null."));
    }

    private static bool TryReadLegacyEdit(
        IReadOnlyDictionary<string, object?> arguments,
        string oldKey,
        string newKey,
        out EditEntry? edit)
    {
        edit = null;
        if (!arguments.TryGetValue(oldKey, out var oldValue) ||
            !arguments.TryGetValue(newKey, out var newValue) ||
            oldValue is null ||
            newValue is null)
        {
            return false;
        }

        edit = new EditEntry(
            ReadRequiredString(arguments, oldKey),
            ReadRequiredString(arguments, newKey));
        return true;
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

    private static List<ResolvedEdit> ResolveReplacements(string normalizedOriginal, IReadOnlyList<EditEntry> edits)
    {
        var normalizedForFuzzy = NormalizeForFuzzyMatchWithMap(normalizedOriginal);
        var replacements = edits
            .Select(edit =>
            {
                var normalizedOld = NormalizeLineEndings(edit.OldText);
                var normalizedNew = NormalizeLineEndings(edit.NewText);
                var exactMatchCount = CountOccurrences(normalizedOriginal, normalizedOld);
                if (exactMatchCount > 1)
                {
                    throw new InvalidOperationException(
                        $"Expected exactly one match for edits[].oldText, but found {exactMatchCount}.");
                }

                if (exactMatchCount == 1)
                {
                    var start = normalizedOriginal.IndexOf(normalizedOld, StringComparison.Ordinal);
                    var end = start + normalizedOld.Length;
                    return new ResolvedEdit(start, end, normalizedOriginal[start..end], normalizedNew);
                }

                var fuzzyOld = NormalizeForFuzzyMatch(normalizedOld);
                var fuzzyMatchCount = CountOccurrences(normalizedForFuzzy.Normalized, fuzzyOld);
                if (fuzzyMatchCount != 1)
                {
                    throw new InvalidOperationException(
                        $"Expected exactly one match for edits[].oldText, but found {fuzzyMatchCount}.");
                }

                var fuzzyStart = normalizedForFuzzy.Normalized.IndexOf(fuzzyOld, StringComparison.Ordinal);
                var startIndex = normalizedForFuzzy.IndexMap[fuzzyStart];
                var endIndex = normalizedForFuzzy.IndexMap[fuzzyStart + fuzzyOld.Length];

                return new ResolvedEdit(
                    startIndex,
                    endIndex,
                    normalizedOriginal[startIndex..endIndex],
                    normalizedNew);
            })
            .OrderBy(edit => edit.StartIndex)
            .ToList();

        EnsureNonOverlapping(replacements);
        return replacements;
    }

    private static void EnsureNonOverlapping(List<ResolvedEdit> replacements)
    {
        for (var i = 1; i < replacements.Count; i++)
        {
            if (replacements[i].StartIndex < replacements[i - 1].EndIndex)
            {
                throw new InvalidOperationException("edits entries must not overlap.");
            }
        }
    }

    private static string ApplyReplacements(string source, IReadOnlyList<ResolvedEdit> replacements)
    {
        var delta = replacements.Sum(edit => edit.NewText.Length - (edit.EndIndex - edit.StartIndex));
        var builder = new StringBuilder(source.Length + delta);
        var cursor = 0;

        foreach (var replacement in replacements)
        {
            builder.Append(source.AsSpan(cursor, replacement.StartIndex - cursor));
            builder.Append(replacement.NewText);
            cursor = replacement.EndIndex;
        }

        builder.Append(source.AsSpan(cursor));
        return builder.ToString();
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        if (string.IsNullOrEmpty(needle))
        {
            throw new ArgumentException("edits[].oldText cannot be empty.");
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

    private static string NormalizeLineEndings(string value)
    {
        return value.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
    }

    private static string NormalizeForFuzzyMatch(string text)
    {
        return NormalizeForFuzzyMatchWithMap(text).Normalized;
    }

    private static NormalizedFuzzyText NormalizeForFuzzyMatchWithMap(string text)
    {
        var normalized = new StringBuilder(text.Length);
        var indexMap = new List<int>(text.Length + 1);
        var lineStart = 0;

        while (lineStart < text.Length)
        {
            var lineEnd = text.IndexOf('\n', lineStart);
            var hasLineBreak = lineEnd >= 0;
            var contentEnd = hasLineBreak ? lineEnd : text.Length;
            var trimmedEnd = contentEnd;

            while (trimmedEnd > lineStart && char.IsWhiteSpace(text[trimmedEnd - 1]) && text[trimmedEnd - 1] != '\n')
            {
                trimmedEnd--;
            }

            for (var i = lineStart; i < trimmedEnd; i++)
            {
                normalized.Append(NormalizeFuzzyChar(text[i]));
                indexMap.Add(i);
            }

            if (!hasLineBreak)
            {
                break;
            }

            normalized.Append('\n');
            indexMap.Add(lineEnd);
            lineStart = lineEnd + 1;
        }

        indexMap.Add(text.Length);
        return new NormalizedFuzzyText(normalized.ToString(), indexMap);
    }

    private static char NormalizeFuzzyChar(char value)
    {
        return value switch
        {
            '\u201C' or '\u201D' => '"',
            '\u2018' or '\u2019' => '\'',
            '\u2014' or '\u2013' => '-',
            '\u00A0' => ' ',
            _ => value
        };
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

    private static string BuildSnippet(string content, int replacementIndex, int replacementLength)
    {
        const int contextWindow = 120;
        var snippetStart = Math.Max(0, replacementIndex - contextWindow);
        var snippetEnd = Math.Min(content.Length, replacementIndex + replacementLength + contextWindow);
        return content[snippetStart..snippetEnd];
    }

    private sealed record EditEntry(string OldText, string NewText);

    private sealed record ResolvedEdit(int StartIndex, int EndIndex, string OldText, string NewText);

    private sealed record NormalizedFuzzyText(string Normalized, IReadOnlyList<int> IndexMap);
}
