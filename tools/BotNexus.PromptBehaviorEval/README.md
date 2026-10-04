# Prompt behavior evaluator

This opt-in console harness measures how a real model follows BotNexus prompt guidance. It uses the production `AgentLoopRunner`, `OpenAICompatProvider`, message converter, prompt sections, and tool executor; it does not synthesize model output or implement another agent loop.

## Cost and reliability

Every invocation makes real provider requests and can incur token charges. The deterministic fixture requires exactly 20 observable operations: initial todo, inspection, revised todo, change application, fourteen distinct validation/checkpoint operations, completion verification, and final todo. This deliberately exceeds the GPT narration threshold, so a GPT run cannot pass without at least one non-empty progress message splitting the chain. Tool chains normally require several provider turns. Provider throttling, outages, model updates, and sampling make results flaky and non-deterministic; repeat all four mutation cells per rung multiple times and compare distributions rather than treating one run as a unit test. The paid model evaluation is intentionally opt-in and absent from ordinary CI. The evaluator project is included in the root traversal so remote CORE compiles it, and CORE discovers and runs its deterministic contract-test project without making provider requests.

## Configuration and invocation

Create a JSON file outside source control (the API key itself stays in an environment variable):

```json
{
  "endpoint": "https://your-openai-compatible-endpoint/v1",
  "provider": "explicit-provider-label",
  "api": "openai-compat",
  "model": "exact-model-id",
  "apiKeyEnvironmentVariable": "BEHAVIOR_EVAL_API_KEY",
  "rung": "Gpt",
  "mutation": "None",
  "maxTokens": 2048,
  "timeoutSeconds": 120,
  "outputPath": "artifacts/behavior-eval/gpt-current.json"
}
```

Then run on request:

```powershell
$env:BEHAVIOR_EVAL_API_KEY = '<secret>'
dotnet run --project tools/BotNexus.PromptBehaviorEval -- --config path/to/eval.json
```

Run the `Default`, `Claude`, and `Gpt` rungs separately with a deliberately supplied provider/model/endpoint. Set `api` to `openai-compat` for Chat Completions-compatible endpoints or `openai-responses` for Responses-compatible endpoints. A rung selects prompt guidance only; it does not pretend that the serving model belongs to that family. This permits controlled cross-model and native-family comparisons without requiring an architectural decision about a universal credential router.

Mutation values are `None`, `FormerTodoInstruction`, `FormerResultWaitInstruction`, or the JSON string `FormerTodoInstruction, FormerResultWaitInstruction`. The former strings are exact copies from `37b566531^`. Each mutation replaces the corresponding current instruction, rather than adding a contradictory duplicate beside it. Run all four mutation cells per rung repeatedly to isolate each instruction, their interaction, and model variance.

After collecting at least two independent result files for each of the three rungs and four mutation cells, build an offline matrix report without making provider requests:

```powershell
$results = @(Get-ChildItem artifacts/behavior-eval -Filter '*.json' -File | ForEach-Object FullName)
dotnet run --project tools/BotNexus.PromptBehaviorEval -- --report @results
```

Supply explicit paths if your shell cannot enumerate the files. The report prints twelve cells, their sample/pass/fail counts, maximum silent tool-call spacing, and verdicts: `missing`, `insufficient-samples`, `consistently-green`, `consistently-red`, `mixed`, or `no-red-observed`. It rejects mixed serving provider/model identities and duplicate cell/timestamps. A missing or insufficient cell never counts as green or red. Nonzero exit means the matrix lacks repeated evidence, the current cells are not consistently green, or a former instruction has not been consistently red; it does not prove the code is broken. This offline command reads result JSON, including prompts and task text, so keep result files in a private scratch directory and review any report before publishing it. Running the report does not replace the paid repeated measurements needed to establish a model-specific result. Guidance placement for other model families is separately owned by #4579.

The output JSON records provider/model/rung/mutation, timestamps, ordered tool calls, accepted todo snapshots, every accepted or rejected todo transition, rejected operation count, added-item-after-inspection evidence, distinct done-item count, assistant-message count, maximum tool calls between non-empty assistant messages, token counts, final text, ordered raw observations, and the exact system prompt. Tool state enforces the exact operation order; an out-of-order call is recorded as rejected, returns a failure result, and does not advance the fixture. The `acceptance` object reports the expected operation order, named checks, an explicit `passed` verdict, and `failedChecks`. Common checks require the exact operation order, an expected sequence long enough to distinguish a one-item stop from full completion, the discovered item after inspection, at least two distinct done items, no rejected todo transitions, and no rejected operations. The GPT rung additionally requires maximum silent spacing of 10 tool calls; Default and Claude still report spacing but do not inherit that model-specific criterion. Keep result files out of source control when prompts or task text are sensitive.
