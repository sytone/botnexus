using System.Text.Json;

namespace BotNexus.Agent.Providers.Core.Validation;

/// <summary>
/// Validates tool call arguments against the tool's JSON Schema parameters.
/// Called after LLM response parsing, before tool dispatch.
/// </summary>
/// <remarks>
/// Validation is preceded by a losslessly-safe coercion pass (issue #1552): a model
/// frequently emits a string-encoded integer (<c>"300"</c>) or a bare scalar where an
/// array is expected (<c>"platform"</c> instead of <c>["platform"]</c>). The tools
/// themselves already tolerate these shapes downstream (e.g. <c>AskUserTool.ReadInt</c>
/// parses numeric strings, <c>MemorySaveTool.ParseTags</c> only honours arrays), so a
/// strict reject-only validator would burn a turn for no benefit. The coercion pass
/// rewrites the corrected shape and returns it so the downstream tool receives the
/// fixed arguments; genuinely-wrong shapes (e.g. an object where an array is expected)
/// are still rejected, now with the received kind in the message.
/// </remarks>
public static class ToolCallValidator
{
    /// <summary>
    /// Characters of a long string value shown in a diagnostic before eliding. (Issue #2759.)
    /// </summary>
    private const int PreviewLength = 40;

    /// <summary>
    /// Validates arguments against the tool's parameter schema.
    /// Returns (isValid, errors) — invalid calls get an error ToolResult
    /// instead of dispatching to the tool.
    /// </summary>
    /// <param name="arguments">Tool call arguments to validate.</param>
    /// <param name="parameterSchema">JSON Schema parameter definition from the tool.</param>
    /// <returns>A tuple containing validation status and any validation errors.</returns>
    public static (bool IsValid, string[] Errors) Validate(
        JsonElement arguments,
        JsonElement parameterSchema)
        => Validate(arguments, parameterSchema, out _);

    /// <summary>
    /// Validates arguments against the tool's parameter schema, first coercing
    /// losslessly-safe shape mismatches (string-encoded numbers/booleans and
    /// scalar-for-array). The coerced arguments are returned via
    /// <paramref name="coercedArguments"/> so the caller can dispatch the corrected
    /// shape to the tool.
    /// </summary>
    /// <param name="arguments">Tool call arguments to validate.</param>
    /// <param name="parameterSchema">JSON Schema parameter definition from the tool.</param>
    /// <param name="coercedArguments">
    /// The arguments after coercion. Equal to <paramref name="arguments"/> when no
    /// coercion was applicable (or when arguments are not a JSON object).
    /// </param>
    /// <returns>A tuple containing validation status and any validation errors.</returns>
    public static (bool IsValid, string[] Errors) Validate(
        JsonElement arguments,
        JsonElement parameterSchema,
        out JsonElement coercedArguments)
    {
        coercedArguments = arguments;

        if (parameterSchema.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            return (true, []);
        }

        coercedArguments = CoerceArguments(arguments, parameterSchema);

        var errors = new List<string>();

        ValidateRequired(coercedArguments, parameterSchema, errors);
        ValidateTopLevelProperties(coercedArguments, parameterSchema, errors);
        ValidateAdditionalProperties(coercedArguments, parameterSchema, errors);

        return errors.Count == 0
            ? (true, [])
            : (false, errors.ToArray());
    }

    /// <summary>
    /// Produces a coerced copy of <paramref name="arguments"/> where each property whose
    /// supplied kind mismatches its schema type is rewritten to the schema type when the
    /// conversion is lossless and safe. Returns the original element when arguments are
    /// not an object or nothing needed coercing.
    /// </summary>
    private static JsonElement CoerceArguments(JsonElement arguments, JsonElement schema)
    {
        if (arguments.ValueKind != JsonValueKind.Object)
        {
            return arguments;
        }

        if (!schema.TryGetProperty("properties", out var propertiesElement) ||
            propertiesElement.ValueKind != JsonValueKind.Object)
        {
            return arguments;
        }

        var changed = false;
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (var argument in arguments.EnumerateObject())
            {
                writer.WritePropertyName(argument.Name);

                if (propertiesElement.TryGetProperty(argument.Name, out var propertySchema) &&
                    propertySchema.ValueKind == JsonValueKind.Object &&
                    TryCoerceValue(argument.Value, propertySchema, writer))
                {
                    changed = true;
                }
                else
                {
                    argument.Value.WriteTo(writer);
                }
            }

            writer.WriteEndObject();
        }

        if (!changed)
        {
            return arguments;
        }

        using var coercedDocument = JsonDocument.Parse(buffer.ToArray());
        return coercedDocument.RootElement.Clone();
    }

    /// <summary>
    /// Attempts to write a coerced form of <paramref name="value"/> matching the schema
    /// type to <paramref name="writer"/>. Returns <c>true</c> when a coercion was applied
    /// (and written); <c>false</c> when no safe coercion applies (caller writes the
    /// original value verbatim).
    /// </summary>
    private static bool TryCoerceValue(JsonElement value, JsonElement propertySchema, Utf8JsonWriter writer)
    {
        // Cap the synchronous in-process JSON.parse of model-controlled strings (issue #1738,
        // mirror OpenClaw): a string over this length is not parsed and is left to validation.
        const int MaxJsonCoerceLength = 64 * 1024;

        if (!propertySchema.TryGetProperty("type", out var typeElement))
        {
            return false;
        }

        var allowedTypes = GetAllowedTypes(typeElement);
        if (allowedTypes.Count == 0)
        {
            return false;
        }

        // Already matches an allowed type — no coercion needed.
        if (allowedTypes.Any(type => MatchesType(value, type)))
        {
            return false;
        }

        // string -> integer / number / boolean (lossless round-trip only).
        if (value.ValueKind == JsonValueKind.String)
        {
            var text = value.GetString();

            if (allowedTypes.Contains("integer") && TryParseInteger(text, out var integerValue))
            {
                writer.WriteNumberValue(integerValue);
                return true;
            }

            if (allowedTypes.Contains("number") && decimal.TryParse(text, out var numberValue))
            {
                writer.WriteNumberValue(numberValue);
                return true;
            }

            if (allowedTypes.Contains("boolean") && TryParseBoolean(text, out var boolValue))
            {
                writer.WriteBooleanValue(boolValue);
                return true;
            }
        }

        // string whose CONTENT is a JSON array/object (a model serialised a structured
        // param as a JSON string, e.g. "[\"a\",\"b\"]" or "{\"x\":1}"). Parse it into the
        // real shape so the downstream tool receives an array/object, not a literal string.
        // This must run BEFORE the scalar -> single-element array wrap below so a genuine
        // JSON-array string is parsed rather than wrapped or comma-split. (Issue #1738.)
        //
        // MaxJsonCoerceLength caps the synchronous in-process JSON.parse: the input is
        // model-controlled, so an unbounded parse here is a denial-of-service surface. A
        // string over the cap is left unparsed and falls through to the reject path.
        if (value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } json)
        {
            var trimmed = json.AsSpan().Trim();

            if (trimmed.Length > 0 && trimmed.Length <= MaxJsonCoerceLength)
            {
                if (allowedTypes.Contains("array") && trimmed[0] == '[' &&
                    TryWriteParsedJson(json, JsonValueKind.Array, writer))
                {
                    return true;
                }

                if (allowedTypes.Contains("object") && trimmed[0] == '{' &&
                    TryWriteParsedJson(json, JsonValueKind.Object, writer))
                {
                    return true;
                }
            }
        }

        // scalar -> single-element array (optionally comma-split for string items). A string
        // whose content looks like a JSON array/object is excluded: it was either parsed
        // above or is malformed/oversized, in which case it must reach the reject path rather
        // than be silently wrapped into a 1-element array that masks the wrong shape.
        // A scalar is also NOT wrapped when the declared item type cannot be a scalar (e.g.
        // edit.edits, whose items are objects). Wrapping "rename it" into ["rename it"] would
        // manufacture a schema-valid array whose single element is the wrong shape, pushing the
        // failure past validation into the tool where the diagnostic is far worse. (Issue #2415.)
        if (allowedTypes.Contains("array") && IsScalar(value) && !LooksLikeJsonStructure(value, allowedTypes) &&
            SchemaItemsAcceptScalars(propertySchema))
        {
            WriteScalarAsArray(value, propertySchema, writer);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Attempts to parse <paramref name="json"/> and, only when it parses to the
    /// <paramref name="expectedKind"/>, writes the parsed value to <paramref name="writer"/>.
    /// Returns <c>false</c> (writing nothing) on a parse failure or a kind mismatch so the
    /// caller can fall through. The parsed document is written before disposal.
    /// </summary>
    private static bool TryWriteParsedJson(string json, JsonValueKind expectedKind, Utf8JsonWriter writer)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != expectedKind)
            {
                return false;
            }

            document.RootElement.WriteTo(writer);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// True when <paramref name="value"/> is a string whose trimmed content begins like a
    /// JSON array (when an array is allowed) or object (when an object is allowed). Used to
    /// keep such strings out of the scalar -> single-element array wrap so a malformed or
    /// oversized JSON structure rejects instead of being silently wrapped.
    /// </summary>
    private static bool LooksLikeJsonStructure(JsonElement value, List<string> allowedTypes)
    {
        if (value.ValueKind != JsonValueKind.String || value.GetString() is not { Length: > 0 } text)
        {
            return false;
        }

        var trimmed = text.AsSpan().Trim();
        if (trimmed.Length == 0)
        {
            return false;
        }

        return (allowedTypes.Contains("array") && trimmed[0] == '[')
            || (allowedTypes.Contains("object") && trimmed[0] == '{');
    }

    private static void WriteScalarAsArray(JsonElement value, JsonElement propertySchema, Utf8JsonWriter writer)
    {
        writer.WriteStartArray();

        if (value.ValueKind == JsonValueKind.String &&
            SchemaItemsAreStrings(propertySchema) &&
            value.GetString() is { } text &&
            text.Contains(','))
        {
            foreach (var part in text.Split(','))
            {
                var trimmed = part.Trim();
                if (trimmed.Length > 0)
                {
                    writer.WriteStringValue(trimmed);
                }
            }
        }
        else
        {
            value.WriteTo(writer);
        }

        writer.WriteEndArray();
    }

    /// <summary>
    /// True when the schema's declared item type could legitimately be satisfied by a scalar,
    /// which is what the scalar to single-element array wrap produces. An <c>items</c> schema
    /// declaring only <c>object</c> or <c>array</c> returns <c>false</c> so the wrap is skipped
    /// and the caller rejects with the real type error instead. An absent or untyped
    /// <c>items</c> schema is permissive so the many tools that declare a bare <c>array</c>
    /// keep their historical behaviour. (Issue #2415.)
    /// </summary>
    private static bool SchemaItemsAcceptScalars(JsonElement propertySchema)
    {
        if (!propertySchema.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Object)
        {
            return true;
        }

        if (!items.TryGetProperty("type", out var itemType))
        {
            return true;
        }

        var itemTypes = GetAllowedTypes(itemType);
        if (itemTypes.Count == 0)
        {
            return true;
        }

        return itemTypes.Any(t => t is "string" or "integer" or "number" or "boolean");
    }

    private static bool SchemaItemsAreStrings(JsonElement propertySchema)
    {
        if (!propertySchema.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (!items.TryGetProperty("type", out var itemType))
        {
            return false;
        }

        return GetAllowedTypes(itemType).Contains("string");
    }

    private static bool IsScalar(JsonElement value)
        => value.ValueKind is JsonValueKind.String
            or JsonValueKind.Number
            or JsonValueKind.True
            or JsonValueKind.False;

    private static bool TryParseInteger(string? text, out long value)
    {
        value = 0;
        return !string.IsNullOrWhiteSpace(text) && long.TryParse(text, out value);
    }

    private static bool TryParseBoolean(string? text, out bool value)
    {
        value = false;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        return bool.TryParse(text, out value);
    }

    private static void ValidateRequired(JsonElement arguments, JsonElement schema, ICollection<string> errors)
    {
        if (!schema.TryGetProperty("required", out var requiredElement) || requiredElement.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        if (arguments.ValueKind != JsonValueKind.Object)
        {
            errors.Add("Arguments must be a JSON object.");
            return;
        }

        foreach (var required in requiredElement.EnumerateArray())
        {
            if (required.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var name = required.GetString();
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            if (!arguments.TryGetProperty(name, out _))
            {
                errors.Add($"Missing required property '{name}'.{DescribeRequiredSignature(arguments, requiredElement)}");
            }
        }
    }

    /// <summary>
    /// Builds the trailing clause for a missing-required-property error naming the sibling
    /// properties that WERE supplied and restating the full required signature. Issue #2415
    /// observed 6 weekly <c>edit</c> failures where a bare "Missing required property 'path'"
    /// left the model guessing whether its other arguments had also been rejected; naming both
    /// halves makes the retry one-shot. The "supplied" clause is omitted entirely when nothing
    /// was supplied rather than emitting an empty list.
    /// </summary>
    private static string DescribeRequiredSignature(JsonElement arguments, JsonElement requiredElement)
    {
        var required = requiredElement.EnumerateArray()
            .Where(r => r.ValueKind == JsonValueKind.String)
            .Select(r => r.GetString())
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .ToArray();

        var supplied = arguments.ValueKind == JsonValueKind.Object
            ? arguments.EnumerateObject().Select(p => p.Name).ToArray()
            : [];

        var clause = supplied.Length > 0
            ? $" You supplied: {string.Join(", ", supplied)}."
            : string.Empty;

        // Issue #2690: a missing 'path' was 37 of the 449 measured `edit` failures. Naming the
        // required properties still leaves the caller to infer the shape, so show a minimal
        // payload skeleton built from the schema's own required list.
        var skeleton = string.Join(", ", required.Select(name => $"\"{name}\": ..."));
        return $"{clause} This tool's required: {string.Join(", ", required)}."
               + $" Minimal valid payload: {{ {skeleton} }}.";
    }

    private static void ValidateTopLevelProperties(JsonElement arguments, JsonElement schema, ICollection<string> errors)
    {
        if (arguments.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        if (!schema.TryGetProperty("properties", out var propertiesElement) || propertiesElement.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        foreach (var argumentProperty in arguments.EnumerateObject())
        {
            if (!propertiesElement.TryGetProperty(argumentProperty.Name, out var propertySchema) ||
                propertySchema.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            ValidateType(argumentProperty, propertySchema, errors);
            ValidateEnum(argumentProperty, propertySchema, errors);
        }
    }

    private static void ValidateAdditionalProperties(JsonElement arguments, JsonElement schema, ICollection<string> errors)
    {
        if (arguments.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        if (!schema.TryGetProperty("additionalProperties", out var additionalProps))
        {
            return;
        }

        if (additionalProps.ValueKind != JsonValueKind.False)
        {
            return;
        }

        if (!schema.TryGetProperty("properties", out var propertiesElement) || propertiesElement.ValueKind != JsonValueKind.Object)
        {
            // No properties defined but additionalProperties is false - all properties are unknown.
            // There are no declared names to compare against, so no suggestion is possible here.
            foreach (var argumentProperty in arguments.EnumerateObject())
            {
                errors.Add($"Property '{argumentProperty.Name}' is not defined in the schema.");
            }
            return;
        }

        var declaredNames = propertiesElement.EnumerateObject().Select(p => p.Name).ToArray();

        foreach (var argumentProperty in arguments.EnumerateObject())
        {
            if (!propertiesElement.TryGetProperty(argumentProperty.Name, out _))
            {
                var message = $"Property '{argumentProperty.Name}' is not defined in the schema.";

                // Issue #2408: a misspelled property otherwise costs the model a whole turn of
                // guesswork. When a declared property is close enough to be a plausible typo,
                // name it so the model can self-correct on the very next turn. When nothing is
                // close the message stays byte-identical to the historical text.
                var suggestion = FindClosestPropertyName(argumentProperty.Name, declaredNames);
                if (suggestion is not null)
                {
                    message += $" Did you mean '{suggestion}'?";
                }

                errors.Add(message);
            }
        }
    }

    /// <summary>
    /// Returns the declared property name that is the most plausible typo-correction for
    /// <paramref name="unknownName"/>, or <c>null</c> when nothing is close enough.
    /// Comparison is case-insensitive so a casing-only mistake is always suggested.
    /// Ties are broken by ordinal order of the candidate name so the emitted message is
    /// deterministic regardless of schema property declaration order.
    /// </summary>
    /// <remarks>
    /// Issue #2759 AC4 asked for a missing-<c>path</c> error to name candidate files whose content
    /// matches the supplied <c>oldText</c> values. NOT IMPLEMENTED, deliberately: this validator is
    /// a pure function of (arguments, schema). It has no reference to the session, the turn's tool
    /// history, or the filesystem, so it cannot know which files were read this turn. Satisfying
    /// AC4 would require threading turn state into the validation seam, which is a plumbing change
    /// well outside a diagnostics fix and would couple a schema validator to conversation state.
    /// AC4 is reported unmet rather than built.
    /// </remarks>
    private static string? FindClosestPropertyName(string unknownName, IReadOnlyList<string> declaredNames)
    {
        if (declaredNames.Count == 0 || unknownName.Length == 0)
        {
            return null;
        }

        // Threshold scales with the length of the supplied token: short names must match
        // almost exactly (otherwise unrelated 2-3 character names look "close"), longer
        // names tolerate the two or three character slips typical of a real typo.
        var threshold = unknownName.Length <= 4 ? 1 : unknownName.Length <= 8 ? 2 : 3;

        string? best = null;
        var bestDistance = int.MaxValue;

        foreach (var candidate in declaredNames)
        {
            var distance = LevenshteinDistance(unknownName, candidate);
            if (distance > threshold)
            {
                continue;
            }

            // Strictly-better wins; an exact tie is resolved ordinal-first for determinism.
            if (distance < bestDistance ||
                (distance == bestDistance && best is not null && string.CompareOrdinal(candidate, best) < 0))
            {
                best = candidate;
                bestDistance = distance;
            }
        }

        return best;
    }

    /// <summary>
    /// Classic iterative two-row Levenshtein edit distance (insert/delete/substitute all
    /// cost 1), compared case-insensitively. Deliberately dependency-free and O(n*m) - the
    /// inputs are short JSON property names, so the naive form is more than fast enough.
    /// </summary>
    private static int LevenshteinDistance(string left, string right)
    {
        left = left.ToLowerInvariant();
        right = right.ToLowerInvariant();

        if (left.Length == 0)
        {
            return right.Length;
        }

        if (right.Length == 0)
        {
            return left.Length;
        }

        // previous[j] holds the distance for the previous source prefix; current[j] the row being built.
        var previous = new int[right.Length + 1];
        var current = new int[right.Length + 1];

        for (var j = 0; j <= right.Length; j++)
        {
            previous[j] = j;
        }

        for (var i = 1; i <= left.Length; i++)
        {
            current[0] = i;

            for (var j = 1; j <= right.Length; j++)
            {
                var substitutionCost = left[i - 1] == right[j - 1] ? 0 : 1;

                current[j] = Math.Min(
                    Math.Min(current[j - 1] + 1, previous[j] + 1),
                    previous[j - 1] + substitutionCost);
            }

            (previous, current) = (current, previous);
        }

        // The final completed row was swapped into 'previous'.
        return previous[right.Length];
    }

    private static void ValidateType(JsonProperty argumentProperty, JsonElement propertySchema, ICollection<string> errors)
    {
        if (!propertySchema.TryGetProperty("type", out var typeElement))
        {
            return;
        }

        var allowedTypes = GetAllowedTypes(typeElement);
        if (allowedTypes.Count == 0)
        {
            return;
        }

        if (allowedTypes.Any(type => MatchesType(argumentProperty.Value, type)))
        {
            return;
        }

        var message =
            $"Property '{argumentProperty.Name}' must be of type {string.Join(" or ", allowedTypes)} " +
            $"(received {DescribeValue(argumentProperty.Value)}).";

        // Issue #2415: 'edits' repeatedly arrived as a JSON string whose content was malformed
        // (a '>' where a ':' belongs), so the coercion pass could not parse it. Reporting only
        // the truncated payload tells the model nothing it can act on, so it retries blind.
        // Surfacing the parser's own reason and position turns the retry into a targeted fix.
        var parseFailure = DescribeJsonStringParseFailure(argumentProperty.Value, allowedTypes);
        if (parseFailure is not null)
        {
            message += $" It was a string and is not valid JSON: {parseFailure}";

            // Issue #2690: 'edits' arriving stringified was 28 of the 449 measured `edit`
            // failures. A well-formed JSON string is already coerced upstream (#1738/#2415), so
            // anything reaching here is a stringified structure that could NOT be recovered.
            // Naming the wrapper - rather than only the type mismatch - tells the caller the
            // payload was right and only the quoting was wrong, which is the one-shot fix.
            message += $" It appears to be a stringified {allowedTypes[0]}."
                       + $" Send '{argumentProperty.Name}' as a JSON {allowedTypes[0]} value, not as a quoted string.";
        }

        errors.Add(message);
    }

    /// <summary>
    /// Renders a short, safe description of the received value for diagnostics — the JSON
    /// kind plus the literal for short scalars (quoted for strings). Long or structured
    /// values are summarised by kind only to keep the error message bounded.
    /// </summary>
    /// <remarks>
    /// Issue #2759: the preview is elided at <see cref="PreviewLength"/> characters, and the
    /// elision used to be indistinguishable from the value itself having been cut short. The
    /// captured failures read <c>received string "[{"oldText":" \"lastRunUtc\": \"2026-08."</c>
    /// followed by "reached end of data", which reads as though the ARGUMENT were truncated when
    /// in fact only the DISPLAY was. The two are diagnosed and retried completely differently, so
    /// an elided preview now states the full character count of the value it elided.
    /// </remarks>
    private static string DescribeValue(JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.String:
                var text = value.GetString() ?? string.Empty;
                if (text.Length > PreviewLength)
                {
                    return $"string \"{text[..PreviewLength]}…\" "
                           + $"(preview only; the full value is {text.Length} characters)";
                }

                return $"string \"{text}\"";
            case JsonValueKind.Number:
                return $"number {value.GetRawText()}";
            case JsonValueKind.True:
            case JsonValueKind.False:
                return $"boolean {value.GetRawText()}";
            case JsonValueKind.Array:
                return "array";
            case JsonValueKind.Object:
                return "object";
            case JsonValueKind.Null:
                return "null";
            default:
                return value.ValueKind.ToString().ToLowerInvariant();
        }
    }

    /// <summary>
    /// When the rejected value is a string that clearly ATTEMPTED to encode the expected
    /// array/object (it starts with a bracket or brace), re-parses it purely to capture the
    /// parser's own message and byte position. Returns <c>null</c> for a string that never
    /// looked like JSON (claiming a parse failure there would be misleading) and for one that
    /// parses fine, which the coercion pass would already have handled. (Issue #2415.)
    /// </summary>
    private static string? DescribeJsonStringParseFailure(JsonElement value, List<string> allowedTypes)
    {
        if (!LooksLikeJsonStructure(value, allowedTypes) || value.GetString() is not { } text)
        {
            return null;
        }

        try
        {
            using var probe = JsonDocument.Parse(text);
            return null;
        }
        catch (JsonException ex)
        {
            var position = ex.BytePositionInLine ?? 0;

            // Issue #2759: report the parse offset AGAINST THE FULL VALUE and state that full
            // length, so the caller can tell an incomplete payload (offset lands at the end)
            // from a malformed one (offset lands in the middle). Without the length the offset
            // is uninterpretable and the model retries blind.
            var detail = $"{ex.Message} (position {position} of {text.Length} characters).";

            return IsTruncatedJson(ex, text)
                ? detail + " The value ends mid-structure, so it was cut short in transit rather"
                         + " than mis-typed: no coercion can recover it. Re-send the WHOLE value,"
                         + " splitting it across several smaller calls if it is large."
                : detail;
        }
    }

    /// <summary>
    /// True when the parse failed because the input simply STOPPED rather than because it was
    /// mis-typed — the parser consumed everything it was given and still wanted more.
    /// </summary>
    /// <remarks>
    /// Issue #2759 determination. The premise under investigation was that <c>edit</c> validates
    /// ahead of the #1562 coercion seam. That is REFUTED: <see cref="Validate"/> calls
    /// <c>CoerceArguments</c> before <c>ValidateRequired</c>/<c>ValidateTopLevelProperties</c>,
    /// <c>TryCoerceValue</c> parses a bracket-leading string into an array whenever the schema
    /// allows one, and <c>edit</c>'s schema declares <c>edits</c> as a plain top-level
    /// <c>"type": "array"</c>, so the coercion branch IS entered. A well-formed JSON-array string
    /// for <c>edits</c> is therefore already accepted today.
    ///
    /// The residual weekly failures are not a coercion gap. Every captured payload ends
    /// mid-token ("Expected end of string, but instead reached end of data"), i.e. the provider
    /// delivered an INCOMPLETE argument value. There is no correct parse of a prefix, so the only
    /// honest fix is diagnostic: say that the value is incomplete, and never let the elided
    /// display preview be mistaken for the cause.
    /// </remarks>
    private static bool IsTruncatedJson(JsonException exception, string text)
        => exception.LineNumber is { } line
           && exception.BytePositionInLine is { } position
           && line == CountNewlines(text)
           && position >= LastLineLength(text);

    private static long CountNewlines(string text) => text.Count(c => c == '\n');

    /// <summary>
    /// UTF-8 byte length of the final line — <see cref="JsonException.BytePositionInLine"/> counts
    /// BYTES, so a char count would misclassify any non-ASCII payload. (Issue #2759.)
    /// </summary>
    private static long LastLineLength(string text)
    {
        var lastNewline = text.LastIndexOf('\n');
        var lastLine = lastNewline < 0 ? text : text[(lastNewline + 1)..];
        return System.Text.Encoding.UTF8.GetByteCount(lastLine);
    }

    private static void ValidateEnum(JsonProperty argumentProperty, JsonElement propertySchema, ICollection<string> errors)
    {
        if (!propertySchema.TryGetProperty("enum", out var enumElement) || enumElement.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var allowedValue in enumElement.EnumerateArray())
        {
            if (JsonElementsEqual(argumentProperty.Value, allowedValue))
            {
                return;
            }
        }

        errors.Add($"Property '{argumentProperty.Name}' must be one of the allowed enum values.");
    }

    private static List<string> GetAllowedTypes(JsonElement typeElement)
    {
        var allowedTypes = new List<string>();

        switch (typeElement.ValueKind)
        {
            case JsonValueKind.String:
                var singleType = typeElement.GetString();
                if (!string.IsNullOrWhiteSpace(singleType))
                {
                    allowedTypes.Add(singleType);
                }

                break;

            case JsonValueKind.Array:
                foreach (var type in typeElement.EnumerateArray())
                {
                    if (type.ValueKind != JsonValueKind.String)
                    {
                        continue;
                    }

                    var typeName = type.GetString();
                    if (!string.IsNullOrWhiteSpace(typeName))
                    {
                        allowedTypes.Add(typeName);
                    }
                }

                break;
        }

        return allowedTypes;
    }

    private static bool MatchesType(JsonElement value, string schemaType)
    {
        return schemaType switch
        {
            "string" => value.ValueKind == JsonValueKind.String,
            "number" => value.ValueKind == JsonValueKind.Number,
            "integer" => value.ValueKind == JsonValueKind.Number && IsInteger(value),
            "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
            "object" => value.ValueKind == JsonValueKind.Object,
            "array" => value.ValueKind == JsonValueKind.Array,
            "null" => value.ValueKind == JsonValueKind.Null,
            _ => true
        };
    }

    private static bool IsInteger(JsonElement value)
    {
        if (value.TryGetInt64(out _) || value.TryGetUInt64(out _))
        {
            return true;
        }

        if (!value.TryGetDecimal(out var decimalValue))
        {
            return false;
        }

        return decimal.Truncate(decimalValue) == decimalValue;
    }

    private static bool JsonElementsEqual(JsonElement left, JsonElement right)
    {
        if (left.ValueKind != right.ValueKind)
        {
            if (left.ValueKind == JsonValueKind.Number && right.ValueKind == JsonValueKind.Number)
            {
                return NumbersEqual(left, right);
            }

            return false;
        }

        return left.ValueKind switch
        {
            JsonValueKind.String => string.Equals(left.GetString(), right.GetString(), StringComparison.Ordinal),
            JsonValueKind.Number => NumbersEqual(left, right),
            JsonValueKind.True or JsonValueKind.False => left.GetBoolean() == right.GetBoolean(),
            JsonValueKind.Null => true,
            _ => string.Equals(left.GetRawText(), right.GetRawText(), StringComparison.Ordinal)
        };
    }

    private static bool NumbersEqual(JsonElement left, JsonElement right)
    {
        if (left.TryGetDecimal(out var leftDecimal) && right.TryGetDecimal(out var rightDecimal))
        {
            return leftDecimal == rightDecimal;
        }

        return string.Equals(left.GetRawText(), right.GetRawText(), StringComparison.Ordinal);
    }
}
