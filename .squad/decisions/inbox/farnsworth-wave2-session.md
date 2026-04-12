# Farnsworth Wave 2 Session Model / Value Object Adoption

**Date:** 2026-04-12  
**Owner:** Farnsworth  
**Status:** Implemented

## Context
Wave 2 required moving gateway session contracts toward domain primitives and richer session metadata while preserving compatibility with existing persisted data and tests.

## Decision
1. Rename gateway status terminal enum from `Closed` to `Sealed`.
2. Extend `GatewaySession` with `SessionType`, `IsInteractive`, and `Participants`.
3. Add domain participant model (`SessionParticipant`, `ParticipantType`) in `BotNexus.Domain`.
4. Adopt domain value objects in gateway-facing contracts:
   - `ChannelKey` for channel identity
   - `MessageRole` for transcript roles

## Compatibility
- SQLite migration updates legacy persisted statuses from `closed` to `Sealed`.
- SQLite loader maps legacy `closed` values to `Sealed` as a fallback.
- Session stores persist `session_type` and `participants_json` where applicable.

## Validation
- `dotnet build BotNexus.slnx --nologo --tl:off`
- `dotnet test tests\BotNexus.Gateway.Tests\BotNexus.Gateway.Tests.csproj --nologo --tl:off --no-build`
- `dotnet test tests\BotNexus.Domain.Tests\BotNexus.Domain.Tests.csproj --nologo --tl:off`
