# Hermes Decision — update git pull regression tests (2026-05-07)

## Decision

Treat `botnexus update` pull-stage behavior as a strict quality gate:

1. If git pull fails (non-zero), update must return that non-zero code and must **not** stop/start the gateway.
2. If git pull is canceled, update must return non-zero and must **not** stop/start the gateway.
3. If git pull is slow, update must wait for pull completion before stop is attempted.

## Why

The reported user failure (`git pull error: A task was canceled`) is in the pull stage, so restart logic should never execute on that path.  
These tests prevent regression where partial update flow manipulates gateway state after pull-stage errors/cancellation.

## Test Path

- `tests\BotNexus.Cli.Tests\Commands\UpdateCommandTests.cs`