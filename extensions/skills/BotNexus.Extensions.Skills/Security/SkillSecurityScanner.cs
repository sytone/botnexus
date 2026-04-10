using System.Text.RegularExpressions;
using System.IO.Abstractions;

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
            Pattern: new Regex(@"process\.env", RegexOptions.Compiled),
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
                matchEvidence = source.Length > EvidenceMaxLength ? source[..EvidenceMaxLength] : source;
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

    internal static bool IsScannable(string filePath)
        => ScannableExtensions.Contains(Path.GetExtension(filePath));

    private static string TruncateEvidence(string evidence)
        => evidence.Length <= EvidenceMaxLength
            ? evidence
            : evidence[..EvidenceMaxLength] + "…";

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
