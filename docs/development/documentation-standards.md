# Documentation standards

Use this standard when writing or reviewing BotNexus documentation, whether you are a human or an agent. Help the reader complete a task or understand a concept without guessing. Accuracy and safety take priority over shorter wording.

Apply it to new pages and changed sections now. Improve existing pages in small, focused pull requests (PRs). This standard does not mean that every existing page already complies.

## Choose the audience

| Guide | Reader knowledge | What to explain |
| --- | --- | --- |
| [User guide](../user-guide/README.md) | Basic computer use: files, folders, and a browser. No programming knowledge. | How to install, chat, change settings, and recover from problems. Explain terminal use before requiring commands. |
| [Developer guide](README.md) | First-year computer science: basic programming concepts. No assumed .NET or BotNexus experience. | Required tools, how to build and change the project, and project-specific patterns when first used. |
| [Architecture guide](../architecture/README.md) | Readers ready to study system design. Link prerequisite developer material. | Component responsibilities, data flow, boundaries, decisions, and trade-offs. Define specialized terms. |

Readers in all three groups may use English as a second language. A technical audience is not a reason to use complicated English. Name the audience and goal near the top of each page. Link deeper explanations rather than putting the full architecture before a user's first task.

## Organize by what the reader wants to do

Providers, extensions, channels and features belong in the **User guide** when the page explains how to choose, configure or use them. Do not make users visit the Developer guide merely because a capability is implemented as an extension.

The **Developer guide** explains how to build, test or integrate a capability. The **Architecture guide** explains its internal design and trade-offs. A folder name does not decide the audience: keep an existing URL when possible and place its navigation link where the intended reader will look.

A user-facing capability guide must answer:

1. What does it do, and when would I use it?
2. What accounts, software, permissions or costs should I know about?
3. How do I enable or configure it on my installation?
4. What can I try first, and what result should I check?
5. What are some other useful tasks or example requests?
6. What can fail, what are its limits, and how do I turn it off or stop it where applicable?

Label suggested requests as examples, not guaranteed or previously observed outcomes. State required tools and access before each example. Do not claim that writing a prompt installs an extension or grants permission. Link the exact reference for advanced options instead of duplicating it.

Keep technical detail available, but outside the beginner's main path. When a page mixes audiences, add a clear user introduction and link the implementation explanation, or split the content while retaining links from the original URL. Use the [documentation map](documentation-map.md) to find the owning section and known exceptions.

## Write plainly and name things consistently

- Use short sentences, direct verbs, and one main idea per paragraph. Address the reader as “you.”
- Use numbered steps for ordered actions. State where an action takes place and what it changes.
- Avoid idioms, jokes, metaphors, and unexplained abbreviations. Do not use “simply,” “obviously,” or “just” to describe instructions.
- Define jargon and spell out acronyms at first use. For example, a command-line interface (CLI) is a program you use by typing commands in a terminal.
- Choose one term for each concept. Match visible labels exactly when naming buttons or menus. If a screen says **Chat**, do not tell the reader to find “the messaging console.”
- Use code formatting for exact commands, paths, configuration keys, API names, and identifiers. Do not rename these to make the prose easier. Explain them beside the exact spelling.
- Keep security warnings, limitations, side effects, and distinctions between similar concepts. Shorter text must not change permission or policy meaning.

### Names and glossary entries

Check the relevant reference and source before choosing a product term. Define it at first use even when a glossary exists. Reuse the definition from the concept's owning page and link there. Add a short “Terms” section when several definitions are needed; do not create a second competing reference.

For example, keep **conversation** and **session** distinct and link to [Conversations](../user-guide/conversations.md). Keep the tool identifier `memory_save` unchanged. Use familiar descriptive link text, such as “Install BotNexus,” rather than a file name or “click here.” Preserve existing page URLs and heading anchors where possible; when changing one, update incoming links.

### Before and after: writing examples

These are **writing examples, not verified BotNexus procedures or promises of behavior**. Their labels and outcomes must be checked before reuse in a task guide.

| Before | After | Reason |
| --- | --- | --- |
| “Just fire up the UI and you're good to go.” | “Open the web interface in your browser.” | Removes an idiom and an unsupported success claim. |
| “Obviously, configure the LLM backend first.” | “First, configure the service that supplies the language model. This service is called a provider.” | Explains the term and order. |
| “The operation failed due to invalid credentials.” | “Sign-in failed. Check that you used the account required by this provider.” | States the problem and a specific check. |
| “Restore your backup to recover everything.” | “State which data the backup contains before giving restore steps. List data it does not contain.” | Prevents a broad recovery promise. |

## Choose the page type

- **Tutorial:** teaches through one complete, bounded example. Give prerequisites, ordered steps, and checkpoints.
- **Task guide:** helps a reader achieve a specific goal. Use the template below and link optional detail.
- **Reference:** describes exact options, commands, defaults, allowed values, and constraints. Keep lookup tables complete within their stated scope; do not turn them into a second tutorial.
- **Explanation:** explains why or how something works. State boundaries and trade-offs. Put detailed design in the architecture guide.

One page may link to the other types. Keep its main purpose clear. Link the existing installation or command recipe instead of copying it into every landing page.

## Task-page template

Replace each prompt with verified content. Remove sections only when they do not apply.

```markdown
# Verb and task

Who this is for and the goal. State the version or implementation status.

## Before you start

- Operating system and supported environment.
- Tools and required versions, with installation links and a way to check them.
- Shell to use, how to open it, and the working directory for commands.
- Required account, permissions, and configuration. Explain placeholders.
- Credential safety: where secrets are entered and stored; never publish real secrets.
- Side effects, interruptions, and backup or recovery needs before the first action.

## Steps

1. State one action and where to perform it.
2. Show the next action. Label each command block with its shell or language.
3. Provide a checkpoint before the reader continues to a risky action.

## Expected result

Describe the visible result. Label sample output and explain variable values.
State what this check proves and what it does not prove.

## If it does not work

Give a symptom, a safe check, and the next action. Say when to stop and seek help.
Explain how to remove secrets from logs before sharing them.

## Next steps

Link the relevant reference, related task, and deeper explanation.
```

Do not put a warning after the command it qualifies. Separate Windows and Linux instructions when they differ. Do not imply that a shell or tool comes with an operating system without evidence. Never publish credentials, real access tokens, or private machine paths in examples.

## Verify claims and label their status

Check the current source in the target branch, not an old page or a previous agent's summary. For a configuration claim, trace the declaration, the **binder** (code that reads the setting), and the **consumer** (code that acts on it). A key appearing in a class does not prove that a running feature uses it. For a command, check registration, argument parsing, and its handler. Use tests as supporting evidence, not as a substitute for reading the relevant behavior.

Keep two kinds of evidence separate in the PR's existing **Validation** section:

- **Source-verified:** name the revision, file, and symbol that support the claim, and what you traced. State that the procedure was not executed if it was not.
- **Executed:** record the exact steps or commands, environment, version, observed result, and relevant output. Redact secrets. Do not turn expected output into a claim of observed success.

Source verification can support a factual correction without running a live service. It cannot prove a clean installation succeeds, credentials work, or a backup restores all data. Installation, update, restore, and security examples need risk-appropriate review even when source-verified. Keep the [release walkthrough](documentation-grooming.md#the-release-walk) as execution evidence for installation.

State applicability near the start of a page or above an affected example:

| Label | Meaning and required context |
| --- | --- |
| **Released** | Present in a named release. Verify against that release, not only `main`. |
| **Main branch** | Verified at a named commit on `main`; not a promise that the installed release includes it. |
| **Experimental** | Implemented but subject to stated limitations or change. Describe those limits. |
| **Planned** | Not implemented. Link the tracking issue; do not present it as a usable procedure. |
| **Legacy** | Historical or superseded material. Explain the limitation and link the replacement above any sample. |

Version and maturity can both matter: an experimental feature may exist in a release. Do not infer support for another operating system, provider, or version from one successful example. Correct an existing factual error when evidence establishes the answer. Do not invent support commitments or product decisions to fill a gap.

## Review and continued improvement

Use [Documentation grooming](documentation-grooming.md) for the existing lint, understanding review, documentation build, and release walkthrough. A lint or link-check pass does not prove that a reader can follow the page.

Agents may continually propose and open routine documentation PRs and independently review them without a human reviewing every wording change. Keep each PR focused on one task, page, or related set of defects. Prioritize entry pages, risky instructions, and sections touched by product changes. Record remaining work in issues instead of claiming the whole documentation set is fixed.

### Review is not merge authority

| Action | Required distinction |
| --- | --- |
| **Author self-review** | The author checks the diff, sources, audience, and validation evidence. This is not independent approval. |
| **Independent agent review** | A reviewer that did not author the change reads the actual candidate diff and sources. Record its identity or run, reviewed revision, findings, and outcome. A separate run is not evidence that GitHub sees a different identity. |
| **GitHub approval** | An actual submitted approval from an eligible authenticated reviewer. Verify the identity and repository rules. An agent review report or chat message is not a GitHub approval. Never approve your own PR through another run using the same identity. |
| **Merge** | A separate action requiring explicit merge authorization and the actual repository protections and checks. This standard grants no standing or automatic merge authority. |

An independent agent may approve low-risk wording, navigation, and evidence-backed factual corrections when no support promise, safety meaning, or ownership restriction changes. Installation, update, restore, credential, permission, and security examples are not low-risk wording changes merely because only Markdown changed. Review their risks and required evidence before deciding readiness.

Ask a human to decide unsupported support promises, product or policy choices, changes to security meaning, and claims whose source is unclear. Respect `owner` and `ai-policy` headers and the [document ownership rules](https://github.com/sytone/botnexus/blob/main/AGENTS.md#document-ownership); obtain the required human approval for protected pages. Do not fabricate review evidence, bypass checks, create scheduled jobs, or change branch protections under this standard. If eligible GitHub approval is unavailable, report that blocker rather than treating an agent report as a substitute.

Use the existing [PR and commit conventions](pr-and-commit-conventions.md) and repository PR template, not a new documentation-only schema. Put evidence, risk, and unresolved decisions in the corresponding existing sections. Follow the contributor-usability requirements tracked in [#3894](https://github.com/sytone/botnexus/issues/3894): expose prerequisites and supported setup paths, not author-private state. That issue tracks the shared contract and enforcement; this page does not claim its proposed gate is already implemented. A documentation-only change may explain why runtime provisioning evidence does not apply, but must not claim an unperformed clean-install check.

## Author and reviewer checklist

- [ ] Audience, goal, and page type are clear; prerequisites match the reader's knowledge.
- [ ] Terms are defined, names are consistent, and identifiers remain exact.
- [ ] Steps are ordered; warnings precede actions; expected results and recovery checks are useful.
- [ ] Behavior and status are supported by source evidence; execution claims have actual results.
- [ ] Security, support, ownership, and higher-risk examples received the required review.
- [ ] Existing links and useful content are preserved; deeper detail is linked rather than duplicated.
- [ ] Grooming lint, documentation build, and understanding review have recorded outcomes or explicit gaps.
- [ ] Author review, independent review, GitHub approval, and merge authorization are reported separately.
