# Decision: Database location secret redaction

## Context
Gateway location APIs were echoing database connection strings through `PathOrEndpoint`, which can expose credentials in browser UI and API clients.

## Decision
Treat database location values as secrets in all location responses (`list/get/create/update`). Return a fixed placeholder (`[connection string configured]`) and `hasConfiguredSecret=true` instead of the raw connection string.

## Consequences
- UI can safely display configuration status without receiving secret material.
- Create/update still accept and persist connection strings.
- Database updates with blank `value` preserve the existing secret to avoid accidental credential loss during metadata-only edits.
