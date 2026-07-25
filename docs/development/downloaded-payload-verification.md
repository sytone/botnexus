---
owner: shared
author: BotNexus Team
ai-policy: collaborative
---

# Downloaded Payload Verification

**Rule:** any BotNexus install, update, bootstrap, or remediation path that fetches
executable content over the network MUST verify that content against a known SHA-256
checksum (or a signature) **before** anything executes, extracts, or installs it, and MUST
fail closed on a non-2xx response or a truncated download.

"Executable content" is anything the machine will subsequently act on rather than merely
display: shell scripts, PowerShell scripts, installers, archives that get unpacked onto a
load path, prebuilt binaries, and plugin/extension bundles.

## Why this rule exists

A download-then-execute step is only as trustworthy as its weakest failure mode. Three
things all look identical to naive code — "a file appeared on disk":

1. A proxy or CDN returned an HTML error page with a 200-shaped body.
2. The connection dropped mid-body, leaving a syntactically valid but truncated script.
3. An attacker (or a compromised mirror) substituted a different artifact entirely.

Only (3) is classic supply-chain tampering, but (1) and (2) are far more common and can be
just as destructive when the partial file is handed to an interpreter. Checksum
verification plus a fail-closed transfer collapses all three into one safe outcome:
nothing runs.

This is currently a **standing guard-rail, not a live fix**. BotNexus has no
download-then-execute installer path today — `bn update` pulls source with git and builds
locally, and the `Invoke-WebRequest` calls in `scripts/botnexus-watchdog.ps1` and
`scripts/recover-gateway.ps1` are health probes whose responses are inspected, never
executed. The rule is documented and the helper is in place so that the first path which
*does* fetch an artifact inherits verification instead of inheriting the exposure.

## The rule in practice

Any new or modified code path that fetches executable content must satisfy all of:

| Requirement | Meaning |
| --- | --- |
| **Verify before execute** | Compute SHA-256 over the fetched bytes and compare to an expected digest sourced independently of the payload itself. Never derive the expected digest from the same response. |
| **Fail closed on non-2xx** | A non-success status is a hard stop. Do not fall back to a cached copy, a mirror, or "try running it anyway". |
| **Fail closed on truncation** | If the server advertises `Content-Length`, the received byte count must match it exactly. |
| **Reject empty payloads** | A zero-byte body is never valid executable content. |
| **Never stage into the live path** | Download to a temporary file and only move it into its final location after verification passes. A rejected download must leave nothing behind. |
| **Require a well-formed digest** | A missing, blank, or malformed expected digest is itself a failure. Do not silently skip verification when no digest is configured. |

The last row is the one most often got wrong: an optional-checksum design degrades to no
checksum the moment a config value is missing. Verification is mandatory or it is theatre.

## Using the helper (.NET)

`VerifiedPayloadDownloader` in `src/gateway/BotNexus.Cli/Commands/` implements all of the
above. Route CLI-side fetches of executable content through it rather than hand-rolling an
`HttpClient` read:

```csharp
var result = await VerifiedPayloadDownloader.DownloadAndVerifyAsync(
    httpClient,
    new Uri("https://example.com/botnexus-update.zip"),
    expectedSha256,          // from a manifest fetched/pinned separately
    destinationPath,
    cancellationToken);

if (!result.Succeeded)
{
    AnsiConsole.MarkupLineInterpolated($"[red]Update aborted:[/] {result.FailureReason}");
    return 1;                // fail closed - do not proceed to install
}

// Only now is result.FilePath safe to unpack or execute.
```

`VerifiedPayloadDownloader.ComputeSha256Async` is available for callers that need to record
or compare a digest for a file already on disk.

Behaviour is covered by
`tests/gateway/BotNexus.Cli.Tests/Commands/VerifiedPayloadDownloaderTests.cs`, which
exercises the happy path plus checksum mismatch, non-2xx, truncated body, empty body,
malformed expected digest, and transport failure.

## Using the rule (PowerShell)

Scripts under `scripts/` have no verified-download helper because no script currently
downloads executable content. If one needs to, the same requirements apply — use
`Invoke-WebRequest` with `-OutFile` into a temp path, check `StatusCode`, compare
`Get-FileHash -Algorithm SHA256` against the expected digest, and only then move the file
into place and run it. Do not pipe a response body straight into `Invoke-Expression`.

## Reviewer checklist

When reviewing a PR that adds a network fetch, ask:

- Does the fetched content ever get executed, unpacked, or loaded? If yes, is there a digest check?
- Where does the expected digest come from, and could an attacker who controls the payload also control the digest?
- What happens on a 404, a 500, or a dropped connection — does the code stop, or does it carry on with whatever bytes it has?
- If verification fails, is the partial file deleted?
