# Post-Sprint 3 Consistency Review

**Author:** Nibbler (Consistency Reviewer)
**Date:** 2026-04-03
**Commit:** e7ff6d8

## Summary

Sprint 3 delivered 7 features (AD-9 through AD-17) with 4 new training docs and multiple API changes. Consistency review found **22 discrepancies** across 7 files — all fixed.

## Pattern Observed

New training docs (06-09) were written based on planned APIs rather than final implementations. Every Sprint 3 training doc had at least one wrong API signature. The most critical gap was 07-thinking-levels.md claiming `--thinking` didn't exist in the CLI — when it was the primary deliverable of AD-10 and AD-17.

## Discrepancies Fixed (by severity)

### HIGH (7)
1. **07-thinking-levels.md**: CLI section said "--thinking flag does not exist" — rewrote with actual --thinking, /thinking, and session metadata
2. **09-tool-development.md**: IAgentTool.ExecuteAsync missing `toolCallId` parameter across interface definition and all 4 examples
3. **09-tool-development.md**: GetPromptGuidelines return type wrong (`string?` vs `IReadOnlyList<string>`)
4. **06-context-file-discovery.md**: Truncation algorithm was binary search in docs, char-by-char iteration in code
5. **08-building-custom-coding-agent.md**: SystemPromptBuilder.Build() called with non-existent parameters
6. **03-coding-agent.md**: Missing ListDirectoryTool from tool table, code example, and count
7. **CodingAgent/README.md**: Tool count wrong (6→7), missing --thinking in CLI help

### MEDIUM (10)
8. **08-building-custom-coding-agent.md**: Missing `using BotNexus.CodingAgent.Utils` namespace import
9. **08-building-custom-coding-agent.md**: SystemPromptBuilder used as static method but it's instance-based
10. **08-building-custom-coding-agent.md**: Cross-ref linked to `08-tool-development.md` instead of `09-tool-development.md`
11. **09-tool-development.md**: EchoTool example ExecuteAsync wrong signature
12. **09-tool-development.md**: CalculatorTool example ExecuteAsync wrong signature
13. **09-tool-development.md**: DatabaseQueryTool example ExecuteAsync wrong signature + wrong callback name
14. **09-tool-development.md**: Error handling example ExecuteAsync wrong signature
15. **09-tool-development.md**: Built-in tools list missing ListDirectoryTool
16. **05-glossary.md**: Duplicate ThinkingLevel entry (lines 432 and 531)
17. **05-glossary.md**: Cross-reference header missing modules 06-09

### LOW (5)
18. **CodingAgent/README.md**: Opening line missing grep and list_directory from tool list
19. **CodingAgent/README.md**: Missing list_directory tool section
20. **CodingAgent/README.md**: ReadTool params showed `range` instead of `start_line`/`end_line`
21. **08-building-custom-coding-agent.md**: DemoTool GetPromptGuidelines returns wrong type
22. **09-tool-development.md**: DatabaseQueryTool GetPromptGuidelines returns wrong type

## Recommendation

Training docs authored during a sprint should be reviewed against final code BEFORE the sprint is considered complete. The doc-writing agent and the code-writing agent need a handoff checkpoint to catch signature mismatches.

## Validation

- ✅ Build: `dotnet build BotNexus.slnx` — 0 errors, 0 warnings
- ✅ Tests: 415/415 pass across 7 test projects
