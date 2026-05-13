# Decision: CLI locations database redaction

## Context
`botnexus locations list` was printing `location.Path` directly. For database locations this value can be a full connection string containing credentials.

## Decision
Use `ResolveSafeDisplayPath` in `LocationsCommand` so database locations with configured secrets always show `(redacted)`, while filesystem/API/mcp/remote-node locations still show their configured path or endpoint.

## Consequences
- CLI output now matches the API/UI secret-redaction contract for database location values.
- Location listing remains useful for non-secret location types.
- Regression tests assert both that secrets are absent and that non-database values remain visible.
