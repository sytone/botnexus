using System.Text;
using System.Text.Json;
using BotNexus.Agent.Core.Tools;
using BotNexus.Agent.Core.Types;
using BotNexus.Gateway.Abstractions.Security;
using BotNexus.Tools.Extensions;
using BotNexus.Tools.Utils;
using BotNexus.Agent.Providers.Core.Models;
using DiffPlex.DiffBuilder;
using DiffPlex.DiffBuilder.Model;
using System.IO.Abstractions;

namespace BotNexus.Tools;

/// <summary>
/// Metadata describing the outcome of an <see cref="EditTool"/> invocation. Exposed via
/// <see cref="AgentToolResult.Details"/> so callers can distinguish a real mutation from an
/// idempotent no-op (issue #2100) without parsing the human-readable message.
/// </summary>
/// <param name="Changed">
/// <c>true</c> when at least one edit changed file bytes; <c>false</c> when every requested edit
/// was already satisfied and the file was left untouched.
/// </param>
/// <param name="AlreadySatisfiedIndices">
/// Zero-based indices (into the requested edits array) of edits whose replacement text was already
/// present, and so were treated as no-ops.
/// </param>
public sealed record EditResultDetails(bool Changed, IReadOnlyList<int> AlreadySatisfiedIndices);


/// <summary>
/// Metadata describing an <see cref="EditTool"/> invocation that was rejected because the file
/// changed since the caller read it (issue #2101). Exposed via <see cref="AgentToolResult.Details"/>
/// so a caller can distinguish a stale-content rejection from a real match failure and re-read
/// deterministically instead of retrying blindly with stale <c>oldText</c>.
/// </summary>
/// <param name="ExpectedHash">The concurrency token the caller supplied (from a prior <c>read</c>).</param>
/// <param name="ActualHash">The current concurrency token of the file on disk.</param>
public sealed record EditStaleContentDetails(string ExpectedHash, string ActualHash);


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
                "expectedHash": {
                  "type": "string",
                  "description": "Optional optimistic-concurrency token from the `read` tool's structured result. When supplied, the edit is rejected with a stale-content outcome if the file changed since it was read. Re-read immediately before editing; never reuse oldText after another edit; never copy oldText from shell output."
                },
                "edits": {
                  "type": "array",
                  "description": "One or more targeted replacements. Each edit is matched against the original file, not incrementally. Expected shape: { \"path\": \"...\", \"edits\": [ { \"oldText\": \"...\", \"newText\": \"...\" } ] }.",
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

        var prepared = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["path"] = ReadRequiredString(arguments, "path"),
            ["edits"] = ReadEdits(arguments)
        };

        var expectedHash = ReadOptionalExpectedHash(arguments);
        if (expectedHash is not null)
        {
            prepared["expectedHash"] = expectedHash;
        }

        return Task.FromResult<IReadOnlyDictionary<string, object?>>(prepared);
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
        var edits = arguments["edits"] as IReadOnlyList<EditEntry> ?? ReadEdits(arguments);
        var expectedHash = ReadOptionalExpectedHash(arguments);

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
            // UTF-8 first with a system-code-page fallback so editing a legacy windows-1252 / shift_jis
            // file matches against readable text (not mojibake). DecodeBytes already strips a leading
            // BOM; the guard below stays as defence in depth.
            var original = TextDecoder.DecodeBytes(originalBytes);
            if (hasUtf8Bom && original.StartsWith('\uFEFF'))
            {
                original = original[1..];
            }
            // Issue #2101: if the caller supplied a concurrency token, reject the edit when the file
            // changed since it was read. This converts a blind stale-content retry (the "found 0"
            // family) into one deterministic re-read, and never applies edits against changed bytes.
            if (expectedHash is not null)
            {
                var actualHash = ContentToken.Compute(original);
                if (!string.Equals(actualHash, expectedHash, StringComparison.Ordinal))
                {
                    var staleMessage =
                        "File changed since read; re-read before editing. "
                        + $"expectedHash '{expectedHash}' but the file is now '{actualHash}'. "
                        + "No edits were applied.";
                    return new AgentToolResult(
                        [new AgentToolContent(AgentToolContentType.Text, staleMessage)],
                        new EditStaleContentDetails(expectedHash, actualHash));
                }
            }

            var originalLineEnding = DetectLineEnding(original);
            var normalizedOriginal = original.NormalizeLineEndings();
            var resolved = ResolveReplacements(normalizedOriginal, edits);
            var relativePath = PathUtils.GetRelativePath(fullPath, _workingDirectory);

            // Issue #2100: an edit whose resolved target text already equals its replacement is an
            // idempotent no-op. Separate those from the edits that actually change bytes so an
            // already-satisfied entry never fails the batch and, when every entry is a no-op, the
            // whole call succeeds without touching the file.
            var alreadySatisfied = resolved
                .Where(edit => string.Equals(edit.OldText, edit.NewText, StringComparison.Ordinal))
                .OrderBy(edit => edit.EditIndex)
                .Select(edit => edit.EditIndex)
                .ToList();
            var effective = resolved
                .Where(edit => !string.Equals(edit.OldText, edit.NewText, StringComparison.Ordinal))
                .ToList();

            if (effective.Count == 0)
            {
                var noOpMessage = "No changes needed - replacement text is already present.";
                var noOpDetails = new EditResultDetails(false, alreadySatisfied);
                return new AgentToolResult(
                    [new AgentToolContent(AgentToolContentType.Text, noOpMessage)],
                    noOpDetails);
            }

            var updatedNormalized = ApplyReplacements(normalizedOriginal, effective);
            var updatedText = ApplyLineEnding(updatedNormalized, originalLineEnding);

            _fileSystem.File.WriteAllText(fullPath, updatedText, new UTF8Encoding(hasUtf8Bom));

            var diff = BuildUnifiedDiff(relativePath, normalizedOriginal, updatedNormalized);
            var message = $"Successfully replaced {effective.Count} block(s) in '{relativePath}'.";
            if (alreadySatisfied.Count > 0)
            {
                // Report which requested edits were already present so a mixed batch is transparent.
                var indices = string.Join(", ", alreadySatisfied);
                message += $" {alreadySatisfied.Count} edit(s) were already present (indices: {indices}).";
            }

            message += $"\n{diff}";

            var details = new EditResultDetails(true, alreadySatisfied);
            return new AgentToolResult(
                [new AgentToolContent(AgentToolContentType.Text, message)],
                details);
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
            EditEntry entry => entry,
            JsonElement element => ParseEditElement(element),
            IReadOnlyDictionary<string, object?> dict => new EditEntry(
                ReadRequiredString(dict, "oldText"),
                ReadRequiredString(dict, "newText")),
            _ => throw new ArgumentException("Each edits entry must be an object." + ShapeHint)
        };
    }

    private static EditEntry ParseEditElement(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("Each edits entry must be an object." + ShapeHint);
        }

        if (!element.TryGetProperty("oldText", out var oldTextElement) || oldTextElement.ValueKind != JsonValueKind.String)
        {
            throw new ArgumentException("Each edits entry must include oldText." + ShapeHint);
        }

        if (!element.TryGetProperty("newText", out var newTextElement) || newTextElement.ValueKind != JsonValueKind.String)
        {
            throw new ArgumentException("Each edits entry must include newText." + ShapeHint);
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

    private static string? ReadOptionalExpectedHash(IReadOnlyDictionary<string, object?> arguments)
    {
        if (!arguments.TryGetValue("expectedHash", out var value) || value is null)
        {
            return null;
        }

        var text = value switch
        {
            JsonElement { ValueKind: JsonValueKind.Null } => null,
            JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
            JsonElement element => element.ToString(),
            _ => value.ToString()
        };

        return string.IsNullOrWhiteSpace(text) ? null : text;
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
            .Select((edit, editIndex) =>
            {
                var normalizedOld = edit.OldText.NormalizeLineEndings();
                var normalizedNew = edit.NewText.NormalizeLineEndings();
                var exactMatchCount = CountOccurrences(normalizedOriginal, normalizedOld);
                if (exactMatchCount > 1)
                {
                    throw new InvalidOperationException(
                        $"Expected exactly one match for edits[].oldText, but found {exactMatchCount}."
                        + DescribeMatchLines(normalizedOriginal, normalizedOld));
                }

                if (exactMatchCount == 1)
                {
                    var start = normalizedOriginal.IndexOf(normalizedOld, StringComparison.Ordinal);
                    var end = start + normalizedOld.Length;
                    return new ResolvedEdit(editIndex, start, end, normalizedOriginal[start..end], normalizedNew);
                }

                var fuzzyOld = NormalizeForFuzzyMatch(normalizedOld);
                var fuzzyMatchCount = CountOccurrences(normalizedForFuzzy.Normalized, fuzzyOld);
                if (fuzzyMatchCount == 0)
                {
                    throw new InvalidOperationException(BuildNoMatchDiagnostic(normalizedOriginal, normalizedOld));
                }

                if (fuzzyMatchCount != 1)
                {
                    throw new InvalidOperationException(
                        $"Expected exactly one match for edits[].oldText, but found {fuzzyMatchCount}."
                        + DescribeFuzzyMatchLines(normalizedOriginal, normalizedForFuzzy, fuzzyOld));
                }

                var fuzzyStart = normalizedForFuzzy.Normalized.IndexOf(fuzzyOld, StringComparison.Ordinal);
                var startIndex = normalizedForFuzzy.IndexMap[fuzzyStart];
                var endIndex = normalizedForFuzzy.IndexMap[fuzzyStart + fuzzyOld.Length];

                return new ResolvedEdit(
                    editIndex,
                    startIndex,
                    endIndex,
                    normalizedOriginal[startIndex..endIndex],
                    normalizedNew);
            })
            .OrderBy(edit => edit.StartIndex)
            .ToList();

        EnsureNonOverlapping(replacements, normalizedOriginal);
        return replacements;
    }

    private static void EnsureNonOverlapping(List<ResolvedEdit> replacements, string normalizedOriginal)
    {
        for (var i = 1; i < replacements.Count; i++)
        {
            if (replacements[i].StartIndex < replacements[i - 1].EndIndex)
            {
                var previous = replacements[i - 1];
                var current = replacements[i];
                // Report which two resolved edits collide and their 1-based line ranges so the
                // caller can make them disjoint or merge them, instead of guessing blind.
                var previousRange = DescribeLineRange(normalizedOriginal, previous.StartIndex, previous.EndIndex);
                var currentRange = DescribeLineRange(normalizedOriginal, current.StartIndex, current.EndIndex);
                throw new InvalidOperationException(
                    "edits entries must not overlap."
                    + $" edits[{i - 1}] ({previousRange}) overlaps edits[{i}] ({currentRange})."
                    + " Make edits disjoint or combine them into one.");
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

    // Correct-shape example mirrored in the tool description so a malformed-entry error tells the
    // caller exactly what a valid edits payload looks like instead of leaving them to guess.
    private const string ShapeHint =
        " Expected shape: { \"path\": \"...\", \"edits\": [ { \"oldText\": \"...\", \"newText\": \"...\" } ] }.";

    /// <summary>
    /// Builds the line-location suffix for the ambiguous exact-match case (issue #1736): finds every
    /// exact occurrence of <paramref name="needle"/> in <paramref name="text"/>, converts each start
    /// offset to a 1-based line number, and returns a sentence such as
    /// <c> Matches at lines 44 and 207. Add surrounding context to oldText so it matches exactly one
    /// site.</c> Returns an empty string when fewer than two matches are present so callers can append
    /// unconditionally.
    /// </summary>
    private static string DescribeMatchLines(string text, string needle)
    {
        if (string.IsNullOrEmpty(needle))
        {
            return string.Empty;
        }

        var lineNumbers = new List<int>();
        var index = 0;
        while ((index = text.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            lineNumbers.Add(LineNumberAt(text, index));
            index += needle.Length;
        }

        return FormatMatchLines(lineNumbers);
    }

    /// <summary>
    /// The fuzzy-match counterpart of <see cref="DescribeMatchLines"/>: enumerates every occurrence of
    /// the fuzzy-normalized needle in the fuzzy-normalized text, maps each fuzzy start offset back to
    /// the original text via the index map, and reports the original-text line numbers. This keeps the
    /// reported lines accurate even when the match only succeeded after smart-quote/whitespace folding.
    /// </summary>
    private static string DescribeFuzzyMatchLines(string normalizedOriginal, NormalizedFuzzyText fuzzy, string fuzzyNeedle)
    {
        if (string.IsNullOrEmpty(fuzzyNeedle))
        {
            return string.Empty;
        }

        var lineNumbers = new List<int>();
        var index = 0;
        while ((index = fuzzy.Normalized.IndexOf(fuzzyNeedle, index, StringComparison.Ordinal)) >= 0)
        {
            var originalStart = fuzzy.IndexMap[index];
            lineNumbers.Add(LineNumberAt(normalizedOriginal, originalStart));
            index += fuzzyNeedle.Length;
        }

        return FormatMatchLines(lineNumbers);
    }

    private static string FormatMatchLines(List<int> lineNumbers)
    {
        if (lineNumbers.Count < 2)
        {
            return string.Empty;
        }

        return $" Matches at {FormatLineNumbers(lineNumbers)}."
               + " Add surrounding context to oldText so it matches exactly one site.";
    }

    /// <summary>
    /// Describes the 1-based line span an offset range covers as <c>line 12</c> (single line) or
    /// <c>lines 12-18</c> (multi-line), used to show where two overlapping edits collide.
    /// </summary>
    private static string DescribeLineRange(string text, int startIndex, int endIndex)
    {
        var startLine = LineNumberAt(text, startIndex);
        // The end offset is exclusive; step back one so a range that ends exactly at a line break
        // does not claim the following line.
        var endLine = LineNumberAt(text, Math.Max(startIndex, endIndex - 1));
        return startLine == endLine ? $"line {startLine}" : $"lines {startLine}-{endLine}";
    }

    /// <summary>
    /// Converts a character offset into a 1-based line number by counting the line breaks before it,
    /// matching the <c>\n</c>-counting approach the rest of the file uses on already line-normalized text.
    /// </summary>
    private static int LineNumberAt(string text, int offset)
    {
        var clamped = Math.Clamp(offset, 0, text.Length);
        var line = 1;
        for (var i = 0; i < clamped; i++)
        {
            if (text[i] == '\n')
            {
                line++;
            }
        }

        return line;
    }

    private static string FormatLineNumbers(List<int> lineNumbers)
    {
        if (lineNumbers.Count == 1)
        {
            return $"line {lineNumbers[0]}";
        }

        if (lineNumbers.Count == 2)
        {
            return $"lines {lineNumbers[0]} and {lineNumbers[1]}";
        }

        var leading = string.Join(", ", lineNumbers.Take(lineNumbers.Count - 1));
        return $"lines {leading} and {lineNumbers[^1]}";
    }

    /// <summary>
    /// Builds an actionable message for the 0-match case (issue #1555): instead of a bare
    /// "found 0", it anchors on the first non-empty line of <paramref name="normalizedOld"/>,
    /// finds the closest line in the file (best token/substring overlap), and reports its line
    /// number and text. When that closest line is identical to the anchor once leading/trailing
    /// whitespace is removed, it adds a hint that the only difference is indentation or invisible
    /// characters and to re-read the file with <c>read</c> for clean text. The message still
    /// contains the "found 0" token so existing tooling and tests keep working.
    /// </summary>
    private static string BuildNoMatchDiagnostic(string normalizedOriginal, string normalizedOld)
    {
        const string prefix = "Expected exactly one match for edits[].oldText, but found 0.";

        var anchor = FirstNonEmptyLine(normalizedOld);
        if (anchor is null)
        {
            return prefix;
        }

        var fileLines = normalizedOriginal.Split('\n');
        var anchorTrimmed = anchor.Trim();

        var bestIndex = -1;
        var bestScore = -1.0;
        for (var i = 0; i < fileLines.Length; i++)
        {
            var score = LineSimilarity(anchorTrimmed, fileLines[i].Trim());
            if (score > bestScore)
            {
                bestScore = score;
                bestIndex = i;
            }
        }

        if (bestIndex < 0 || bestScore <= 0)
        {
            return $"{prefix} No similar line was found in the file \u2014 re-read the file with `read` to confirm the current text.";
        }

        var closestLine = fileLines[bestIndex];
        var lineNumber = bestIndex + 1;
        var snippet = Truncate(closestLine, 200);
        var message = $"{prefix} The closest text in the file is at line {lineNumber}: \u00AB{snippet}\u00BB.";

        // If the only difference is surrounding whitespace/invisible characters, say so explicitly.
        if (string.Equals(closestLine.Trim(), anchorTrimmed, StringComparison.Ordinal))
        {
            message += " It differs only in leading/trailing whitespace or invisible characters \u2014 " +
                       "re-read the file with `read` to get clean text (pasted shell output can carry hidden ANSI/whitespace).";
        }

        return message;
    }

    private static string? FirstNonEmptyLine(string text)
    {
        foreach (var line in text.Split('\n'))
        {
            if (!string.IsNullOrWhiteSpace(line))
            {
                return line;
            }
        }

        return null;
    }

    /// <summary>
    /// Scores how similar two already-trimmed lines are in the range [0, 1]: exact match scores
    /// 1, otherwise the score is the length of the longest common substring relative to the longer
    /// line. Cheap and good enough to surface the line the model most likely meant.
    /// </summary>
    private static double LineSimilarity(string a, string b)
    {
        if (a.Length == 0 || b.Length == 0)
        {
            return 0;
        }

        if (string.Equals(a, b, StringComparison.Ordinal))
        {
            return 1;
        }

        var longest = LongestCommonSubstringLength(a, b);
        return (double)longest / Math.Max(a.Length, b.Length);
    }

    private static int LongestCommonSubstringLength(string a, string b)
    {
        // Rolling two-row DP to keep allocation small for long lines.
        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        var best = 0;

        for (var i = 1; i <= a.Length; i++)
        {
            for (var j = 1; j <= b.Length; j++)
            {
                if (a[i - 1] == b[j - 1])
                {
                    current[j] = previous[j - 1] + 1;
                    if (current[j] > best)
                    {
                        best = current[j];
                    }
                }
                else
                {
                    current[j] = 0;
                }
            }

            (previous, current) = (current, previous);
            Array.Clear(current);
        }

        return best;
    }

    private static string Truncate(string value, int maxLength)
    {
        return value.Length <= maxLength ? value : value[..maxLength] + "\u2026";
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
                // Surrogate pairs (emoji, supplementary plane chars) must be emitted together.
                // Processing a lone high surrogate through .Normalize() throws ArgumentException.
                if (char.IsHighSurrogate(text[i]))
                {
                    if (i + 1 < trimmedEnd && char.IsLowSurrogate(text[i + 1]))
                    {
                        normalized.Append(text[i]);
                        indexMap.Add(i);
                        normalized.Append(text[i + 1]);
                        indexMap.Add(i + 1);
                        i++;
                    }
                    else
                    {
                        // Lone surrogate — emit replacement character to avoid crash
                        normalized.Append('\uFFFD');
                        indexMap.Add(i);
                    }

                    continue;
                }

                // Lone low surrogate without preceding high — also replace
                if (char.IsLowSurrogate(text[i]))
                {
                    normalized.Append('\uFFFD');
                    indexMap.Add(i);
                    continue;
                }

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

    private sealed record ResolvedEdit(int EditIndex, int StartIndex, int EndIndex, string OldText, string NewText);

    private sealed record NormalizedFuzzyText(string Normalized, IReadOnlyList<int> IndexMap);
}
