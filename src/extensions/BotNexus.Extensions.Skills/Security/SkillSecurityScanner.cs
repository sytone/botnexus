using System.Text.RegularExpressions;
using System.IO.Abstractions;

using BotNexus.Gateway.Abstractions.Text;

namespace BotNexus.Extensions.Skills.Security;

/// <summary>Severity levels for security scan findings.</summary>
public enum ScanSeverity
{
    Info,
    Warn,
    Critical,
}

/// <summary>A single security finding from the skill scanner.</summary>
public sealed record ScanFinding(
    string RuleId,
    ScanSeverity Severity,
    string File,
    int Line,
    string Message,
    string Evidence);

/// <summary>Aggregate summary returned by a directory scan.</summary>
public sealed record ScanSummary(
    int ScannedFiles,
    int Critical,
    int Warn,
    int Info,
    IReadOnlyList<ScanFinding> Findings);

/// <summary>
/// Scans skill source files for dangerous patterns.
/// Ported from OpenClaw's skill-scanner.ts and adapted for .NET.
/// </summary>
public static class SkillSecurityScanner
{
    // -----------------------------------------------------------------------
    // Scannable extensions
    // -----------------------------------------------------------------------

    private static readonly HashSet<string> ScannableExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".js", ".ts", ".mjs", ".cjs", ".mts", ".cts", ".jsx", ".tsx",
        ".cs", ".ps1", ".py", ".sh", ".bash",
    };

    private const int DefaultMaxFiles = 500;
    private const int DefaultMaxFileBytes = 1_048_576; // 1 MB
    private const int EvidenceMaxLength = 120;

    // -----------------------------------------------------------------------
    // Rule types
    // -----------------------------------------------------------------------

    private sealed record LineRule(
        string RuleId,
        ScanSeverity Severity,
        string Message,
        Regex Pattern,
        Regex? RequiresContext = null);

    private sealed record SourceRule(
        string RuleId,
        ScanSeverity Severity,
        string Message,
        Regex Pattern,
        Regex? RequiresContext = null);

    // -----------------------------------------------------------------------
    // Line rules (per-line pattern matching, one finding per rule per file)
    // -----------------------------------------------------------------------

    private static readonly LineRule[] LineRules =
    [
        new(
            RuleId: "dangerous-exec",
            Severity: ScanSeverity.Critical,
            Message: "Shell command execution detected (child_process)",
            Pattern: new Regex(@"\b(exec|execSync|spawn|spawnSync|execFile|execFileSync)\s*\(", RegexOptions.Compiled),
            RequiresContext: new Regex(@"child_process", RegexOptions.Compiled)),

        new(
            RuleId: "dynamic-code-execution",
            Severity: ScanSeverity.Critical,
            Message: "Dynamic code execution detected",
            Pattern: new Regex(@"\beval\s*\(|new\s+Function\s*\(", RegexOptions.Compiled)),

        new(
            RuleId: "crypto-mining",
            Severity: ScanSeverity.Critical,
            Message: "Possible crypto-mining reference detected",
            Pattern: new Regex(@"stratum\+tcp|stratum\+ssl|coinhive|cryptonight|xmrig", RegexOptions.Compiled | RegexOptions.IgnoreCase)),

        new(
            RuleId: "suspicious-network",
            Severity: ScanSeverity.Warn,
            Message: "WebSocket connection to non-standard port",
            Pattern: new Regex(@"new\s+WebSocket\s*\(\s*[""']wss?://[^""']*:(\d+)", RegexOptions.Compiled)),
    ];

    private static readonly HashSet<int> StandardPorts = [80, 443, 8080, 8443, 3000];

    // -----------------------------------------------------------------------
    // Source rules (full-source pattern matching)
    // -----------------------------------------------------------------------

    private static readonly SourceRule[] SourceRules =
    [
        new(
            RuleId: "potential-exfiltration",
            Severity: ScanSeverity.Warn,
            Message: "File read combined with network send — possible data exfiltration",
            Pattern: new Regex(@"readFileSync|readFile", RegexOptions.Compiled),
            RequiresContext: new Regex(@"\bfetch\b|\bpost\b|http\.request", RegexOptions.Compiled | RegexOptions.IgnoreCase)),

        new(
            RuleId: "obfuscated-code",
            Severity: ScanSeverity.Warn,
            Message: "Hex-encoded string sequence detected (possible obfuscation)",
            Pattern: new Regex(@"(\\x[0-9a-fA-F]{2}){6,}", RegexOptions.Compiled)),

        new(
            RuleId: "obfuscated-code",
            Severity: ScanSeverity.Warn,
            Message: "Large base64 payload with decode call detected (possible obfuscation)",
            Pattern: new Regex(@"(?:atob|Buffer\.from)\s*\(\s*[""'][A-Za-z0-9+/=]{200,}[""']", RegexOptions.Compiled)),

        new(
            RuleId: "env-harvesting",
            Severity: ScanSeverity.Critical,
            Message: "Environment variable access combined with network send — possible credential harvesting",
            // #2809 clause 4: widened from the dotted-only `process\.env` to also cover the
            // computed form `process["env"]`. Unlike dangerous-exec this needs no binding pass:
            // `process` is a global, not an imported identifier, so there is no import site to
            // alias. A local rebinding (`const p = process; p.env`) remains out of scope — it is a
            // general alias-tracking problem for arbitrary globals, not a module-import question,
            // and solving it lexically would produce false positives on any variable named `p`.
            Pattern: new Regex(@"process\s*(?:\.\s*env\b|\[\s*[""']env[""']\s*\])", RegexOptions.Compiled),
            RequiresContext: new Regex(@"\bfetch\b|\bpost\b|http\.request", RegexOptions.Compiled | RegexOptions.IgnoreCase)),
    ];

    // -----------------------------------------------------------------------
    // Public API
    // -----------------------------------------------------------------------

    /// <summary>
    /// Scans all scannable files in <paramref name="dirPath"/> and returns an
    /// aggregate summary with severity counts.
    /// </summary>
    public static ScanSummary ScanDirectory(
        string dirPath,
        int maxFiles = DefaultMaxFiles,
        int maxFileBytes = DefaultMaxFileBytes,
        IFileSystem? fileSystem = null)
    {
        var fs = fileSystem ?? new FileSystem();
        maxFiles = Math.Max(1, maxFiles);
        maxFileBytes = Math.Max(1, maxFileBytes);

        var files = CollectScannableFiles(dirPath, maxFiles, fs);
        var allFindings = new List<ScanFinding>();
        int scannedFiles = 0;
        int critical = 0, warn = 0, info = 0;

        foreach (var file in files)
        {
            var fileInfo = fs.FileInfo.New(file);
            if (!fileInfo.Exists || fileInfo.Length > maxFileBytes)
                continue;

            string source;
            try
            {
                source = fs.File.ReadAllText(file);
            }
            catch
            {
                continue;
            }

            scannedFiles++;
            var findings = ScanSource(source, file);
            foreach (var f in findings)
            {
                allFindings.Add(f);
                switch (f.Severity)
                {
                    case ScanSeverity.Critical: critical++; break;
                    case ScanSeverity.Warn: warn++; break;
                    default: info++; break;
                }
            }
        }

        return new ScanSummary(scannedFiles, critical, warn, info, allFindings);
    }

    /// <summary>
    /// Scans a single source string and returns all findings.
    /// </summary>
    public static IReadOnlyList<ScanFinding> ScanSource(string source, string filePath)
    {
        var findings = new List<ScanFinding>();
        var lines = source.Split('\n');
        var matchedLineRules = new HashSet<string>(StringComparer.Ordinal);

        // #2809: bindings are resolved once per source; null when nothing is imported
        // from child_process, in which case dangerous-exec behaves exactly as before.
        var childProcessBindings = BuildChildProcessBindingPattern(source);

        // --- Line rules ---
        foreach (var rule in LineRules)
        {
            if (matchedLineRules.Contains(rule.RuleId))
                continue;

            if (rule.RequiresContext is not null && !rule.RequiresContext.IsMatch(source))
                continue;

            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                var match = rule.Pattern.Match(line);

                // Binding-aware fallback: an aliased or computed-member invocation of a
                // child_process export is the same danger spelled differently.
                if (!match.Success && rule.RuleId == "dangerous-exec" && childProcessBindings is not null)
                    match = childProcessBindings.Match(line);

                if (!match.Success)
                    continue;

                // Special handling: suspicious-network checks port number
                if (rule.RuleId == "suspicious-network" && match.Groups.Count > 1)
                {
                    if (int.TryParse(match.Groups[1].Value, out var port) && StandardPorts.Contains(port))
                        continue;
                }

                findings.Add(new ScanFinding(
                    rule.RuleId,
                    rule.Severity,
                    filePath,
                    i + 1,
                    rule.Message,
                    TruncateEvidence(line.Trim())));

                matchedLineRules.Add(rule.RuleId);
                break; // one finding per line-rule per file
            }
        }

        // --- Source rules ---
        var matchedSourceRules = new HashSet<string>(StringComparer.Ordinal);

        foreach (var rule in SourceRules)
        {
            var ruleKey = $"{rule.RuleId}::{rule.Message}";
            if (matchedSourceRules.Contains(ruleKey))
                continue;

            if (!rule.Pattern.IsMatch(source))
                continue;

            if (rule.RequiresContext is not null && !rule.RequiresContext.IsMatch(source))
                continue;

            // Find the first matching line for evidence + line number
            int matchLine = 0;
            string matchEvidence = string.Empty;
            for (int i = 0; i < lines.Length; i++)
            {
                if (rule.Pattern.IsMatch(lines[i]))
                {
                    matchLine = i + 1;
                    matchEvidence = lines[i].Trim();
                    break;
                }
            }

            if (matchLine == 0)
            {
                matchLine = 1;
                matchEvidence = GraphemeSafeTruncation.Truncate(source, EvidenceMaxLength)!;
            }

            findings.Add(new ScanFinding(
                rule.RuleId,
                rule.Severity,
                filePath,
                matchLine,
                rule.Message,
                TruncateEvidence(matchEvidence)));

            matchedSourceRules.Add(ruleKey);
        }

        return findings;
    }

    // -----------------------------------------------------------------------
    // Internal helpers
    // -----------------------------------------------------------------------

    // -----------------------------------------------------------------------
    // child_process binding pass (#2809)
    // -----------------------------------------------------------------------

    /// <summary>
    /// Export names of <c>child_process</c> that actually execute a command. Kept
    /// identical to the direct-form <c>dangerous-exec</c> pattern so both paths
    /// agree on what "dangerous" means.
    /// </summary>
    private static readonly string[] ChildProcessExecExports =
        ["exec", "execSync", "spawn", "spawnSync", "execFile", "execFileSync"];

    /// <summary>Matches a <c>require("child_process")</c> / <c>require("node:child_process")</c> call.</summary>
    private const string RequireChildProcess = @"require\s*\(\s*[""'](?:node:)?child_process[""']\s*\)";

    /// <summary><c>const NAME = require("child_process")</c> — NAME holds the whole module.</summary>
    private static readonly Regex RequireNamespaceBinding = new(
        @"(?:const|let|var)\s+([A-Za-z_$][\w$]*)\s*=\s*" + RequireChildProcess, RegexOptions.Compiled);

    /// <summary><c>const { exec: run, spawn } = require("child_process")</c> — captures the destructure body.</summary>
    private static readonly Regex RequireDestructureBinding = new(
        @"(?:const|let|var)\s*\{([^}]*)\}\s*=\s*" + RequireChildProcess, RegexOptions.Compiled);

    /// <summary><c>import ... from "node:child_process"</c> — captures the whole clause before <c>from</c>.</summary>
    private static readonly Regex ImportClause = new(
        @"import\s+([^;]*?)\s+from\s*[""'](?:node:)?child_process[""']", RegexOptions.Compiled);

    /// <summary>A single identifier, optionally renamed (<c>spawn as launch</c> / <c>exec: run</c>).</summary>
    private static readonly Regex BindingSpecifier = new(
        @"^\s*([A-Za-z_$][\w$]*)\s*(?:(?:as|:)\s*([A-Za-z_$][\w$]*)\s*)?$", RegexOptions.Compiled);

    /// <summary>
    /// Records which local identifiers are bound to <c>child_process</c> exports, so the
    /// scanner can answer a binding question instead of matching call-site spelling. Any
    /// rename between import and call defeated the previous regex-only rule (#2809).
    /// </summary>
    /// <returns>
    /// A regex matching an invocation through any recorded binding, or <c>null</c> when the
    /// source binds nothing from <c>child_process</c>.
    /// </returns>
    private static Regex? BuildChildProcessBindingPattern(string source)
    {
        // Identifiers bound directly to an exec-capable export: `run(...)` executes.
        var directBindings = new HashSet<string>(StringComparer.Ordinal);
        // Identifiers holding the module itself: only `ns.exec(...)` / `ns["exec"](...)` executes.
        var namespaceBindings = new HashSet<string>(StringComparer.Ordinal);

        foreach (Match m in RequireNamespaceBinding.Matches(source))
            namespaceBindings.Add(m.Groups[1].Value);

        foreach (Match m in RequireDestructureBinding.Matches(source))
            AddDestructuredBindings(m.Groups[1].Value, directBindings);

        foreach (Match m in ImportClause.Matches(source))
        {
            var clause = m.Groups[1].Value;

            // Named/aliased specifiers: import { spawn as launch } from ...
            var braceStart = clause.IndexOf('{');
            var braceEnd = clause.LastIndexOf('}');
            if (braceStart >= 0 && braceEnd > braceStart)
            {
                AddDestructuredBindings(clause[(braceStart + 1)..braceEnd], directBindings);
                clause = clause[..braceStart];
            }

            // Whatever remains is a default and/or namespace binding: `cp`, `* as cp`, `cp,`.
            foreach (var part in clause.Split(','))
            {
                var token = part.Trim().TrimEnd(',').Trim();
                if (token.StartsWith('*'))
                {
                    var idx = token.LastIndexOf(' ');
                    token = idx >= 0 ? token[(idx + 1)..] : string.Empty;
                }

                if (IsIdentifier(token))
                    namespaceBindings.Add(token);
            }
        }

        var alternatives = new List<string>();

        foreach (var name in directBindings)
            alternatives.Add($@"\b{Regex.Escape(name)}\s*\(");

        if (namespaceBindings.Count > 0)
        {
            var exports = string.Join("|", ChildProcessExecExports);
            foreach (var name in namespaceBindings)
            {
                var ns = Regex.Escape(name);
                // Dotted and computed member access are the same call through different syntax.
                alternatives.Add($@"\b{ns}\s*\.\s*(?:{exports})\s*\(");
                alternatives.Add($@"\b{ns}\s*\[\s*[""'](?:{exports})[""']\s*\]\s*\(");
            }
        }

        return alternatives.Count == 0
            ? null
            : new Regex(string.Join("|", alternatives), RegexOptions.Compiled);
    }

    private static void AddDestructuredBindings(string specifierList, HashSet<string> directBindings)
    {
        foreach (var raw in specifierList.Split(','))
        {
            var specifier = BindingSpecifier.Match(raw);
            if (!specifier.Success)
                continue;

            var exported = specifier.Groups[1].Value;
            if (!ChildProcessExecExports.Contains(exported, StringComparer.Ordinal))
                continue;

            // The local name is the alias when renamed, otherwise the export name itself.
            var local = specifier.Groups[2].Success ? specifier.Groups[2].Value : exported;
            directBindings.Add(local);
        }
    }

    private static bool IsIdentifier(string token)
        => token.Length > 0
            && (char.IsLetter(token[0]) || token[0] == '_' || token[0] == '$')
            && token.All(c => char.IsLetterOrDigit(c) || c == '_' || c == '$');

    internal static bool IsScannable(string filePath)
        => ScannableExtensions.Contains(Path.GetExtension(filePath));

    private static string TruncateEvidence(string evidence)
        // #2924: shared boundary policy. Evidence is arbitrary skill-file text and is rendered to
        // the user, so a cut inside a cluster shows as a mangled glyph in the scan report.
        => GraphemeSafeTruncation.Truncate(evidence, EvidenceMaxLength, "…")!;

    private static List<string> CollectScannableFiles(string dirPath, int maxFiles, IFileSystem fileSystem)
    {
        var files = new List<string>();
        if (!fileSystem.Directory.Exists(dirPath))
            return files;

        var stack = new Stack<string>();
        stack.Push(dirPath);

        while (stack.Count > 0 && files.Count < maxFiles)
        {
            var currentDir = stack.Pop();
            try
            {
                foreach (var entry in fileSystem.Directory.EnumerateFileSystemEntries(currentDir))
                {
                    if (files.Count >= maxFiles)
                        break;

                    var name = Path.GetFileName(entry);

                    // Skip hidden dirs and node_modules
                    if (name.StartsWith('.') || string.Equals(name, "node_modules", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (fileSystem.Directory.Exists(entry))
                    {
                        stack.Push(entry);
                    }
                    else if (fileSystem.File.Exists(entry) && IsScannable(entry))
                    {
                        files.Add(entry);
                    }
                }
            }
            catch
            {
                // Permission denied or similar — skip this directory
            }
        }

        return files;
    }
}
