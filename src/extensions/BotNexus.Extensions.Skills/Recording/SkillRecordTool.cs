using System.Text;
using System.Text.Json;
using BotNexus.Agent.Core.Tools;
using BotNexus.Agent.Core.Types;
using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Domain.Text;

namespace BotNexus.Extensions.Skills.Recording;

/// <summary>
/// Turns a run that worked into a reusable skill, through a propose-and-confirm cycle.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why this shape.</strong> Measurement of 1,702 recorded tool calls on this deployment
/// killed the obvious designs. Recording literally replays exactly once — only 27% of calls ever
/// repeat verbatim. Generalising by diffing repeated runs needs several runs of "the same task",
/// and deciding which runs are the same task is the problem the traces cannot answer. And with
/// arguments stripped there is nothing left to abstract over: the commonest three-step sequence on
/// the box is <c>bash → bash → bash</c>, 501 times.
/// </para>
/// <para>
/// So no step of this tool asks a trace to identify parameters. The division of labour is:
/// the TRACE supplies the literals, because it is the only witness to what actually ran; the AGENT
/// supplies the semantics, because it is the only party that knows why the steps were what they
/// were; and the OPERATOR rules on the result, because "does this vary between runs" is a question
/// about intent that neither of the other two can answer.
/// </para>
/// <para>
/// The cycle is <c>steps → propose → review → confirm</c>. Nothing reaches a skills directory until
/// <c>confirm</c>, and <c>confirm</c> requires the digest that <c>review</c> issued, so the skill
/// installed is byte-for-byte the one that was displayed.
/// </para>
/// </remarks>
public sealed class SkillRecordTool(
    SkillManagerTool writer,
    SkillDraftStore drafts,
    ISessionTraceSource? trace,
    SkillsConfig config,
    string? createdBy = null) : IAgentTool
{
    /// <summary>
    /// What to say when the trace is empty. Almost always the same cause, and it is not "nothing
    /// happened": a tool call made in the CURRENT turn has not been written to session history yet,
    /// so an agent that finishes a task and immediately asks for its own steps sees none of them.
    /// </summary>
    /// <remarks>
    /// Verified on the live instance: a turn that ran <c>bash</c> and then asked for <c>steps</c>
    /// got zero; asking again in the very next turn returned that same call with its arguments.
    /// Reading persisted history rather than the in-flight run is deliberate — it is what makes a
    /// recording survive compaction and what stops a proposal being checked against the agent's own
    /// account of itself — so this is a real constraint to explain, not a bug to paper over.
    /// The failure is at least safe in the other direction: <c>propose</c> validates against the
    /// same empty trace and refuses, so nothing can be recorded from a run that is not on record.
    /// </remarks>
    private const string EmptyTraceGuidance =
        "No tool calls are recorded for this session yet.\n\n" +
        "If you have just finished the work you want to record, this is expected: the calls you " +
        "made THIS turn are not written to session history until the turn completes, and the " +
        "recorder reads history rather than your own context — that is what makes a recording " +
        "survive compaction and keeps it checkable. Ask again on your next turn and they will be " +
        "here.\n\n" +
        "If this session genuinely ran no tools, there is nothing to record: a skill proposed here " +
        "would be prose with no run behind it.";

    /// <summary>Per-step argument budget in the <c>steps</c> listing.</summary>
    private const int MaxArgumentChars = 600;

    /// <summary>Longest description carried into a frontmatter parameter line.</summary>
    private const int MaxParameterDescriptionChars = 200;

    public string Name => "skill_record";

    public string Label => "Skill Recorder (Propose & Confirm)";

    /// <summary>
    /// Locally generated (#2519). Every action returns gateway-authored text, or tool ARGUMENTS the
    /// model itself authored earlier in this same session — never a tool's result content, which is
    /// where externally fetched material would live.
    /// </summary>
    public string ContentSource => ToolContentSource.Local;

    public Tool Definition => new(
        Name,
        "Turn a task you just completed into a reusable skill, by proposing which values were " +
        "parameters and having the operator confirm before anything is installed. Use it when a " +
        "non-trivial task succeeded and the same task will be asked again with different specifics. " +
        "Call it on a turn AFTER the work finished, not in the same turn: the recorder reads " +
        "persisted session history, and this turn's own tool calls are not written there until the " +
        "turn completes. " +
        "The cycle is: 'steps' to read back what this session actually ran; 'propose' to draft the " +
        "skill with {{placeholders}} and say which literals were parameters and why; 'review' to " +
        "show the operator exactly what would be installed and get a confirmation token; 'confirm' " +
        "with that token to install it. Nothing is installed before 'confirm'. Always show the " +
        "operator the 'review' output and get their agreement before confirming — the point of this " +
        "tool is that a person decides what varies, and confirming on your own defeats it.",
        JsonDocument.Parse("""
            {
              "type": "object",
              "properties": {
                "action": {
                  "type": "string",
                  "enum": ["steps", "propose", "review", "confirm", "discard", "list"],
                  "description": "'steps' - read back the tool calls this session actually made, so a proposal is built from the run rather than from memory; 'propose' - stage a draft skill from those steps; 'review' - render the pending draft exactly as it would be installed, and issue a confirmation token; 'confirm' - install the reviewed draft (requires the token); 'discard' - drop a pending draft; 'list' - show pending drafts."
                },
                "name": {
                  "type": "string",
                  "description": "Skill name. Required for propose, review, confirm and discard. Lowercase alphanumeric + hyphens, max 64 chars."
                },
                "content": {
                  "type": "string",
                  "description": "Required for 'propose'. Full SKILL.md content: YAML frontmatter with 'name' and 'description', then the instructions. Write each value that varies between runs as {{parameterName}}. Do NOT write a 'parameters:' frontmatter block yourself - it is generated from the 'parameters' argument at confirm time so the two cannot drift."
                },
                "parameters": {
                  "type": "array",
                  "description": "Required for 'propose' (may be empty for a skill with no varying values). One entry per {{placeholder}} in the content.",
                  "items": {
                    "type": "object",
                    "properties": {
                      "name": { "type": "string", "description": "Slot name, matching a {{placeholder}} in the content." },
                      "observedValue": { "type": "string", "description": "The literal value THIS run used. It is checked against the recorded tool calls; a value that appears in no recorded call is rejected." },
                      "description": { "type": "string", "description": "What a caller should supply, and why it varies between runs. This is what the operator reads when deciding." }
                    },
                    "required": ["name", "observedValue", "description"]
                  }
                },
                "scope": {
                  "type": "string",
                  "enum": ["agent", "workspace", "shared"],
                  "description": "Where the skill would be installed on confirm. Defaults to workspace."
                },
                "token": {
                  "type": "string",
                  "description": "Required for 'confirm'. The confirmation token issued by 'review'. It changes if the draft changes, so a stale token means the operator reviewed something else."
                }
              },
              "required": ["action"]
            }
            """).RootElement.Clone());

    public Task<IReadOnlyDictionary<string, object?>> PrepareArgumentsAsync(
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(arguments);
    }

    public async Task<AgentToolResult> ExecuteAsync(
        string toolCallId,
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken = default,
        AgentToolUpdateCallback? onUpdate = null)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!config.AllowSkillCreation)
            return Error("Skill management is not enabled for this agent, so recordings cannot be installed.");

        var action = (ReadString(arguments, "action") ?? string.Empty).Trim().ToLowerInvariant();

        return action switch
        {
            "steps" => await ListStepsAsync(cancellationToken).ConfigureAwait(false),
            "propose" => await ProposeAsync(arguments, cancellationToken).ConfigureAwait(false),
            "review" => Review(arguments),
            "confirm" => await ConfirmAsync(toolCallId, arguments, cancellationToken).ConfigureAwait(false),
            "discard" => Discard(arguments),
            "list" => ListDrafts(),
            _ => Error($"Unknown action: '{action}'. Valid: steps, propose, review, confirm, discard, list.")
        };
    }

    // ──────────────────────────── steps ─────────────────────────────────────

    private async Task<AgentToolResult> ListStepsAsync(CancellationToken cancellationToken)
    {
        var (steps, failure) = await TryReadTraceAsync(cancellationToken).ConfigureAwait(false);
        if (failure is not null)
            return failure;

        if (steps.Count == 0)
            return Ok(EmptyTraceGuidance);

        var sb = new StringBuilder();
        sb.AppendLine($"## Recorded steps for session {trace!.SessionId.Value}");
        sb.AppendLine();
        sb.AppendLine($"{steps.Count} tool call(s), oldest first. These come from persisted session history, " +
                      "not from your context, so they are complete even if this conversation was compacted.");
        sb.AppendLine();

        foreach (var step in steps)
        {
            sb.AppendLine($"{step.Ordinal}. **{step.ToolName}**{(step.IsError ? " _(errored)_" : string.Empty)}");
            if (!string.IsNullOrWhiteSpace(step.ArgumentsJson))
                sb.AppendLine($"   `{Truncate(Flatten(step.ArgumentsJson), MaxArgumentChars)}`");
        }

        sb.AppendLine();
        sb.AppendLine("Next: draft the skill with `propose`. Write every value that varies between runs as " +
                      "`{{name}}`, and declare each one with the literal value shown above — proposals whose " +
                      "observed values do not appear in these calls are rejected.");

        return Ok(sb.ToString());
    }

    // ──────────────────────────── propose ───────────────────────────────────

    private async Task<AgentToolResult> ProposeAsync(
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken)
    {
        if (!TryReadName(arguments, out var name, out var nameError))
            return Error(nameError);

        var content = ReadString(arguments, "content");
        if (string.IsNullOrWhiteSpace(content))
            return Error("'content' is required for propose.");

        if (!SkillManagerTool.TryResolveScope(arguments, out var scope, out var scopeError))
            return Error(scopeError);

        if (!TryReadParameters(arguments, out var parameters, out var parameterError))
            return Error(parameterError);

        // The frontmatter parameter block is generated at confirm time from the declared
        // parameters. Accepting a hand-written one too would create two sources of truth that
        // disagree the first time an operator renames a slot at review.
        if (HasFrontmatterKey(content, "parameters"))
            return Error(
                "The content's frontmatter declares 'parameters:'. Remove it — the block is generated " +
                "from the 'parameters' argument when the draft is confirmed, so the installed skill " +
                "always declares exactly what was confirmed.");

        var (steps, failure) = await TryReadTraceAsync(cancellationToken).ConfigureAwait(false);
        if (failure is not null)
            return failure;

        var validation = SkillDraftValidator.Validate(content, parameters, steps);
        if (!validation.IsValid)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"Proposal for '{name}' rejected. Nothing was staged.");
            sb.AppendLine();
            foreach (var error in validation.Errors)
                sb.AppendLine($"- {error}");
            return Error(sb.ToString().TrimEnd());
        }

        var draft = new SkillDraft
        {
            Name = name,
            Scope = ScopeName(scope),
            Content = content,
            Parameters = parameters,
            Steps = Summarise(steps),
            UnparameterisedLiterals = validation.UnparameterisedLiterals,
            Warnings = validation.Warnings,
            SessionId = trace!.SessionId.Value,
            CreatedBy = createdBy ?? "unknown",
            CreatedAt = DateTimeOffset.UtcNow
        };

        drafts.Save(draft);

        var report = new StringBuilder();
        report.AppendLine($"Draft '{name}' staged. It is NOT installed and is not discoverable as a skill.");
        report.AppendLine();
        report.AppendLine($"- {parameters.Count} parameter(s), each verified to appear in the recorded run");
        report.AppendLine($"- {steps.Count} recorded step(s) behind it");
        report.AppendLine($"- would install to the **{ScopeName(scope)}** scope on confirm");

        if (validation.Warnings.Count > 0)
        {
            report.AppendLine();
            report.AppendLine("Warnings:");
            foreach (var warning in validation.Warnings)
                report.AppendLine($"- {warning}");
        }

        report.AppendLine();
        report.AppendLine($"Next: run `review` with name '{name}', show the operator the whole output, and " +
                          "ask them to confirm the parameters and the literals that stayed fixed.");

        return Ok(report.ToString());
    }

    // ──────────────────────────── review ────────────────────────────────────

    private AgentToolResult Review(IReadOnlyDictionary<string, object?> arguments)
    {
        if (!TryReadName(arguments, out var name, out var nameError))
            return Error(nameError);

        var draft = drafts.TryLoad(name);
        if (draft is null)
            return Error($"No pending draft named '{name}'. Use action 'list' to see pending drafts.");

        var sb = new StringBuilder();
        sb.AppendLine($"# Review draft: {draft.Name}");
        sb.AppendLine();
        sb.AppendLine($"Recorded from session {draft.SessionId} by {draft.CreatedBy} at {draft.CreatedAt:u}. " +
                      $"Would install to the **{draft.Scope}** scope.");
        sb.AppendLine();

        sb.AppendLine("## Parameters proposed");
        sb.AppendLine();
        if (draft.Parameters.Count == 0)
        {
            sb.AppendLine("_None. This skill would replay exactly as recorded._");
        }
        else
        {
            sb.AppendLine("| Slot | This run used | Why it varies |");
            sb.AppendLine("|---|---|---|");
            foreach (var p in draft.Parameters)
                sb.AppendLine($"| `{{{{{p.Name}}}}}` | `{Cell(p.ObservedValue)}` | {Cell(p.Description)} |");
        }

        sb.AppendLine();
        sb.AppendLine("## Values kept FIXED");
        sb.AppendLine();
        if (draft.UnparameterisedLiterals.Count == 0)
        {
            sb.AppendLine("_Nothing from the run was left hard-coded in the instructions._");
        }
        else
        {
            sb.AppendLine("These appeared in the run and in the instructions, and were NOT parameterised. " +
                          "Each is a decision that they are part of the skill rather than this run's specifics:");
            sb.AppendLine();
            foreach (var literal in draft.UnparameterisedLiterals)
                sb.AppendLine($"- `{literal}`");
        }

        if (draft.Warnings.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Warnings");
            sb.AppendLine();
            foreach (var warning in draft.Warnings)
                sb.AppendLine($"- {warning}");
        }

        sb.AppendLine();
        sb.AppendLine("## Steps recorded");
        sb.AppendLine();
        foreach (var step in draft.Steps)
        {
            var keys = step.ArgumentKeys.Count > 0 ? $" ({string.Join(", ", step.ArgumentKeys)})" : string.Empty;
            sb.AppendLine($"{step.Ordinal}. `{step.ToolName}`{keys}{(step.IsError ? " — errored" : string.Empty)}");
        }

        sb.AppendLine();
        sb.AppendLine("## SKILL.md that would be installed");
        sb.AppendLine();
        sb.AppendLine("```markdown");
        sb.AppendLine(BuildInstallableContent(draft).TrimEnd());
        sb.AppendLine("```");
        sb.AppendLine();
        sb.AppendLine($"**Show this to the operator.** With their agreement, run `confirm` with name " +
                      $"'{draft.Name}' and token `{draft.ConfirmationToken}`. If they want a slot renamed, " +
                      "a value parameterised, or a literal changed, run `propose` again — the token changes " +
                      "with the content, so a stale one cannot install something they did not see.");

        return Ok(sb.ToString());
    }

    // ──────────────────────────── confirm ───────────────────────────────────

    private async Task<AgentToolResult> ConfirmAsync(
        string toolCallId,
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken)
    {
        if (!TryReadName(arguments, out var name, out var nameError))
            return Error(nameError);

        var draft = drafts.TryLoad(name);
        if (draft is null)
            return Error($"No pending draft named '{name}'. Use action 'list' to see pending drafts.");

        var token = ReadString(arguments, "token");
        if (string.IsNullOrWhiteSpace(token))
            return Error(
                $"'token' is required for confirm. Run `review` with name '{name}' first, show the " +
                "operator what it prints, and use the token it issues.");

        if (!string.Equals(token.Trim(), draft.ConfirmationToken, StringComparison.OrdinalIgnoreCase))
            return Error(
                $"Confirmation token does not match this draft (expected `{draft.ConfirmationToken}`). " +
                "The draft has changed since it was reviewed, so confirming would install something " +
                "other than what was shown. Run `review` again.");

        // Reuse the skill_manage create path wholesale rather than writing the file here: it owns
        // scope resolution, the shared-scope gate, the size limit, frontmatter validation, the
        // post-write security scan and its rollback. A second write path would be a second place
        // for those to be forgotten.
        var createArguments = new Dictionary<string, object?>
        {
            ["action"] = "create",
            ["name"] = draft.Name,
            ["scope"] = draft.Scope,
            ["content"] = BuildInstallableContent(draft)
        };

        var result = await writer
            .ExecuteAsync(toolCallId, createArguments, cancellationToken)
            .ConfigureAwait(false);

        var text = string.Join(string.Empty, result.Content.Select(c => c.Value));
        if (text.StartsWith("Error:", StringComparison.Ordinal))
            return Error($"Install refused, draft kept so it can be corrected and confirmed again.\n{text}");

        // Only now is the staging copy redundant.
        drafts.Delete(draft.Name);

        var sb = new StringBuilder();
        sb.AppendLine(text.TrimEnd());
        sb.AppendLine();
        sb.AppendLine($"Draft cleared. Load it with the skills tool: `load` skill '{draft.Name}'" +
                      (draft.Parameters.Count > 0
                          ? $", supplying parameters: {string.Join(", ", draft.Parameters.Select(p => p.Name))}."
                          : "."));
        return Ok(sb.ToString());
    }

    // ──────────────────────────── discard / list ────────────────────────────

    private AgentToolResult Discard(IReadOnlyDictionary<string, object?> arguments)
    {
        if (!TryReadName(arguments, out var name, out var nameError))
            return Error(nameError);

        return drafts.Delete(name)
            ? Ok($"Draft '{name}' discarded. Nothing was installed.")
            : Error($"No pending draft named '{name}'.");
    }

    private AgentToolResult ListDrafts()
    {
        var pending = drafts.List();
        if (pending.Count == 0)
            return Ok("No pending skill drafts.");

        var sb = new StringBuilder();
        sb.AppendLine("## Pending skill drafts");
        sb.AppendLine();
        sb.AppendLine("These are staged, not installed, and are not discoverable as skills.");
        sb.AppendLine();
        foreach (var draft in pending)
        {
            sb.AppendLine($"- **{draft.Name}** ({draft.Scope} scope, {draft.Parameters.Count} parameter(s), " +
                          $"{draft.Steps.Count} step(s)) — proposed {draft.CreatedAt:u}");
        }

        return Ok(sb.ToString());
    }

    // ──────────────────────────── content assembly ──────────────────────────

    /// <summary>
    /// Produces the exact SKILL.md that <c>confirm</c> would install: the proposed body with a
    /// generated <c>parameters:</c> frontmatter block declaring the confirmed slots.
    /// </summary>
    /// <remarks>
    /// Called by both <c>review</c> and <c>confirm</c>, from the same draft, so the text an operator
    /// reads and the file that lands are produced by one function rather than two that agree today.
    /// </remarks>
    internal static string BuildInstallableContent(SkillDraft draft)
    {
        if (draft.Parameters.Count == 0)
            return draft.Content;

        var lines = draft.Content.Split(["\r\n", "\n"], StringSplitOptions.None);
        var firstFence = Array.FindIndex(lines, l => l.Trim() == "---");
        var closingFence = firstFence < 0
            ? -1
            : Array.FindIndex(lines, firstFence + 1, l => l.Trim() == "---");

        var block = new List<string> { "parameters:" };
        foreach (var p in draft.Parameters)
            block.Add($"  {p.Name}: \"{YamlSafe(p.Description)}\"");

        if (closingFence < 0)
        {
            // No usable frontmatter. Rather than silently dropping the declarations, prepend a
            // block; SkillManagerTool's create still rejects the result for a missing description,
            // which is the correct outcome and names the real problem.
            return string.Join("\n", ["---", .. block, "---", string.Empty, draft.Content]);
        }

        var rebuilt = new List<string>();
        rebuilt.AddRange(lines[..closingFence]);
        rebuilt.AddRange(block);
        rebuilt.AddRange(lines[closingFence..]);
        return string.Join("\n", rebuilt);
    }

    /// <summary>
    /// Makes a description safe for the minimal frontmatter parser, which strips matching outer
    /// quotes and does no unescaping. Newlines collapse to spaces and inner double quotes become
    /// single quotes, so the emitted line can never terminate its own quoting.
    /// </summary>
    private static string YamlSafe(string value)
    {
        var flattened = Flatten(value).Replace('"', '\'');
        return Truncate(flattened, MaxParameterDescriptionChars);
    }

    /// <summary>True when the frontmatter block declares <paramref name="key"/> at top level.</summary>
    private static bool HasFrontmatterKey(string content, string key)
    {
        var lines = content.Split(["\r\n", "\n"], StringSplitOptions.None);
        var firstFence = Array.FindIndex(lines, l => l.Trim() == "---");
        if (firstFence < 0)
            return false;

        var closingFence = Array.FindIndex(lines, firstFence + 1, l => l.Trim() == "---");
        if (closingFence < 0)
            return false;

        for (var i = firstFence + 1; i < closingFence; i++)
        {
            var line = lines[i];
            if (line.Length > 0 && (line[0] == ' ' || line[0] == '\t'))
                continue;
            var separator = line.IndexOf(':');
            if (separator > 0 && string.Equals(line[..separator].Trim(), key, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    // ──────────────────────────── plumbing ──────────────────────────────────

    /// <summary>
    /// Reads the live trace, or explains why it cannot.
    /// </summary>
    /// <remarks>
    /// An unwired trace source returns a refusal, never an empty recording. The difference matters:
    /// an empty recording would let a proposal through with nothing to check its observed values
    /// against, which is precisely the "does nothing" fallback this codebase has shipped four times.
    /// </remarks>
    private async Task<(IReadOnlyList<RecordedStep> Steps, AgentToolResult? Failure)> TryReadTraceAsync(
        CancellationToken cancellationToken)
    {
        if (trace is null)
        {
            return ([], Error(
                "No session trace is available to this agent, so a recording cannot be checked " +
                "against what actually ran. Skill recording is unavailable rather than unverified."));
        }

        try
        {
            return (await trace.GetStepsAsync(cancellationToken).ConfigureAwait(false), null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return ([], Error($"Could not read this session's history: {ex.Message}"));
        }
    }

    private static IReadOnlyList<RecordedStepSummary> Summarise(IReadOnlyList<RecordedStep> steps)
        => steps.Select(s => new RecordedStepSummary
        {
            Ordinal = s.Ordinal,
            ToolName = s.ToolName,
            ArgumentKeys = ArgumentKeys(s.ArgumentsJson),
            IsError = s.IsError
        }).ToList();

    private static IReadOnlyList<string> ArgumentKeys(string? argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson))
            return [];

        try
        {
            using var document = JsonDocument.Parse(argumentsJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return [];
            return document.RootElement.EnumerateObject().Select(p => p.Name).ToList();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private bool TryReadName(IReadOnlyDictionary<string, object?> arguments, out string name, out string error)
    {
        name = ReadString(arguments, "name") ?? string.Empty;
        if (string.IsNullOrWhiteSpace(name))
        {
            error = "'name' is required for this action.";
            return false;
        }

        if (!SkillParser.IsValidName(name))
        {
            error = $"Invalid skill name '{name}'. Must be lowercase alphanumeric + hyphens, " +
                    "no leading/trailing/consecutive hyphens, max 64 chars.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    private static bool TryReadParameters(
        IReadOnlyDictionary<string, object?> arguments,
        out IReadOnlyList<DraftParameter> parameters,
        out string error)
    {
        parameters = [];
        error = string.Empty;

        if (!arguments.TryGetValue("parameters", out var raw) || raw is null)
            return true;

        JsonElement element;
        switch (raw)
        {
            case JsonElement je:
                element = je;
                break;
            default:
                try
                {
                    element = JsonSerializer.SerializeToElement(raw);
                }
                catch (NotSupportedException)
                {
                    error = "'parameters' could not be read as a list of parameter objects.";
                    return false;
                }
                break;
        }

        if (element.ValueKind == JsonValueKind.Null || element.ValueKind == JsonValueKind.Undefined)
            return true;

        if (element.ValueKind != JsonValueKind.Array)
        {
            error = "'parameters' must be an array of {name, observedValue, description} objects.";
            return false;
        }

        var read = new List<DraftParameter>();
        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                error = "Each entry in 'parameters' must be an object with name, observedValue and description.";
                return false;
            }

            read.Add(new DraftParameter
            {
                Name = (item.TryGetProperty("name", out var n) ? n.ToString() : string.Empty).Trim(),
                ObservedValue = item.TryGetProperty("observedValue", out var v) ? v.ToString() : string.Empty,
                Description = (item.TryGetProperty("description", out var d) ? d.ToString() : string.Empty).Trim()
            });
        }

        parameters = read;
        return true;
    }

    private static string ScopeName(SkillSource scope) => scope switch
    {
        SkillSource.Agent => "agent",
        SkillSource.Global => "shared",
        _ => "workspace"
    };

    private static string Flatten(string value)
        => value.Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' ');

    /// <summary>Escapes a value for a markdown table cell, where an unescaped pipe splits columns.</summary>
    private static string Cell(string value)
        => Truncate(Flatten(value).Replace("|", "\\|"), 120);

    /// <summary>
    /// Shortens a value for display via <see cref="StringTextExtensions.SafeTruncate"/> (#2883).
    /// Everything cut here is model-supplied tool argument text, which is exactly where a raw slice
    /// splits a surrogate pair.
    /// </summary>
    private static string Truncate(string value, int max)
        => value.SafeTruncate(max, "…") ?? value;

    private static string? ReadString(IReadOnlyDictionary<string, object?> args, string key)
    {
        if (!args.TryGetValue(key, out var value) || value is null)
            return null;

        return value switch
        {
            JsonElement { ValueKind: JsonValueKind.String } el => el.GetString(),
            JsonElement el => el.ToString(),
            _ => value.ToString()
        };
    }

    private static AgentToolResult Ok(string text)
        => new([new AgentToolContent(AgentToolContentType.Text, text)]);

    private static AgentToolResult Error(string message)
        => new([new AgentToolContent(AgentToolContentType.Text, $"Error: {message}")]);
}
