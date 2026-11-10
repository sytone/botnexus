---
name: analyze-agent-session-rca
description: Run a blameless, evidence-based root cause analysis of an AI agent session without redoing the task or taking actions.
parameters:
  issue:
    description: Issue or problem the RCA is focusing on from this conversation
    required: true
---
You are running a blameless root cause analysis (RCA) of an AI agent session. Explain what happened, why it happened and what change to the system would stop it happening again. Do not redo the original task or take any actions. This is analysis only.

{{issue}}

## Inputs
- Conversation record: [attached transcript: user messages, agent responses, every tool call with its arguments, result or error, and timestamps]
- Original request: [what I asked the agent to do]
- Expected outcome: [what good looked like, e.g., correct result in ~3 turns / ~2 min]
- Issues I detected: [be specific, with turn numbers if possible, e.g., "took 18 min for a 2-min lookup"; "turns 6–11 retried the same failing search"; "meeting times shown in UTC instead of PST"]
- Agent setup, if available: [system prompt, skills, tools, model]

## Ground rules
1. Evidence first. Cite the turn, timestamp or tool call behind every claim. Label each claim FACT (seen in the record) or INFERENCE (your interpretation, with High/Med/Low confidence). If the record is missing something you need, say so. Never fill the gap with a guess.
2. Symptoms are not root causes. "The agent made a mistake", "the model hallucinated" and "the tool failed" describe what happened, not why. Keep asking why until you reach something in the system that can be changed: instructions, tool design or error messages, missing or misleading context, planning logic, environment (auth, throttling, latency), or ambiguity in my request.
3. Test my diagnosis. Confirm, refine or refute each issue I raised. List any other issues you find separately.
4. No hindsight bias. Judge each decision by what the agent knew at that moment, not what we know now.

## Analysis
1. Timeline: a table of every turn with timestamp, elapsed time, action or tool call, result, and a tag (Productive / Redundant / Failed / Recovery / Waiting on user). Mark where it first went off track and where it recovered.
2. Time and turn accounting: compare the actual run to an ideal path (the shortest correct sequence given what the agent knew then). Split wall-clock time into tool latency, agent processing, retries and waiting on me. Count the turns and minutes lost to each issue.
3. Problem statement: for each issue, expected vs. actual with numbers, in 1–2 sentences.
4. 5 Whys: for each issue, a why-chain with evidence at every step. Where a why has more than one valid answer, branch and follow each one. Use fewer or more than five if the evidence calls for it. Don't pad.
5. Root cause: separate the trigger (what started it), the contributing factors (what made it worse) and the root cause. Tag each root cause: Instructions | Tooling | Context | Planning | Environment | User input. Then test each one:
   - Counterfactual: if only this were fixed, would the issue have been prevented?
   - Coverage: does it explain every symptom? If not, what is left?
   - Recurrence: which other tasks or sessions would hit this?
6. Detection: why didn't the agent notice and correct itself sooner? What signal did it miss or not have?

## Fixes
For each root cause, propose fixes at three layers:
- Prevent: stops it happening (change to instructions or a skill, tool behavior, better default)
- Detect: catches it early (e.g., "after 2 identical failures, change approach")
- Recover: what the agent should do once it happens
For each fix, give the exact change (with proposed wording for any prompt or skill edit), where it lives, expected savings in turns or minutes, effort (S/M/L), and risks or side effects. Rank the top 3 by impact vs. effort.

## Output
1. TL;DR (≤5 bullets): what happened, root cause(s), turns and time lost, top fix
2. Timeline table
3. Time and turn accounting (actual vs. ideal)
4. Per issue: problem statement → 5 Whys → root cause and tests → why it wasn't caught sooner
5. Fixes table, ranked
6. What went well (≤3 bullets), so the fixes don't break it
7. Evidence gaps and open questions
