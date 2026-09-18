# Produce and consume release history metadata

This page is for BotNexus release maintainers and developers who consume release history. It defines the versioned JSON contract produced by the CLI release workflow.

## Producer contract

`.github/workflows/release-cli.yml` is the only producer. For each release it:

1. captures the immutable source commit selected when the workflow starts;
2. generates the canonical Markdown notes with `git-cliff`;
3. passes those notes to `scripts/repo/New-ReleaseHistoryManifest.ps1`;
4. commits `docs/public/releases/release-history.json` with the version bump, changelog, and release page.

The producer does not scrape rendered documentation or reconstruct entries from a later `main` branch. The `commit` field identifies the source revision used to generate that release. The release commit itself is created afterward and therefore has a different commit ID.

The script fails the release before publication when it finds:

- an invalid or mismatched version, tag, or 40-character commit ID;
- an unsupported manifest schema;
- duplicate release versions;
- invalid semantic versions;
- a release that cannot be sorted newest first by semantic version;
- an empty change category or change summary;
- a non-HTTPS URL or a URL outside the public BotNexus GitHub repository and documentation site.

## Consumer contract

The public artifact URL is:

`https://sytone.github.io/botnexus/releases/release-history.json`

Consumers must require `schemaVersion` to equal `1.0.0`. The root `releases` array is ordered newest first. Each release contains:

| Field | Meaning |
| --- | --- |
| `version` | Semantic version without the `v` prefix. |
| `tag` | Matching Git tag, such as `v0.46.0`. |
| `commit` | Full 40-character source commit selected by the release workflow. |
| `releasedAt` | UTC release date in `YYYY-MM-DD` form. |
| `releaseUrl` | Public GitHub release URL. |
| `documentationUrl` | Public release documentation URL. |
| `categories` | Categories preserved from the canonical Markdown notes. |
| `categories[].changes` | Change summaries in source order. |
| `categories[].changes[].documentationUrls` | Public capability documentation links found in the change summary. |

Consumers must treat the version and commit as one identity. They must not substitute the current branch tip, infer a missing commit from a tag without verification, reorder versions lexicographically, scrape the rendered release page, or accept links outside the validated public hosts.

## Missing or invalid artifact

A missing, unreachable, unsupported, or invalid manifest means release history is unavailable. Consumers must show an empty or error state and retain any last independently validated data. They must not synthesize released history from `CHANGELOG.md`, rendered HTML, GitHub issue text, or unreleased commits.

This fail-closed behavior keeps the portal and update clients aligned with the release workflow instead of presenting development activity as a published release.
