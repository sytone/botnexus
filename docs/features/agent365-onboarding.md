# Agent 365 Admin and Onboarding Guide

This is the **operator-facing** guide for running a BotNexus agent under
[Microsoft Agent 365](https://learn.microsoft.com/en-us/microsoft-agent-365/developer/): what has to
exist in your Microsoft Entra tenant, which capability tier you are actually buying, and which
BotNexus config keys bind the two together.

It is deliberately **not** a repeat of the reference pages. Those stay authoritative:

- [Agent 365 Channel](../extensions/agent365.md) — the adapter surface, `channels:agent365` keys, capability flags.
- [Agent 365 Observability Export](./agent365-observability.md) — the `telemetry.agent365` OTLP export path.
- [`docs/configuration.md`](../configuration.md) — the full config reference for both sections.

## Read this first: what BotNexus implements today

Agent 365 is a layered platform, and BotNexus does not sit on all of its layers. Being precise about
this is the whole point of the page — an operator who assumes "Agent 365 support" means "AI teammate"
will provision a tenant capability that nothing in this codebase currently consumes.

| Agent 365 capability tier | BotNexus status | Where it lives |
|---|---|---|
| **Register** — agent has an identity and is visible in tenant inventory | **Implemented** (message round-trip) | [`channels:agent365`](../extensions/agent365.md#configuration) |
| **Observability** — OTel traces of inference and tool calls | **Implemented** (direct OTLP, no A365 SDK) | [`telemetry.agent365`](./agent365-observability.md#configuration) |
| **Work IQ** — governed Mail/Calendar/SharePoint/Teams tool access | **Not implemented** | tracked by the epic ([#1875](https://github.com/Sytone/botnexus/issues/1875)) |
| **AI teammate** — the agent's own user account, mailbox, Teams presence | **Not implemented** | tracked by the epic ([#1875](https://github.com/Sytone/botnexus/issues/1875)) |

Throughout, **BotNexus remains the response engine**. The Microsoft SDKs are used as a channel and a
telemetry sink; every reply is produced by the normal BotNexus agent loop, providers, memory, and
tools. Microsoft states the same seam from its side: the Agent 365 SDK
["does not create or host agents"](https://learn.microsoft.com/en-us/microsoft-agent-365/developer/),
it enhances agents you already built.

## Tenant prerequisites

From the Microsoft
[quickstart prerequisites](https://learn.microsoft.com/en-us/microsoft-agent-365/developer/get-started#prerequisites):

- A Microsoft tenant with **Agent 365 enabled**. Sign in as a **Global Administrator**, or as an
  **Agent ID Developer** with a Global Administrator available to complete the OAuth permission grants.
- An **Azure subscription** with permission to create resources.
- The Agent 365 CLI requires **.NET 8.0 or later**
  ([CLI prerequisites](https://learn.microsoft.com/en-us/microsoft-agent-365/developer/agent-365-cli#prerequisites)).

### Licensing is per-capability, not per-product

This is the single most common onboarding surprise, and it is worth reading twice. Microsoft
documents licensing per adopted capability:

| Capability | Licence requirement |
|---|---|
| Identity | No additional licence; the tenant must have Agent 365 enabled. |
| Observability | At least one user in the tenant with a **Microsoft 365 E7 or Microsoft Agent 365** licence **assigned**. Without it, **telemetry is dropped silently**. |
| Tooling (Work IQ) | A **Microsoft 365 Copilot** licence. Work IQ MCP is in preview. |
| Notifications (agent's own user account) | Available **only to tenants in the Frontier preview program**. |

Source: [Quickstart: Connect an existing agent to Agent 365](https://learn.microsoft.com/en-us/microsoft-agent-365/developer/get-started#prerequisites).

The observability row is the one that bites: the licence being *present* in the tenant is not
sufficient — it must be **assigned** to a user, or ingestion returns `200 OK` and discards the
request. That failure mode is described further in
[Agent 365 Observability Export](./agent365-observability.md#tenant-prerequisites).

## Frontier gating

Two things — and, per the current Microsoft documentation, only these two — are gated on the
[Frontier preview program](https://www.microsoft.com/microsoft-365-copilot/frontier-program):

1. **The agent's own user account.** An optional second Entra account that can hold licences and
   Microsoft 365 resources such as a mailbox, so the agent can appear in the directory and be
   reached through Teams, Outlook, Word comments, and email
   ([Agent 365 identity](https://learn.microsoft.com/en-us/microsoft-agent-365/developer/identity#agents-user-account)).
2. **The `Agentic-User` execution mode**, which is how an agent acts *as* that account.

The Register and Observability tiers — the two BotNexus implements today — **do not require
Frontier**. You can onboard a BotNexus agent end-to-end without being enrolled.

## Provisioning: blueprint → agent identity

### The identity objects

Agent 365 represents each agent as a Microsoft Entra service principal with `servicePrincipalType`
set to `ServiceIdentity`. Three objects make up the model, and **not every agent uses all three**
([Agent 365 identity](https://learn.microsoft.com/en-us/microsoft-agent-365/developer/identity#identity-objects)):

| Object | What it is |
|---|---|
| **Agent identity blueprint** | A template in Entra ID defining a *kind* of agent. Holds the credentials, declared and inheritable permissions, verified publisher, and app roles. **One blueprint can create many agent identities.** |
| **Agent identity** | The account an individual agent runs as — its own object ID, display name, sponsor, and permission assignments. |
| **Agent's user account** | Optional second account, only when the agent needs a mailbox or directory presence. **Frontier only.** |

The relationship is **one-to-many**, unlike a normal app registration's one-to-one with its service
principal. That is what makes it governable: because agents of the same kind share a blueprint, an
administrator can apply a Conditional Access policy, revoke a permission grant, or disable every
agent of that kind **in one operation**.

### Blueprints are a credential boundary

Credentials live on the **blueprint**, not on the agent identity — the identity itself stores none.
The blueprint uses them to acquire tokens for every agent identity created from it. Supported
credential types are federated identity credentials, certificates and cryptographic keys, and client
secrets; agents running on Azure can federate the blueprint to a managed identity so **no secret is
stored on the blueprint at all**.

> **Design consequence for BotNexus.** Blueprint credentials are shared by every agent identity
> created from that blueprint. Group only agents that can safely share credentials and inherited
> baseline permissions. The epic's working decision is **one blueprint per BotNexus agent**
> ([#1875](https://github.com/Sytone/botnexus/issues/1875)); "one blueprint per credential boundary"
> is Microsoft's own phrasing of the same rule.

Agent identities do not authenticate with passwords, SMS, passkeys, or authenticator apps, so **MFA
does not apply to them** — govern them with Conditional Access instead.

### Every blueprint and identity needs a sponsor

Each agent identity and each blueprint requires **at least one sponsor**: the business
representative accountable for the agent's purpose and lifecycle. Sponsors may be asked whether an
agent should be retained or disabled, and security teams use the sponsor to reach a responsible
human during an incident. Decide this before provisioning — it is not an afterthought field.

### Running the provisioning

Microsoft's documented path is an **AI-coding-assistant skills package**, not a hand-run command
sequence. `gh skill add microsoft/agent365-skills` installs it into Claude Code, GitHub Copilot CLI,
or VS Code agent mode; the `a365-setup` skill then detects the stack and routes to either the
standard-agent path (`make-a365-agent`) or the AI-teammate path (`make-ai-teammate`). Setup writes
generated IDs and endpoints to `a365.generated.config.json`
([quickstart](https://learn.microsoft.com/en-us/microsoft-agent-365/developer/get-started)).

The CLI underneath is a .NET global tool, for CI/CD automation or working without a coding assistant
([CLI reference](https://learn.microsoft.com/en-us/microsoft-agent-365/developer/agent-365-cli)):

```powershell
dotnet tool install --global Microsoft.Agents.A365.DevTools.Cli
a365 -h
```

> **Not verified here.** The per-subcommand `a365` syntax for blueprint creation is published as a
> generated CLI reference rather than a fixed command list, and Microsoft's guidance is explicitly
> that the Skills package runs these commands for you. This guide therefore does **not** reproduce
> specific `a365 <verb>` invocations — a transcribed command list would rot silently against a
> preview tool. Use `a365 -h` and the
> [CLI reference](https://learn.microsoft.com/en-us/microsoft-agent-365/developer/agent-365-cli) as
> the authority.

### Consent is a separate, explicit step

> **Declaring permissions on a blueprint does not grant them.** An administrator must consent,
> either on the blueprint principal or on individual agent identities
> ([Agent 365 identity](https://learn.microsoft.com/en-us/microsoft-agent-365/developer/identity#permissions-and-runtime-flow)).

Declare shared baseline permissions on the blueprint so every agent identity inherits them; assign
permissions directly to an agent identity when the access is specific to that agent. **Azure RBAC is
an exception** — blueprints cannot hold Azure RBAC roles, so assign those directly to each agent
identity.

## Execution modes: which one is BotNexus using?

An agent runs in one of three modes. The mode determines who the agent acts for, which token subject
appears downstream, and which consent is required
([Agent 365 identity](https://learn.microsoft.com/en-us/microsoft-agent-365/developer/identity#permissions-and-runtime-flow)):

| Mode | Acts as | Permission type | Frontier? |
|---|---|---|---|
| **S2S** (service-to-service) | Its own agent identity, no user context. Scheduled tasks, monitoring, background processing. | Application | No |
| **OBO** (on-behalf-of) | A signed-in human user; the user is the token subject and the agent identity is the actor. | Delegated | No |
| **Agentic-User** | Its own Entra user account — mailbox, Teams presence, Word/Outlook interactions. | User-based | **Yes** |

**BotNexus today is S2S.** The channel adapter authenticates outbound Activity replies with an Entra
**client-credential MSAL flow** using `clientId` / `clientSecret`
([Agent 365 Channel](../extensions/agent365.md#how-it-works)), and the observability exporter's S2S
recipe uses the `.../.default` scope against the `/observabilityService/...` route
([Observability Export](./agent365-observability.md#authentication)).

Two consequences worth planning around:

- The **observability exporter must use the same authentication mode the agent authenticates with.**
  Mixing an OBO token with the S2S route (or the reverse) is a misconfiguration, not a preference.
- **Work IQ tooling requires a delegated permission model**, so it is unavailable to an agent running
  purely S2S. This is a real constraint on the future Work IQ tier, not just a licence question.

## Binding a provisioned identity to a BotNexus agent

Both BotNexus-side surfaces are already documented in full; this section only shows how they compose.
No new configuration keys are introduced by this guide.

### 1. Enable the channel (Register tier)

Bind the Entra app to the BotNexus agent that should answer inbound activities:

```json
{
  "channels": {
    "agent365": {
      "clientId": "${AGENT365_CLIENT_ID}",
      "clientSecret": "${AGENT365_CLIENT_SECRET}",
      "tenantId": "${AGENT365_TENANT_ID}",
      "agentId": "farnsworth",
      "inboundRoute": "/agent365/messages"
    }
  }
}
```

`agentId` is the **BotNexus** agent ID that inbound messages route to — it is not the Entra Agent ID.
Full key reference: [Agent 365 Channel → Configuration](../extensions/agent365.md#configuration) and
[`docs/configuration.md`](../configuration.md).

The extension is **disabled by default** in its manifest. Configuring the section is necessary but
not sufficient — also set the manifest `enabled` flag.

### 2. Enable observability export

```json
{
  "telemetry": {
    "enabled": true,
    "agent365": {
      "enabled": true,
      "endpoint": "https://agent365.svc.cloud.microsoft/observabilityService/tenants/<tenantId>/otlp/agents/<agentId>/traces?api-version=1",
      "authHeaderValue": "Bearer <access-token>"
    }
  }
}
```

Off by default; zero egress to Agent 365 until both `enabled` and `endpoint` are set. BotNexus does
**not** mint the token — acquire it out of band via MSAL. Full reference:
[Agent 365 Observability Export](./agent365-observability.md).

## Verifying the onboarding

1. **Round-trip a message.** Send an activity to `inboundRoute` and confirm a reply. This proves the
   Register tier end-to-end.
2. **Confirm the agent appears** in Agent 365 under its registered identity.
3. **Check spans arrive.** After a test run, spans should appear in Microsoft Defender, Microsoft
   Purview, and the Microsoft 365 admin center.

### When telemetry looks like it vanished

Microsoft calls out two causes that both return **HTTP 200**
([quickstart](https://learn.microsoft.com/en-us/microsoft-agent-365/developer/get-started#instrument-for-observability)):

- **No assigned E7 / Agent 365 licence** on any user in the tenant — the whole request is dropped.
  The SKU merely being present is not sufficient.
- **No valid `invoke_agent` span at the run's root.** The spans stay queryable in Defender advanced
  hunting but appear in none of the three surfaces. The root span is the top-level span emitted for
  one agent invocation; every tool call and inference event nests inside it.

> **Known gap.** BotNexus exports its own canonical turn / tool-call / provider-invocation spans and
> does **not** currently emit a span named `invoke_agent` at the run root. If your spans are
> queryable in Defender advanced hunting but missing from the admin center, this is the likely
> cause. Verify against your own tenant before treating it as settled.

Other failures — blueprint registration returning authorization errors, the agent not appearing in
Teams — are covered by Microsoft's
[troubleshooting guidance](https://learn.microsoft.com/en-us/microsoft-agent-365/developer/get-started#troubleshoot).

## Governance you inherit

Because the agent is anchored in an Entra-backed identity, tenant controls apply without further
BotNexus work: **Conditional Access**, permission grants and consent, and lifecycle operations, plus
**Microsoft Purview** and **Microsoft Defender** visibility once observability is exporting.

Sign-in and audit logs **differentiate between the blueprint, the agent identity, and the agent's
user account**, so a reviewer can identify the credential source, the acting identity, and the token
subject. In S2S operations the agent identity is the token subject; in OBO the signed-in user is the
subject and the agent identity is the actor.

## Related

- [Agent 365 Channel](../extensions/agent365.md) — adapter reference and `channels:agent365` keys.
- [Agent 365 Observability Export](./agent365-observability.md) — `telemetry.agent365` reference.
- [`docs/configuration.md`](../configuration.md) — full configuration reference.
- [`docs/observability.md`](../observability.md) — platform observability architecture.
- Microsoft: [Agent 365 developer hub](https://learn.microsoft.com/en-us/microsoft-agent-365/developer/) ·
  [Quickstart](https://learn.microsoft.com/en-us/microsoft-agent-365/developer/get-started) ·
  [Agent identity](https://learn.microsoft.com/en-us/microsoft-agent-365/developer/identity) ·
  [Agent 365 CLI](https://learn.microsoft.com/en-us/microsoft-agent-365/developer/agent-365-cli)
