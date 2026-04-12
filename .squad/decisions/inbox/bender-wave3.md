# Bender Wave 3 Decisions (2026-04-12)

## Context
Wave 3 required fixing sub-agent identity reuse and decoupling cron from channel adapters while preserving runtime behavior.

## Decisions
1. **Sub-agent identity now includes archetype + unique suffix**
   - Child agent IDs are generated as {parentAgentId}::subagent::{archetype}::{uniqueId}.
   - This keeps parent lineage visible while ensuring child identity is distinct.

2. **Archetype is modeled as a Domain smart enum**
   - Added SubAgentArchetype with values: esearcher, coder, planner, eviewer, writer, general.
   - Spawn request and runtime sub-agent info now carry archetype metadata; default is general.

3. **Cron moved to internal trigger abstraction**
   - Added TriggerType smart enum and IInternalTrigger contract.
   - Replaced cron channel adapter usage with CronTrigger to create internal cron sessions directly.
   - AgentPromptAction now resolves a cron internal trigger and records returned session IDs.

## Rationale
- Distinct child IDs remove parent/child identity collisions in logs and lifecycle handling.
- Archetype-aware IDs support future policy/routing differentiation without changing session discoverability patterns.
- Internal trigger abstraction aligns cron with domain intent (trigger, not external channel) and removes no-op channel adapter coupling.

## Validation
- dotnet test tests\BotNexus.Cron.Tests\BotNexus.Cron.Tests.csproj ✅
- dotnet test tests\BotNexus.Domain.Tests\BotNexus.Domain.Tests.csproj ✅
- Full dotnet build BotNexus.slnx currently fails due pre-existing concurrent typed-ID migration errors outside these changes.
