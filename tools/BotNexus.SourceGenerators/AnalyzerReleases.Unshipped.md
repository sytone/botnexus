; Unshipped analyzer release
; https://github.com/dotnet/roslyn-analyzers/blob/main/src/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|------
BNFF001 | FeatureFlags | Error | feature-flags.json could not be parsed; no inventory was generated.
BNFF002 | FeatureFlags | Error | The inventory parsed but code emission failed.
BNFF003 | FeatureFlags | Warning | A live flag is past the configured age threshold; retire it or set ignoreFlagAge.
BNTS001 | ToolSchema | Error | A tool parameter declares a JSON type that is not a JSON Schema type keyword.
BNTS002 | ToolSchema | Error | A tool parameter name is declared more than once.
BNTS003 | ToolSchema | Error | A tool parameter alias targets a key that is not declared before it.
