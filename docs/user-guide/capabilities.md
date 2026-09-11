# Use BotNexus features

BotNexus can do more than answer a question. This guide helps you choose a feature, prepare for it and check its result. You do not need to understand the implementation to use a configured feature.

A feature is not necessarily a separate extension. Canvas and a conversation checklist are platform tools; web search is supplied by an extension. Your agent's actual tools and permissions decide what it can do.

## Before you start

Use a working agent and a test conversation. Ask the agent which tools are available for the task. Do not assume every installation includes the same tools, accounts or data.

The requests below are **examples to try**, not promises of success or records of completed tests. Review actual tool results. Ordinary model responses and scheduled agent work can consume provider quota.

## Choose a feature

| What you want | Feature | What to check first |
| --- | --- | --- |
| See a comparison as a visual page | Canvas | Agent has `canvas`; use a web interface that can display it |
| Follow the steps of a longer task | Conversation checklist | Agent has `todo`; do not replace your external task tracker |
| Keep a useful preference for later | Memory | Memory tools and the intended store are available; avoid secrets |
| Run a repeated task | Scheduling | Agent has `cron`; gateway must be running; choose a timezone |
| Ask another agent for a second view | Agent communication | An appropriate agent exists and communication is permitted |
| Read a page or use another service | Extension tools | Extension, account and permissions are configured |

## Display a comparison in Canvas

Canvas is a page beside your conversation that the agent can create. It can show tables, charts and simple interactive forms.

1. Give the agent a small, non-sensitive set of information. For example, paste three proposed meeting times.
2. Ask it to compare the options in Canvas using only the information you supplied.
3. Open the Canvas link returned by the tool when one is available, or the Canvas view in your portal layout.
4. Check that every option is present and that the agent has not invented prices, dates or other facts.

Example request:

> Compare these three meeting times in Canvas. Show the date, time and timezone for each. Use only the details I provide. Do not create calendar events.

Canvas output can be replaced by another render in the same conversation. A displayed form is not proof that data was sent to another service. Review any submission action before using it. See [Canvas details](../features/canvas.md).

## Follow a task with a checklist

The `todo` tool keeps the agent's working steps for the current conversation. It helps you see what is pending, underway or complete.

Example request:

> Make a checklist for comparing these two documents. Read them, list the differences, then summarize the open questions. Mark a step complete only after doing it. Do not edit either document.

This requires file-reading access if the documents are files rather than pasted text. Check both the checklist and the actual results. A checked box alone is not proof of work.

This checklist is not a replacement for your project task manager, issue tracker, deadlines or assigned work. Ask the agent to use that external system when you need an official task record. See [Conversation checklist details](../features/todo.md).

## Save and find a preference with memory

Memory can keep information for later retrieval when the agent's memory tools and store are configured. It is not a promise that the model remembers every message forever.

1. Choose a harmless preference, such as how you like summaries formatted.
2. Ask the agent to save it to its own memory.
3. Check that the memory tool reports a save, rather than relying only on “I will remember.”
4. Ask it to search memory for that preference and show what it found.

Example request:

> Save this preference in your own memory: use three short bullets for a routine status summary. Then search memory for it and show me the saved preference.

Do not save passwords or tokens. Shared stores can have different access rules from an agent's own memory. Search results can be incomplete; an empty result does not prove that no record exists. The [memory explanation](../features/hybrid-memory-retrieval.md) covers retrieval behavior, and [agent settings](agents.md) covers configuration. Do not manually delete database files as a way to remove one memory.

## Schedule a repeated task

Scheduling uses jobs, often called **cron jobs**. A job has a schedule, a timezone and work to perform. Writing “remind me later” in a normal reply does not create a job.

For a first test, ask for a **disabled** job so you can inspect it before it runs:

> Create a disabled daily job that asks me to review my priorities at 09:00 in my timezone. Ask which timezone I mean before creating it. Show the job ID, schedule, timezone, selected agent and where I should check its results. Do not enable it yet.

Check the actual job record. If no timezone is supplied, the cron tool defaults to UTC, which may differ from your local time. Confirm the timezone explicitly before enabling the job. Scheduled agent prompts use model turns when they run; command jobs run scripts and need extra care.

When ready, ask the agent to enable the inspected job. Check its run history after the first scheduled time. Do not rely on a notification channel that you have not configured and tested. If the gateway is stopped or the provider fails, the task may not complete.

To stop future runs, ask the agent to disable that exact job ID and verify the resulting record. A one-time job requires the scheduler's one-shot setting; a sentence telling the agent to delete itself is not the same thing. See [Scheduling reference](../cron-and-scheduling.md).

## Ask another agent for help

If another suitable agent is available and communication is allowed, ask your agent to get a second view on a small question. Specify what information it may share.

> Ask an available reviewer agent to identify unclear wording in this paragraph. Share only the paragraph. Do not change files or publish the review.

Check that another agent was actually contacted and that its response is identified. Do not accept an invented “reviewer says” message. Permission or budget limits may prevent communication, and another model turn can add cost. See [Agent communication](../features/agent-exchange.md).

## If a feature is unavailable

- Check the selected agent, conversation and tool availability.
- Check configuration and required external accounts before retrying.
- Treat denied access as a boundary, not an invitation to disable safeguards.
- Ask for a clear failure report rather than a guessed success result.

## Next steps

- [Usage ideas](usage-ideas.md): combine these features into small useful tasks.
- [Use extensions](using-extensions.md): configure additional tools.
- [Providers and models](providers.md): connect the model service.

The examples were checked against `CanvasTool`, `TodoTool`, the memory tools in `BotNexus.Memory/Tools`, and the current `BotNexus.Cron/Tools/CronTool` schema, together with their linked guides. They are suggested user requests, not executed workflows.
