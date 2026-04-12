using System.Text;
using System.Text.Json;
using BotNexus.AgentCore.Tools;
using BotNexus.AgentCore.Types;
using BotNexus.Gateway.Abstractions.Security;
using BotNexus.Tools.Extensions;
using BotNexus.Tools.Utils;
using BotNexus.Providers.Core.Models;
using DiffPlex.DiffBuilder;
using DiffPlex.DiffBuilder.Model;
using System.IO.Abstractions;

namespace BotNexus.Tools;

/// <summary>
/// Represents edit tool.
/// </summary>
public sealed class EditTool : IAgentTool
{
    private readonly string _workingDirectory;
    private readonly IPathValidator? _validator;
    private readonly FileMutationQueue _fileMutationQueue;
    private readonly IFileSystem _fileSystem;

    public EditTool(string workingDirectory, IFileSystem? fileSystem = null)
        : this(workingDirectory, validator: null, fileSystem)
    {
    }

    public EditTool(string workingDirectory, IPathValidator? validator, IFileSystem? fileSystem = null)
    {
        _workingDirectory = string.IsNullOrWhiteSpace(workingDirectory)
            ? throw new ArgumentException("Working directory cannot be empty.", nameof(workingDirectory))
            : Path.GetFullPath(workingDirectory);
        _validator = validator;
        _fileMutationQueue = FileMutationQueue.Shared;
        _fileSystem = fileSystem ?? new FileSystem();
    }

    public string Name => "edit";

    public string Label => "Edit File";

    /// <summary>
    /// Executes new.
    /// </summary>
    /// <param name="Name">The name.</param>
    /// <param name="false">The false.</param>
    /// <param name="false">The false.</param>
    /// <param name="arguments">The arguments.</param>
    /// <returns>The new result.</returns>
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

    /// <summary>
    /// Executes prepare arguments async.
    /// </summary>
    /// <param name="arguments">The arguments.</param>
    /// <returns>The prepare arguments async result.</returns>
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

    /// <summary>
    /// Executes execute async.
    /// </summary>
    /// <param name="toolCallId">The tool call id.</param>
    /// <param name="arguments">The arguments.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The execute async result.</returns>
    public async Task<AgentToolResult> ExecuteAsync(
        string toolCallId,
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken = default,
        AgentToolUpdateCallback? onUpdate = null)
    {
        var rawPath = arguments["path"]?.ToString()
                      ?? throw new ArgumentException("Missing required argument: path.");
        var edits = ReadEdits(arguments);

        var fullPath = _validator?.ValidateAndResolve(rawPath, FileAccessMode.Write);
        if (_validator is not null && fullPath is null)
        {
            return new AgentToolResult(
                [new AgentToolContent(AgentToolContentType.Text, $"Access denied: path '{rawPath}' is not permitted for write")]);
        }

        fullPath ??= PathUtils.ResolvePath(rawPath, _workingDirectory, _fileSystem);
        if (!_fileSystem.File.Exists(fullPath))
        {
            throw new FileNotFoundException($"File '{rawPath}' does not exist.", fullPath);
        }

        return await _fileMutationQueue.WithFileLockAsync(fullPath, async () =>
        {
            var originalBytes = _fileSystem.File.ReadAllBytes(fullPath);
            var hasUtf8Bom = HasUtf8Bom(originalBytes);
            var original = Encoding.UTF8.GetString(originalBytes);
            if (hasUtf8Bom && original.StartsWith('\uFEFF'))
            {
                original = original[1..];
            }
            var originalLineEnding = DetectLineEnding(original);
            var normalizedOriginal = original.NormalizeLineEndings();
            var replacements = ResolveReplacements(normalizedOriginal, edits);
            var updatedNormalized = ApplyReplacements(normalizedOriginal, replacements);
            var updatedText = ApplyLineEnding(updatedNormalized, originalLineEnding);
            if (string.Equals(updatedText, original, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Edit produced no change — the replacement text is identical to the original.");
            }

            _fileSystem.File.WriteAllText(fullPath, updatedText, new UTF8Encoding(hasUtf8Bom));

            var relativePath = PathUtils.GetRelativePath(fullPath, _workingDirectory);
            var diff = BuildUnifiedDiff(relativePath, normalizedOriginal, updatedNormalized);
            var message = $"Successfully replaced {replacements.Count} block(s) in '{relativePath}'.\n{diff}";

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
                var normalizedOld = edit.OldText.NormalizeLineEndings();
                var normalizedNew = edit.NewText.NormalizeLineEndings();
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
                var mapped = NormalizeFuzzyText(text[i]);
                foreach (var mappedChar in mapped)
                {
                    normalized.Append(mappedChar);
                    indexMap.Add(i);
                }
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

    private static string NormalizeFuzzyText(char value)
    {
        var normalized = value.ToString().Normalize(NormalizationForm.FormKC);
        var builder = new StringBuilder(normalized.Length);
        foreach (var character in normalized)
        {
            builder.Append(character switch
            {
                '\u2018' or '\u2019' or '\u201A' or '\u201B' => '\'',
                '\u201C' or '\u201D' or '\u201E' or '\u201F' => '"',
                '\u2010' or '\u2011' or '\u2012' or '\u2013' or '\u2014' or '\u2015' or '\u2212' => '-',
                '\u00A0' or '\u2002' or '\u2003' or '\u2004' or '\u2005' or '\u2006' or '\u2007' or '\u2008' or '\u2009' or '\u200A' or '\u202F' or '\u205F' or '\u3000' => ' ',
                _ => character
            });
        }

        return builder.ToString();
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

    private static bool HasUtf8Bom(byte[] bytes)
    {
        return bytes.Length >= 3 &&
               bytes[0] == 0xEF &&
               bytes[1] == 0xBB &&
               bytes[2] == 0xBF;
    }

    private static string BuildUnifiedDiff(string relativePath, string before, string after)
    {
        var diff = InlineDiffBuilder.Diff(before, after);
        var lines = diff.Lines;
        var changedLineIndexes = new List<int>();
        for (var index = 0; index < lines.Count; index++)
        {
            if (lines[index].Type != ChangeType.Unchanged)
            {
                changedLineIndexes.Add(index);
            }
        }

        if (changedLineIndexes.Count == 0)
        {
            return string.Empty;
        }

        const int contextLines = 3;
        var hunks = new List<(int Start, int End)>();
        var currentStart = Math.Max(0, changedLineIndexes[0] - contextLines);
        var currentEnd = Math.Min(lines.Count - 1, changedLineIndexes[0] + contextLines);

        for (var i = 1; i < changedLineIndexes.Count; i++)
        {
            var nextStart = Math.Max(0, changedLineIndexes[i] - contextLines);
            var nextEnd = Math.Min(lines.Count - 1, changedLineIndexes[i] + contextLines);
            if (nextStart <= currentEnd + 1)
            {
                currentEnd = nextEnd;
                continue;
            }

            hunks.Add((currentStart, currentEnd));
            currentStart = nextStart;
            currentEnd = nextEnd;
        }

        hunks.Add((currentStart, currentEnd));

        var builder = new StringBuilder();
        builder.AppendLine($"--- a/{relativePath.Replace('\\', '/')}");
        builder.AppendLine($"+++ b/{relativePath.Replace('\\', '/')}");
        foreach (var (start, end) in hunks)
        {
            var oldLine = 1;
            var newLine = 1;
            for (var i = 0; i < start; i++)
            {
                if (lines[i].Type != ChangeType.Inserted)
                {
                    oldLine++;
                }

                if (lines[i].Type != ChangeType.Deleted)
                {
                    newLine++;
                }
            }

            var oldCount = 0;
            var newCount = 0;
            for (var i = start; i <= end; i++)
            {
                if (lines[i].Type != ChangeType.Inserted)
                {
                    oldCount++;
                }

                if (lines[i].Type != ChangeType.Deleted)
                {
                    newCount++;
                }
            }

            builder.AppendLine($"@@ -{oldLine},{oldCount} +{newLine},{newCount} @@");
            for (var i = start; i <= end; i++)
            {
                var line = lines[i];
                switch (line.Type)
                {
                    case ChangeType.Unchanged:
                        builder.AppendLine($" {line.Text}");
                        break;
                    case ChangeType.Deleted:
                        builder.AppendLine($"-{line.Text}");
                        break;
                    case ChangeType.Inserted:
                        builder.AppendLine($"+{line.Text}");
                        break;
                    case ChangeType.Modified:
                        builder.AppendLine($"-{line.Text}");
                        builder.AppendLine($"+{line.Text}");
                        break;
                    case ChangeType.Imaginary:
                        break;
                }
            }
        }

        return builder.ToString().TrimEnd();
    }

    private sealed record EditEntry(string OldText, string NewText);

    private sealed record ResolvedEdit(int StartIndex, int EndIndex, string OldText, string NewText);

    private sealed record NormalizedFuzzyText(string Normalized, IReadOnlyList<int> IndexMap);
}
