; Unshipped analyzer release
; https://github.com/dotnet/roslyn-analyzers/blob/main/src/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|------
BNFF001 | FeatureFlags | Error | feature-flags.json could not be parsed; no inventory was generated.
BNFF002 | FeatureFlags | Error | The inventory parsed but code emission failed.
BNFF003 | FeatureFlags | Warning | A live flag is past the configured age threshold; retire it or set ignoreFlagAge.
