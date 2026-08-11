<#
.SYNOPSIS
    docs-lint: mechanical checks over docs/** that catch the classes of documentation
    defect a human grooming read reliably misses (issue #2865).

.DESCRIPTION
    Grooming is a person reading prose. It catches tone and structure and it glides
    straight past a wrong port number. On 2026-08-07 one writer reading the docset cold
    found twelve defects (DOC-FINDINGS.md); several were two statements contradicting
    each other on the SAME page, and one sent every new user to the wrong port on the
    highest-traffic page in the docset. None needed running code to detect.

    This script implements the mechanical half of grooming. Each rule cites, in its own
    comment, the specific defect from that batch it exists to prevent (issue #2865 AC7).

      Rule 1  literal-drift          A localhost port or dotted config key that appears
                                     inside a fenced code block in docs/** and NOWHERE in
                                     src/** is stale by definition.
                                     Catches: the 18790 port; BotNexus.Cron.Jobs.
      Rule 2  intra-page-contradiction
                                     For a registry of high-value facts, a single page may
                                     state at most one distinct value. A page that
                                     disagrees with itself is a defect, not untidiness.
                                     Catches: tickIntervalSeconds 60-vs-10.
      Rule 3  legacy-marker          A "legacy"/"deprecated"/"non-functional"/"do not copy"
                                     marker buried inside a how-to section, rather than
                                     banner-first, tells a reader to copy the sample and
                                     only afterwards that it does not work.
                                     Catches: the LlmProviderBase sample in
                                     extension-development.md.

    Rule 4 of issue #2865 (docs-vs-source PR trigger) is not a content rule and is
    implemented where it belongs: .github/workflows/docs-lint.yml plus the checklist item
    in .github/pull_request_template.md.

.PARAMETER RepoRoot
    Repository root to lint. Defaults to the repo containing this script.

.PARAMETER DocsPath
    Documentation root, relative to RepoRoot. Defaults to 'docs'.

.PARAMETER SourcePath
    Source root that literals are corroborated against, relative to RepoRoot.
    Defaults to 'src'.

.PARAMETER FactsPath
    Rule 2 fact registry. Defaults to scripts/repo/docs-lint-facts.json.

.PARAMETER AllowListPath
    Rule 1 allow-list of literals that are legitimately docs-only (external services,
    illustrative examples). Defaults to scripts/repo/docs-lint-allow.json.

.PARAMETER Rule
    Restrict the run to named rules. Defaults to all three.

.PARAMETER AsJson
    Emit the structured result to stdout instead of a human report. stdout stays pure
    JSON so a caller can pipe it (skill-wrapper stdout purity, issue #2420/#2761).

.OUTPUTS
    Exit code 0 when clean, 1 when any rule reports a finding, 2 on a usage error.

.EXAMPLE
    pwsh -NoProfile -File scripts/repo/docs-lint.ps1

.EXAMPLE
    pwsh -NoProfile -File scripts/repo/docs-lint.ps1 -AsJson | ConvertFrom-Json
#>
[CmdletBinding()]
param(
    [string]$RepoRoot,
    [string]$DocsPath = 'docs',
    [string]$SourcePath = 'src',
    [string]$FactsPath,
    [string]$AllowListPath,
    [ValidateNotNullOrEmpty()]
    [string[]]$Rule = @('literal-drift', 'intra-page-contradiction', 'legacy-marker'),
    [switch]$AsJson
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not $RepoRoot) {
    $RepoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
}
$RepoRoot = (Resolve-Path -LiteralPath $RepoRoot).Path

# `pwsh -File script.ps1 -Rule a,b,c` hands the parameter ONE literal string - the -File host
# does no PowerShell parsing of arguments, so an array is never constructed and a ValidateSet
# on the raw value rejects the very list it permits. Split here instead, then validate
# ourselves. Discovered by a remote gate failure; do not "simplify" this back to ValidateSet.
$Rule = @($Rule | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ })
$knownRules = @('literal-drift', 'intra-page-contradiction', 'legacy-marker')
$unknownRules = @($Rule | Where-Object { $knownRules -notcontains $_ })
if ($unknownRules.Count -gt 0) {
    [Console]::Error.WriteLine(
        "Unknown rule(s): $($unknownRules -join ', '). Known rules: $($knownRules -join ', ').")
    exit 2
}
if (-not $FactsPath) { $FactsPath = Join-Path $PSScriptRoot 'docs-lint-facts.json' }
if (-not $AllowListPath) { $AllowListPath = Join-Path $PSScriptRoot 'docs-lint-allow.json' }

$docsRoot = Join-Path $RepoRoot $DocsPath
$sourceRoot = Join-Path $RepoRoot $SourcePath

if (-not (Test-Path -LiteralPath $docsRoot)) {
    [Console]::Error.WriteLine("docs root not found: $docsRoot")
    exit 2
}

# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------

# Fenced code blocks are where a reader COPIES from, so they are the only place a
# literal is load-bearing enough to gate on. Prose may legitimately say "the old
# port was 18790"; a fence saying it is an instruction.
function Get-FencedBlock {
    param([Parameter(Mandatory)][AllowEmptyString()][string]$Content)

    $lines = $Content -replace "`r`n", "`n" -split "`n"
    $blocks = [System.Collections.Generic.List[object]]::new()
    $inFence = $false
    $startLine = 0
    $buffer = [System.Collections.Generic.List[string]]::new()

    for ($i = 0; $i -lt $lines.Count; $i++) {
        if ($lines[$i] -match '^\s*(```|~~~)') {
            if ($inFence) {
                $blocks.Add([pscustomobject]@{ StartLine = $startLine; Text = ($buffer -join "`n") })
                $buffer.Clear()
                $inFence = $false
            }
            else {
                $inFence = $true
                $startLine = $i + 2
            }
            continue
        }
        if ($inFence) { $buffer.Add($lines[$i]) }
    }
    # An unterminated fence still contains copyable instructions; lint it rather than
    # silently discarding it, which would be a free way to evade the gate.
    if ($inFence -and $buffer.Count -gt 0) {
        $blocks.Add([pscustomobject]@{ StartLine = $startLine; Text = ($buffer -join "`n") })
    }
    return $blocks
}

function Get-LineNumber {
    param([string]$Content, [int]$Index)
    if ($Index -le 0) { return 1 }
    $prefix = $Content.Substring(0, [Math]::Min($Index, $Content.Length))
    return @($prefix.ToCharArray() | Where-Object { $_ -eq "`n" }).Count + 1
}

$findings = [System.Collections.Generic.List[object]]::new()
function Add-Finding {
    param([string]$RuleName, [string]$File, [int]$Line, [string]$Message)
    $findings.Add([pscustomobject]@{
            rule    = $RuleName
            file    = $File
            line    = $Line
            message = $Message
        })
}

$docFiles = @(Get-ChildItem -LiteralPath $docsRoot -Recurse -Filter *.md -File |
    Where-Object { $_.FullName -notmatch '[\\/](\.vitepress|node_modules|archive)[\\/]' })

# A line that SETS a value is a demonstration, not an assertion about the system.
# `botnexus config set gateway.listenUrl http://localhost:8080` teaches the reader how to
# change the port; it does not claim the gateway listens on 8080, so it can neither be
# "drift" (rule 1) nor contradict the documented default (rule 2). Likewise an
# `export BotNexus__Gateway__ListenUrl=...` sample and a `--endpoint` pointing at a
# third-party server the reader supplies.
#
# This is deliberately narrow. The defect that motivated rule 1 - "Open the portal:
# http://localhost:18790" - is a BROWSE instruction with no assignment, so it is still
# caught; DocsLintScriptTests pins exactly that case.
$demonstrationLine = [regex]::new('config\s+set\b|^\s*(?:export|set|\$env:)\s|--endpoint\b|SET\s+[A-Z_]+=',
    [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)

function Test-DemonstrationLine {
    param([string]$Content, [int]$Index)
    $before = $Content.LastIndexOf("`n", [Math]::Max(0, [Math]::Min($Index, $Content.Length - 1)))
    $after = $Content.IndexOf("`n", [Math]::Min($Index, [Math]::Max(0, $Content.Length - 1)))
    if ($before -lt 0) { $before = -1 }
    if ($after -lt 0) { $after = $Content.Length }
    $line = $Content.Substring($before + 1, $after - $before - 1)
    return $demonstrationLine.IsMatch($line)
}

# Anti-vacuity: a sweep that inspects nothing is trivially green. If the enumeration
# breaks, fail loudly rather than certifying a docset nobody read.
$minimumDocFiles = 20
if ($docFiles.Count -lt $minimumDocFiles) {
    # Deliberately NOT Write-Error: $ErrorActionPreference is 'Stop', which makes Write-Error
    # terminating, so the script would die with exit 1 - indistinguishable from an ordinary
    # findings result - and the caller could never tell a broken sweep from a dirty docset.
    [Console]::Error.WriteLine(
        ("docs-lint scanned only {0} markdown files under '{1}' (minimum {2}). " -f
            $docFiles.Count, $docsRoot, $minimumDocFiles) +
        'The enumeration is broken; a green result here would be vacuous.')
    exit 2
}

# ---------------------------------------------------------------------------
# Rule 1: literal drift
#
# MOTIVATING DEFECT (#2865): getting-started-release.md - the single highest-traffic
# page in the docset - told every new user to open http://localhost:18790, while
# GatewayBindAddress.LoopbackListenUrl declares "http://localhost:5005". The port 18790
# appears nowhere in src/. A literal that lives in docs and in no source file is not a
# fact about the system; it is a fact about a system that no longer exists.
# Also catches the dotted config key BotNexus.Cron.Jobs, which the binder stopped using.
# ---------------------------------------------------------------------------
function Invoke-LiteralDriftRule {
    $allow = @{ ports = @(); keys = @() }
    if (Test-Path -LiteralPath $AllowListPath) {
        $raw = Get-Content -LiteralPath $AllowListPath -Raw | ConvertFrom-Json
        if ($raw.PSObject.Properties.Name -contains 'ports') { $allow.ports = @($raw.ports | ForEach-Object { [string]$_ }) }
        if ($raw.PSObject.Properties.Name -contains 'keys') { $allow.keys = @($raw.keys | ForEach-Object { [string]$_ }) }
    }

    # Corroboration corpus: every text file under src/. A literal is "known to source"
    # when it appears anywhere in it. Deliberately permissive - the rule must flag a
    # literal source has NEVER heard of, not police where source mentions it.
    $sourceText = New-Object System.Text.StringBuilder
    if (Test-Path -LiteralPath $sourceRoot) {
        $srcFiles = Get-ChildItem -LiteralPath $sourceRoot -Recurse -File -Include *.cs, *.json, *.csproj, *.props, *.ps1, *.yml, *.yaml, *.razor, *.ts, *.js |
            Where-Object { $_.FullName -notmatch '[\\/](bin|obj|node_modules)[\\/]' }
        foreach ($f in $srcFiles) {
            $null = $sourceText.Append((Get-Content -LiteralPath $f -Raw -ErrorAction SilentlyContinue))
            $null = $sourceText.Append("`n")
        }
    }
    $corpus = $sourceText.ToString()

    # A plain loopback-port match. Scoping this to "gateway/portal" wording would be a
    # mistake: the motivating defect - a bare `http://localhost:18790` alone in a fence -
    # carries no such wording, and the mutation proof caught exactly that over-narrowing.
    # False positives are handled where they actually arise instead: a port belonging to
    # another process (Ollama 11434, Jaeger 16686, an OTLP collector 4317) is corroborated
    # by src/, and a `config set ... :8080` demonstration is excluded by line shape below.
    $portPattern = [regex]'(?:localhost|127\.0\.0\.1)\s*:\s*(?<port>\d{2,5})\b'
    # A dotted key in CONFIGURATION KEY POSITION - quoted and followed by a colon, or given
    # as a colon-delimited env/binder path. Deliberately not a bare dotted identifier: that
    # also matches .NET namespaces and test project names (BotNexus.Integration.Tests),
    # which are code, not config, and are absent from src/ for entirely legitimate reasons.
    $keyPattern = [regex]'(?<quote>"|'')(?<key>BotNexus(?:\.[A-Z][A-Za-z0-9]+)+)\k<quote>\s*:|\b(?<key>BotNexus(?::[A-Z][A-Za-z0-9]+)+)\b'

    foreach ($file in $docFiles) {
        $content = Get-Content -LiteralPath $file.FullName -Raw
        $relative = $file.FullName.Substring($RepoRoot.Length + 1).Replace('\', '/')

        foreach ($block in (Get-FencedBlock -Content $content)) {
            foreach ($m in $portPattern.Matches($block.Text)) {
                $port = $m.Groups['port'].Value
                if ($allow.ports -contains $port) { continue }
                if (Test-DemonstrationLine -Content $block.Text -Index $m.Index) { continue }
                if ($corpus -match [regex]::Escape(":$port")) { continue }
                Add-Finding 'literal-drift' $relative ($block.StartLine + (Get-LineNumber -Content $block.Text -Index $m.Index) - 1) (
                    "Port $port is instructed in a code fence but appears nowhere in $SourcePath/. " +
                    'A literal that lives only in docs is stale by definition (issue #2865, the 18790 defect). ' +
                    "Correct it against source, or add it to $(Split-Path -Leaf $AllowListPath) if it is genuinely external.")
            }
            foreach ($m in $keyPattern.Matches($block.Text)) {
                $key = $m.Groups['key'].Value
                if ($allow.keys -contains $key) { continue }
                if (Test-DemonstrationLine -Content $block.Text -Index $m.Index) { continue }
                if ($corpus -match [regex]::Escape($key)) { continue }
                Add-Finding 'literal-drift' $relative ($block.StartLine + (Get-LineNumber -Content $block.Text -Index $m.Index) - 1) (
                    "Config key '$key' is instructed in a code fence but appears nowhere in $SourcePath/. " +
                    'The binder no longer reads it (issue #2865, the BotNexus.Cron.Jobs defect).')
            }
        }
    }
}

# ---------------------------------------------------------------------------
# Rule 2: intra-page contradiction
#
# MOTIVATING DEFECT (#2865): cron-and-scheduling.md stated tickIntervalSeconds as 60 in
# a table and 10 in a diagram, on the same page. A reader cannot tell which half is
# true, so both halves - and the reader's trust in every other page - are worthless.
# This is a hard failure, not a warning.
# ---------------------------------------------------------------------------
function Invoke-ContradictionRule {
    if (-not (Test-Path -LiteralPath $FactsPath)) {
        [Console]::Error.WriteLine("fact registry not found: $FactsPath")
        exit 2
    }
    $registry = Get-Content -LiteralPath $FactsPath -Raw | ConvertFrom-Json
    $facts = @($registry.facts)
    if ($facts.Count -eq 0) {
        # An empty registry makes the rule silently pass on every page.
        [Console]::Error.WriteLine('fact registry contains no facts; rule 2 would be vacuous.')
        exit 2
    }

    foreach ($file in $docFiles) {
        $content = Get-Content -LiteralPath $file.FullName -Raw
        $relative = $file.FullName.Substring($RepoRoot.Length + 1).Replace('\', '/')

        foreach ($fact in $facts) {
            $options = [System.Text.RegularExpressions.RegexOptions]::None
            $ignoreCase = $true
            if ($fact.PSObject.Properties.Name -contains 'ignoreCase') { $ignoreCase = [bool]$fact.ignoreCase }
            if ($ignoreCase) { $options = [System.Text.RegularExpressions.RegexOptions]::IgnoreCase }
            $normalize = 'lower'
            if ($fact.PSObject.Properties.Name -contains 'normalize') { $normalize = [string]$fact.normalize }

            $rx = [regex]::new($fact.pattern, $options)
            $observed = [ordered]@{}
            foreach ($m in $rx.Matches($content)) {
                $value = $m.Groups['value'].Value
                if ([string]::IsNullOrWhiteSpace($value)) { continue }
                if (Test-DemonstrationLine -Content $content -Index $m.Index) { continue }
                $keyed = if ($normalize -eq 'none') { $value.Trim() } else { $value.Trim().ToLowerInvariant() }
                if (-not $observed.Contains($keyed)) {
                    $observed[$keyed] = Get-LineNumber -Content $content -Index $m.Index
                }
            }

            if ($observed.Count -gt 1) {
                $detail = ($observed.Keys | ForEach-Object { "'$_' (line $($observed[$_]))" }) -join ', '
                Add-Finding 'intra-page-contradiction' $relative ([int]($observed.Values | Select-Object -First 1)) (
                    "Page states $($observed.Count) different values for fact '$($fact.id)': $detail. " +
                    'A page that disagrees with itself is a defect, not untidiness (issue #2865). ' +
                    "Motivating defect: $($fact.defect)")
            }
        }
    }
}

# ---------------------------------------------------------------------------
# Rule 3: legacy marker inside a how-to
#
# MOTIVATING DEFECT (#2865): extension-development.md presented an LlmProviderBase
# sample under a how-to heading and only disclosed - well below the code fence - that
# the base class is legacy and non-functional. A reader copies the fence first and
# reads the caveat afterwards, if at all. A disqualifying marker must be a banner at
# the TOP of the section, above any fence, or the section must not read as a how-to.
# ---------------------------------------------------------------------------
function Invoke-LegacyMarkerRule {
    $markerPattern = [regex]::new('legacy|non-functional|nonfunctional|deprecated|do not copy|no longer (?:works|functional|supported)',
        [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
    # A section reads as a how-to when its heading tells the reader to do something.
    $howToHeading = [regex]::new('^#{2,6}\s+(?:how to\b|creating\b|create\b|adding\b|add\b|implementing\b|implement\b|building\b|build\b|writing\b|write\b|using\b|use\b|configuring\b|configure\b|setting up\b|getting started\b|step \d)',
        [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)

    foreach ($file in $docFiles) {
        $content = (Get-Content -LiteralPath $file.FullName -Raw) -replace "`r`n", "`n"
        $relative = $file.FullName.Substring($RepoRoot.Length + 1).Replace('\', '/')
        $lines = $content -split "`n"

        $sectionStart = -1
        $sectionIsHowTo = $false
        $sections = [System.Collections.Generic.List[object]]::new()
        for ($i = 0; $i -lt $lines.Count; $i++) {
            if ($lines[$i] -match '^#{1,6}\s+') {
                if ($sectionStart -ge 0) {
                    $sections.Add([pscustomobject]@{ Start = $sectionStart; End = $i - 1; IsHowTo = $sectionIsHowTo })
                }
                $sectionStart = $i
                $sectionIsHowTo = $howToHeading.IsMatch($lines[$i])
            }
        }
        if ($sectionStart -ge 0) {
            $sections.Add([pscustomobject]@{ Start = $sectionStart; End = $lines.Count - 1; IsHowTo = $sectionIsHowTo })
        }

        foreach ($section in $sections) {
            if (-not $section.IsHowTo) { continue }

            $bodyLines = $lines[($section.Start + 1)..$section.End]
            if ($null -eq $bodyLines) { continue }

            $fenceOffset = -1
            $markerOffset = -1
            for ($j = 0; $j -lt @($bodyLines).Count; $j++) {
                $line = @($bodyLines)[$j]
                if ($fenceOffset -lt 0 -and $line -match '^\s*(```|~~~)') { $fenceOffset = $j }
                if ($markerOffset -lt 0 -and $markerPattern.IsMatch($line)) { $markerOffset = $j }
            }

            # No fence to copy, or no disqualifying marker at all: nothing to report.
            if ($fenceOffset -lt 0 -or $markerOffset -lt 0) { continue }
            # Banner form: the marker precedes the sample. This is the ACCEPTED shape -
            # the reader is warned before they can copy anything.
            if ($markerOffset -lt $fenceOffset) { continue }

            Add-Finding 'legacy-marker' $relative ($section.Start + 1 + $markerOffset + 1) (
                "How-to section '$($lines[$section.Start].Trim())' presents a code sample at line " +
                "$($section.Start + 1 + $fenceOffset + 1) and only discloses it is legacy/deprecated/non-functional " +
                "afterwards, at line $($section.Start + 1 + $markerOffset + 1). " +
                'A reader copies the fence and reads the caveat later, if ever (issue #2865, the LlmProviderBase defect). ' +
                'Move the marker into a banner above the sample, or drop the how-to framing.')
        }
    }
}

# ---------------------------------------------------------------------------

if ($Rule -contains 'literal-drift') { Invoke-LiteralDriftRule }
if ($Rule -contains 'intra-page-contradiction') { Invoke-ContradictionRule }
if ($Rule -contains 'legacy-marker') { Invoke-LegacyMarkerRule }

$sorted = @($findings | Sort-Object rule, file, line)
$result = [pscustomobject]@{
    scannedFiles = $docFiles.Count
    rules        = @($Rule)
    findingCount = $sorted.Count
    findings     = $sorted
}

if ($AsJson) {
    $result | ConvertTo-Json -Depth 6
}
else {
    Write-Host "docs-lint: scanned $($docFiles.Count) markdown files under '$DocsPath' with rules: $($Rule -join ', ')"
    if ($sorted.Count -eq 0) {
        Write-Host 'docs-lint: PASS - no findings.'
    }
    else {
        foreach ($f in $sorted) {
            Write-Host ""
            Write-Host ("[{0}] {1}:{2}" -f $f.rule, $f.file, $f.line)
            Write-Host ("    {0}" -f $f.message)
        }
        Write-Host ""
        Write-Host "docs-lint: FAIL - $($sorted.Count) finding(s)."
    }
}

exit ($(if ($sorted.Count -gt 0) { 1 } else { 0 }))
