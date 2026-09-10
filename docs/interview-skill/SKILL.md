---
name: get-to-know-you
description: "Interview the person you work for and record what you learn, so you stop asking the same questions. Use when a user first works with this agent, when they say you should remember something about them or how they want things done, when you catch yourself asking something you have asked before, or when they ask you to update or forget what you know about them."
metadata:
  writes: agent memory via memory_save
---

# Getting to know the person you work for

An agent that re-derives someone's preferences every session is not stateless, it is
forgetful — and the cost lands on them, not you. This skill turns that into a short
conversation whose results survive.

The whole thing rests on one rule, so it comes first.

## The rule: never write what they cannot fix

Everything you record must be something the user can find and correct afterwards.
Memory a person cannot audit is not helpful, it is a rumour that outlives the
conversation that started it — and the longer it survives, the more confidently it gets
repeated back to them.

So, every time:

1. **Say it back before you write it.** Not "shall I remember things about you?" —
   the actual sentence you are about to store.
2. **Write it in their words** where you can. A note that says what they said is
   correctable. A note that says what you concluded is an argument.
3. **Tell them where it went**, once per session, the first time you write:
   > Saved. You can read, edit or delete anything I remember from the Memory tab in
   > this agent's persona drawer — click the agent's name in the top bar.
4. **Never write a whole interview in one go.** One note per fact. A paragraph
   containing five facts cannot have one of them corrected.

## When to run this

- The user is working with this agent for the first time.
- They say "remember that…", "I always…", "don't do X again".
- You are about to ask something you already asked in an earlier conversation.
- They ask what you know about them, or ask you to change or drop something.

Do **not** run it as an opening ritual on every new conversation. Once is the point.

## The interview

Six questions, asked conversationally and **a few at a time** — not as a form. Stop
early if they lose interest; four good notes beat twelve reluctant ones.

| Ask about | Because you will otherwise guess | Category |
|---|---|---|
| What they do, and what this agent is for | Every judgement call about relevance depends on it | `fact` |
| How much detail they want back | The single most common mismatch | `preference` |
| What they want you to check before doing vs. just do | Prevents both nagging and overreach | `preference` |
| Tools, services and conventions in their environment | Stops suggestions that cannot be run | `fact` |
| Anything you should never do | Cheaper to learn now than by doing it | `preference` |
| How they refer to their own things — projects, systems, people | So their shorthand resolves | `fact` |

Ask follow-ups when an answer is vague. "Be concise" is not yet a note; "no preamble,
lead with the command" is.

## Writing what you learn

One `memory_save` call per fact, categorised so it can be found later:

```
memory_save(
  content: "Prefers answers that lead with the command, then a one-line explanation. No preamble.",
  category: "preference",
  tags: ["communication", "interview"]
)
```

Use `category` from the tool's own set — `decision`, `pattern`, `fact`, `procedure`,
`preference` — and tag every note from this skill with `interview` so the whole set can
be reviewed or removed together later.

**Durable only.** "I'm on holiday next week" is not memory, it is context for today.
Ask yourself whether it will still be true in three months; if you cannot say yes, do
not write it.

**Never write secrets.** Not credentials, API keys, tokens, card numbers or passwords —
not even ones the user volunteers, and not "so you don't have to tell me again". Say
you cannot store those and move on. Memory is readable by anything that can read the
agent's memory, and a shared store is readable by every agent granted it.

### If a shared store is configured

Notes go to the agent's own memory by default, which is right for almost everything
here: how one person likes their answers formatted is not platform knowledge.

Use `store:` only for something genuinely true of the environment rather than the
person — a deployment convention, a service everyone uses — and only when the user
agrees it belongs there. Say which store, by name, before you write:

> That's about the platform rather than about you — shall I put it in
> `platform-knowledge`, where the other agents can see it too?

A shared store is a channel: what you write, other agents will later read as fact.

## Updating and forgetting

- **Correcting** — write the correction as a new note that states the current position
  plainly, and delete the old one if you can identify it. Do not leave both, or a later
  search will find the stale one.
- **Forgetting** — when someone asks you to drop something, drop it and confirm what
  went. Do not argue for keeping it, and do not keep a summary of it.
- **Contradiction** — when what they tell you now conflicts with a note, say so and ask
  which holds. Do not silently pick one, and do not assume the newer one wins; they may
  have been describing an exception.

## Checking your work

Before you finish:

- [ ] Every note was said aloud before it was written.
- [ ] Each note holds one fact.
- [ ] Nothing recorded is a secret, or expires within months.
- [ ] They have been told where their notes live and how to change them.
- [ ] Anything they corrected mid-interview was written corrected, not both ways.
