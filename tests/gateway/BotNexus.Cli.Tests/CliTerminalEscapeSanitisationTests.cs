using BotNexus.Cli.Commands;
using BotNexus.Cron;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Configuration;

namespace BotNexus.Cli.Tests;

/// <summary>
/// Issue #2722: the CLI rendered stored cron/agent strings through <c>Markup.Escape</c> only,
/// which neutralises Spectre's <c>[]</c> grammar but forwards ANSI/OSC control sequences to
/// the terminal verbatim. These tests pin the sanitisation at each enumerated render site.
/// </summary>
public sealed class CliTerminalEscapeSanitisationTests
{
    private const char Esc = '\u001b';

    /// <summary>OSC-52 clipboard-write payload, matching the upstream OpenClaw fixture.</summary>
    private const string Osc52 = "\u001b]52;c;Zm9yZ2Vk\u0007";

    /// <summary>CSI sequence: erase display + reposition cursor, used to forge screen content.</summary>
    private const string Csi = "\u001b[2J\u001b[1;1H";

    [Fact]
    public void SafeDisplay_removes_escape_bytes_and_escapes_markup()
    {
        var rendered = CliText.SafeDisplay($"{Osc52}forged: yes [red]x[/]");

        rendered.ShouldNotContain(Esc);
        rendered.ShouldNotContain("]52;c;");
        rendered.ShouldContain("forged: yes");
        rendered.ShouldContain("[[red]]");
    }

    [Fact]
    public void SafeDisplay_maps_null_to_empty()
        => CliText.SafeDisplay(null).ShouldBe(string.Empty);

    // ──────────────────────────────────────────────────────────────────────
    //  #3208 AC3 - `botnexus cron list` renders an agent-writable job name
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The exact hostile payload named by #3208 AC3: an SGR CSI sequence, an OSC title-set
    /// terminated by BEL, and a bare carriage return that repositions the cursor to overwrite
    /// output already printed above it.
    /// </summary>
    private const string HostileJobName =
        "\u001b[31mred\u001b]0;pwned\u0007title\rOVERWRITE";

    /// <summary>Code points AC3 forbids from surviving into human-facing output.</summary>
    private static bool IsForbiddenControl(char c) =>
        c is '\u0007' or '\u0008' or '\u001b' || (c >= '\u007f' && c <= '\u009f');

    private static CronJob HostileJob() => new()
    {
        Id = JobId.From("job-3208"),
        Name = HostileJobName,
        Schedule = "*/5 * * * *",
        ActionType = "agent-prompt",
        AgentId = AgentId.From("farnsworth"),
        Enabled = true,
    };

    /// <summary>
    /// AC3: a cron job name stored in <c>cron.sqlite</c> - fully agent-writable through
    /// <c>CronTool</c> - must not carry a single forbidden control code point into the row
    /// that <c>botnexus cron list</c> hands to Spectre.
    /// </summary>
    [Fact]
    public void Cron_list_row_strips_every_forbidden_control_code_point()
    {
        var row = CronCommands.BuildListRow(HostileJob());

        var survivors = string.Concat(row).Where(IsForbiddenControl).ToArray();

        survivors.ShouldBeEmpty(
            "A stored cron job name reached the operator's terminal with control code points " +
            "intact: " + string.Join(", ", survivors.Select(c => $"U+{(int)c:X4}")) +
            ". Markup.Escape alone does not remove them - route the value through " +
            "CliText.SafeDisplay. See #3208 AC3.");
    }

    /// <summary>
    /// The stripping must not swallow the legitimate text either side of the payload, which is
    /// what distinguishes sanitisation from blanking the field.
    /// </summary>
    [Fact]
    public void Cron_list_row_preserves_the_printable_text_around_the_payload()
    {
        var name = CronCommands.BuildListRow(HostileJob())[1];

        name.ShouldContain("red");
        name.ShouldContain("title");
        name.ShouldContain("OVERWRITE");
        name.ShouldNotContain("[31m");
        name.ShouldNotContain("0;pwned");
    }

    /// <summary>
    /// AC4 counterpart for <c>cron list</c>: a plain job renders byte-identically to the
    /// pre-fix output, and the enabled glyph stays CLI-authored markup rather than being
    /// escaped into literal text.
    /// </summary>
    [Fact]
    public void Cron_list_row_renders_plain_values_byte_identically()
    {
        var row = CronCommands.BuildListRow(new CronJob
        {
            Id = JobId.From("job-plain"),
            Name = "nightly-groom",
            Schedule = "0 3 * * *",
            ActionType = "command",
            AgentId = AgentId.From("farnsworth"),
            Enabled = false,
        });

        row.Length.ShouldBe(6);
        row[0].ShouldBe("job-plain");
        row[1].ShouldBe("nightly-groom");
        row[2].ShouldBe("0 3 * * *");
        row[3].ShouldBe("farnsworth");
        row[4].ShouldBe("[red]\u2717[/]");
        row[5].ShouldBe("command");
    }

    /// <summary>
    /// The same forbidden-code-point rule applied to <see cref="CliText.SafeDisplay"/> itself,
    /// so the guarantee is pinned at the helper and not only at one caller. Lone C0 controls
    /// are the interesting half: they never form an escape sequence, so a sequence-only
    /// stripper passes them straight through.
    /// </summary>
    [Theory]
    [InlineData("\u0007bell")]
    [InlineData("back\u0008space")]
    [InlineData("carriage\rreturn")]
    [InlineData("del\u007fete")]
    [InlineData("c1\u009bcsi")]
    [InlineData("\u001b[31mred\u001b]0;pwned\u0007title\rOVERWRITE")]
    public void SafeDisplay_leaves_no_forbidden_control_code_point(string hostile)
        => CliText.SafeDisplay(hostile).Any(IsForbiddenControl).ShouldBeFalse(
            "CliText.SafeDisplay is the single definition of 'safe to hand to Spectre'; a " +
            "forbidden control code point surviving it defeats every call site at once. See #3208.");

    /// <summary>Tab and newline are layout, not terminal-state manipulation - they survive.</summary>
    [Fact]
    public void SafeDisplay_preserves_tab_and_newline()
        => CliText.SafeDisplay("a\tb\nc").ShouldBe("a\tb\nc");

    [Fact]
    public void Cron_run_row_strips_osc52_from_job_name()
    {
        var entry = new DebugCronCommand.CronRunEntry
        {
            RunId = "r1",
            JobId = "job-1",
            JobName = Osc52 + "nightly",
            StartedAt = "2026-08-03T10:00:00+00:00",
            Status = "completed",
            DurationMs = 12
        };

        var row = DebugCronCommand.FormatRunRow(entry);

        string.Concat(row).ShouldNotContain(Esc);
        row[0].ShouldNotContain("]52;c;");
        row[0].ShouldContain("nightly");
    }

    [Fact]
    public void Cron_run_row_strips_csi_from_error_text()
    {
        var entry = new DebugCronCommand.CronRunEntry
        {
            RunId = "r2",
            JobId = "job-2",
            JobName = "job",
            StartedAt = "2026-08-03T10:00:00+00:00",
            Status = "failed",
            Error = Csi + "boom"
        };

        var row = DebugCronCommand.FormatRunRow(entry);

        string.Concat(row).ShouldNotContain(Esc);
        row[4].ShouldContain("boom");
        row[4].ShouldNotContain("[2J");
    }

    /// <summary>
    /// AC6: sanitisation must not change legitimate output. A plain job name renders exactly
    /// as it did before the fix - a fix that also mangles normal output is a regression.
    /// </summary>
    [Fact]
    public void Cron_run_row_renders_plain_values_byte_identically()
    {
        var entry = new DebugCronCommand.CronRunEntry
        {
            RunId = "r3",
            JobId = "job-3",
            JobName = "daily-groom",
            StartedAt = "2026-08-03T10:20:30+00:00",
            Status = "completed",
            DurationMs = 42
        };

        var row = DebugCronCommand.FormatRunRow(entry);

        row.Length.ShouldBe(5);
        row[0].ShouldBe("daily-groom");
        row[1].ShouldBe("2026-08-03 10:20:30");
        row[2].ShouldBe("42ms");
        row[3].ShouldBe("[green]completed[/]");
        row[4].ShouldBe(string.Empty);
    }

    [Fact]
    public void Agent_show_rows_strip_csi_from_description()
    {
        var agent = new AgentDefinitionConfig
        {
            DisplayName = "Farnsworth",
            Description = Csi + "good news everyone",
            Provider = "copilot",
            Model = "claude",
            Enabled = true
        };

        var rows = AgentCommands.BuildShowRows("farnsworth", agent);

        var description = rows.Single(r => r.Field == "description").Value;
        description.ShouldNotContain(Esc);
        description.ShouldNotContain("[2J");
        description.ShouldContain("good news everyone");
        string.Concat(rows.Select(r => r.Value)).ShouldNotContain(Esc);
    }

    /// <summary>
    /// AC6 for <c>agent show</c>: plain field values are rendered unchanged.
    /// </summary>
    [Fact]
    public void Agent_show_rows_render_plain_values_byte_identically()
    {
        var agent = new AgentDefinitionConfig
        {
            DisplayName = "Farnsworth",
            Description = "Professor",
            Provider = "copilot",
            Model = "claude-opus",
            Enabled = true
        };

        var rows = AgentCommands.BuildShowRows("farnsworth", agent);

        rows.Single(r => r.Field == "id").Value.ShouldBe("farnsworth");
        rows.Single(r => r.Field == "displayName").Value.ShouldBe("Farnsworth");
        rows.Single(r => r.Field == "description").Value.ShouldBe("Professor");
        rows.Single(r => r.Field == "provider").Value.ShouldBe("copilot");
        rows.Single(r => r.Field == "model").Value.ShouldBe("claude-opus");
        rows.Single(r => r.Field == "enabled").Value.ShouldBe("[green]Yes[/]");
    }

    /// <summary>
    /// AC4: the lookup-miss message echoes caller-supplied input straight to the terminal.
    /// </summary>
    [Fact]
    public void Agent_not_found_message_strips_escapes_from_caller_input()
    {
        var message = AgentCommands.AgentNotFoundMessage(Osc52 + "ghost");

        message.ShouldNotContain(Esc);
        message.ShouldNotContain("]52;c;");
        message.ShouldContain("ghost");
        message.ShouldEndWith("was not found.");
    }

    [Fact]
    public void Agent_not_found_message_renders_plain_id_byte_identically()
        => AgentCommands.AgentNotFoundMessage("missing-agent")
            .ShouldBe("[red]Error:[/] Agent [green]missing-agent[/] was not found.");
}
