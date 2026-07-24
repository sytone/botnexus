// @ts-check
/**
 * Unit tests for the PR conventions guard.
 * Run with:  node --test .github/scripts/pr-conventions-guard.test.mjs
 *
 * Covers the pure decision logic: title parsing, UI-path detection, evidence
 * detection, per-type required sections, and the bot exemption / warning-mode
 * behaviour of `run()` with stubbed `github`/`context`/`core`.
 */

import { test } from "node:test";
import assert from "node:assert/strict";
import {
  parseTitle,
  matchUiPaths,
  extractHeadings,
  hasIssueLink,
  hasVisualEvidence,
  hasNoVisibleChangeDeclaration,
  hasNumericEvidence,
  requiredSections,
  evaluate,
  parseApprovalSha,
  shaMatchesHead,
  stripComments,
  run,
  EXEMPT_AUTHORS,
  APPROVE_COMMAND,
} from "./pr-conventions-guard.mjs";

// ---------------------------------------------------------------- title

test("parseTitle accepts a conventional subject with an issue scope", () => {
  const r = parseTitle("fix(#2293): latch the route guard on the fallback branch");
  assert.equal(r.ok, true);
  assert.equal(r.type, "fix");
  assert.equal(r.scope, "#2293");
  assert.equal(r.breaking, false);
});

test("parseTitle accepts a breaking-change marker", () => {
  const r = parseTitle("feat(api)!: drop the legacy tool descriptor shape");
  assert.equal(r.ok, true);
  assert.equal(r.breaking, true);
});

test("parseTitle rejects a missing type", () => {
  assert.equal(parseTitle("just fix the thing").ok, false);
});

test("parseTitle rejects an unknown type", () => {
  const r = parseTitle("wibble: do a thing");
  assert.equal(r.ok, false);
  assert.match(String(r.reason), /Unknown type/);
});

test("parseTitle rejects a trailing period", () => {
  const r = parseTitle("fix: correct the off-by-one.");
  assert.equal(r.ok, false);
  assert.match(String(r.reason), /period/);
});

test("parseTitle rejects a Sentence-cased description", () => {
  const r = parseTitle("fix: Correct the off-by-one");
  assert.equal(r.ok, false);
  assert.match(String(r.reason), /lowercase/);
});

test("parseTitle allows a legitimately capitalized acronym or identifier", () => {
  assert.equal(parseTitle("fix(cli): CLI wizard skips the prompt").ok, true);
  assert.equal(parseTitle("fix: SignalR reconnect drops the queue").ok, true);
});

test("parseTitle rejects an empty title", () => {
  assert.equal(parseTitle("").ok, false);
});

// ------------------------------------------------------------- UI paths

test("matchUiPaths flags razor, scss and wwwroot assets", () => {
  const paths = matchUiPaths([
    "src/extensions/BotNexus.Extensions.Channels.SignalR.BlazorClient/Pages/Home.razor",
    "src/extensions/BotNexus.Extensions.Channels.SignalR.BlazorClient.Mobile/wwwroot/app.css",
    "src/styles/theme.scss",
  ]);
  assert.equal(paths.length, 3);
});

test("matchUiPaths ignores backend-only source", () => {
  assert.deepEqual(
    matchUiPaths([
      "src/gateway/BotNexus.Gateway/GatewayHost.cs",
      "docs/development/README.md",
    ]),
    []
  );
});

test("matchUiPaths excludes bUnit test files — a test renders no user-visible UI", () => {
  assert.deepEqual(
    matchUiPaths([
      "tests/extensions/BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests/ChatPanelTests.cs",
      "tests/extensions/Foo.Tests/Thing.razor",
    ]),
    []
  );
});

test("matchUiPaths normalizes windows separators and de-duplicates", () => {
  const paths = matchUiPaths([
    "src\\extensions\\BotNexus.Extensions.Channels.SignalR.BlazorClient\\Pages\\Home.razor",
    "src/extensions/BotNexus.Extensions.Channels.SignalR.BlazorClient/Pages/Home.razor",
  ]);
  assert.equal(paths.length, 1);
});

// -------------------------------------------------------------- body bits

test("stripComments removes template guidance so boilerplate is not counted", () => {
  assert.equal(stripComments("a <!-- hidden --> b").trim(), "a  b".trim());
});

test("extractHeadings ignores headings that live inside HTML comments", () => {
  const headings = extractHeadings("<!--\n## Summary\n-->\n## Changes");
  assert.equal(headings.has("changes"), true);
  assert.equal(headings.has("summary"), false);
});

test("hasIssueLink accepts Closes/Fixes/Refs", () => {
  assert.equal(hasIssueLink("Closes #2317"), true);
  assert.equal(hasIssueLink("Refs #12"), true);
  assert.equal(hasIssueLink("related to 2317"), false);
});

test("hasVisualEvidence detects markdown images, uploads and video tags", () => {
  assert.equal(hasVisualEvidence("![demo](https://example.com/a.png)"), true);
  assert.equal(
    hasVisualEvidence("https://github.com/sytone/botnexus/assets/1/abc-def"),
    true
  );
  assert.equal(hasVisualEvidence("<video src='x.mp4'></video>"), true);
  assert.equal(hasVisualEvidence("recording: demo.mov"), true);
  assert.equal(hasVisualEvidence("I promise it looks fine"), false);
});

test("hasNoVisibleChangeDeclaration recognises an explicit opt-out", () => {
  assert.equal(hasNoVisibleChangeDeclaration("Pure refactor, no visible UI change"), true);
  assert.equal(hasNoVisibleChangeDeclaration("looks great"), false);
});

test("hasNumericEvidence distinguishes counts from claims", () => {
  assert.equal(hasNumericEvidence("Gateway.Tests 4026/0/1"), true);
  assert.equal(hasNumericEvidence("all tests pass"), false);
});

// --------------------------------------------------------- required sections

test("requiredSections asks fix PRs for a root cause and tests", () => {
  const s = requiredSections("fix");
  assert.equal(s.includes("root cause"), true);
  assert.equal(s.includes("tests"), true);
});

test("requiredSections does not ask docs PRs for tests or root cause", () => {
  const s = requiredSections("docs");
  assert.equal(s.includes("tests"), false);
  assert.equal(s.includes("root cause"), false);
});

test("requiredSections asks feat PRs for tests but not a root cause", () => {
  const s = requiredSections("feat");
  assert.equal(s.includes("tests"), true);
  assert.equal(s.includes("root cause"), false);
});

// ---------------------------------------------------------------- evaluate

const goodFeatBody = `
## Summary
Adds a thing. Closes #10
## Changes
- did it
## Tests
- covered it
## Validation
- Gateway.Tests 100/0
## Risk & rollback
- low
`;

test("evaluate passes a well-formed non-UI feat PR", () => {
  const { violations } = evaluate({
    title: "feat(portal): add a thing",
    body: goodFeatBody,
    changedPaths: ["src/gateway/Thing.cs"],
  });
  assert.deepEqual(violations, []);
});

test("evaluate flags a UI PR with no screenshot or recording", () => {
  const { violations, uiPaths } = evaluate({
    title: "feat(portal): add a thing",
    body: goodFeatBody,
    changedPaths: [
      "src/extensions/BotNexus.Extensions.Channels.SignalR.BlazorClient/Pages/Home.razor",
    ],
  });
  assert.equal(uiPaths.length, 1);
  assert.equal(violations.some((v) => v.rule === "ui-evidence"), true);
});

test("evaluate accepts a UI PR that attaches a recording", () => {
  const { violations } = evaluate({
    title: "feat(portal): add a thing",
    body: `${goodFeatBody}\n## UI evidence\n![streaming](https://example.com/demo.gif)`,
    changedPaths: [
      "src/extensions/BotNexus.Extensions.Channels.SignalR.BlazorClient/Pages/Home.razor",
    ],
  });
  assert.equal(violations.some((v) => v.rule === "ui-evidence"), false);
});

test("evaluate accepts a UI PR that declares no visible delta", () => {
  const { violations } = evaluate({
    title: "refactor(portal): extract a component",
    body: `${goodFeatBody}\nPure refactor, no visible UI change.`,
    changedPaths: [
      "src/extensions/BotNexus.Extensions.Channels.SignalR.BlazorClient/Pages/Home.razor",
    ],
  });
  assert.equal(violations.some((v) => v.rule === "ui-evidence"), false);
});

test("evaluate flags a missing root cause on a fix PR", () => {
  const { violations } = evaluate({
    title: "fix(portal): stop the recursion",
    body: goodFeatBody,
    changedPaths: ["src/gateway/Thing.cs"],
  });
  assert.equal(
    violations.some((v) => v.rule === "sections" && /root cause/.test(v.message)),
    true
  );
});

test("evaluate marks weak validation evidence advisory, not blocking", () => {
  const { violations } = evaluate({
    title: "feat(portal): add a thing",
    body: goodFeatBody.replace("Gateway.Tests 100/0", "all tests pass"),
    changedPaths: ["src/gateway/Thing.cs"],
  });
  const ev = violations.find((v) => v.rule === "evidence");
  assert.ok(ev);
  assert.equal(ev.advisory, true);
});

test("evaluate flags an over-long title", () => {
  const { violations } = evaluate({
    title: `feat(portal): ${"x".repeat(80)}`,
    body: goodFeatBody,
    changedPaths: ["src/gateway/Thing.cs"],
  });
  assert.equal(violations.some((v) => v.rule === "title-length"), true);
});

// ---------------------------------------------------------------- waivers

test("parseApprovalSha requires the command at a line start", () => {
  const sha = "abcdef1234567890";
  assert.equal(parseApprovalSha(`${APPROVE_COMMAND} ${sha}`), sha);
  assert.equal(parseApprovalSha(`please ${APPROVE_COMMAND} ${sha}`), null);
});

test("shaMatchesHead accepts a prefix but rejects a mismatch", () => {
  assert.equal(shaMatchesHead("abcdef1", "abcdef1234567890"), true);
  assert.equal(shaMatchesHead("beef123", "abcdef1234567890"), false);
});

// -------------------------------------------------------------------- run

/** Minimal `core` stub capturing the outcome. */
function makeCore() {
  const state = { failed: null, warnings: [], notices: [], infos: [] };
  const summary = {
    addHeading() { return summary; },
    addRaw() { return summary; },
    async write() {},
  };
  return {
    state,
    core: {
      summary,
      setFailed: (m) => { state.failed = m; },
      warning: (m) => state.warnings.push(m),
      notice: (m) => state.notices.push(m),
      info: (m) => state.infos.push(m),
    },
  };
}

function makeGithub({ files = [], comments = [] } = {}) {
  return {
    paginate: async (fn) => fn(),
    rest: {
      pulls: { listFiles: async () => files },
      issues: { listComments: async () => comments },
      repos: {
        getCollaboratorPermissionLevel: async () => ({ data: { permission: "admin" } }),
      },
    },
  };
}

function makeContext(pr) {
  return { payload: { pull_request: pr }, repo: { owner: "sytone", repo: "botnexus" } };
}

test("run skips exempt automated bot authors", async () => {
  const { state, core } = makeCore();
  await run({
    github: makeGithub(),
    context: makeContext({
      number: 1,
      title: "garbage title",
      body: "",
      head: { sha: "abc" },
      user: { login: EXEMPT_AUTHORS[0] },
    }),
    core,
  });
  assert.equal(state.failed, null);
  assert.match(state.infos.join(" "), /exempt automated bot/);
});

test("run does NOT exempt agent-farnsworth[bot]", async () => {
  const { state, core } = makeCore();
  await run({
    github: makeGithub({ files: [{ filename: "src/gateway/Thing.cs" }] }),
    context: makeContext({
      number: 2,
      title: "garbage title",
      body: "",
      head: { sha: "abc" },
      user: { login: "agent-farnsworth[bot]" },
    }),
    core,
  });
  // Warning-first mode: surfaced but not failed.
  assert.equal(state.failed, null);
  assert.match(state.warnings.join(" "), /convention/i);
});

test("run in warning-first mode never fails the check", async () => {
  const { state, core } = makeCore();
  await run({
    github: makeGithub({ files: [{ filename: "src/gateway/Thing.cs" }] }),
    context: makeContext({
      number: 3,
      title: "nope",
      body: "no sections here",
      head: { sha: "abc123def456" },
      user: { login: "someone" },
    }),
    core,
  });
  assert.equal(state.failed, null);
  assert.equal(state.warnings.length > 0, true);
});

test("run reports a clean pass with no warnings", async () => {
  const { state, core } = makeCore();
  await run({
    github: makeGithub({ files: [{ filename: "src/gateway/Thing.cs" }] }),
    context: makeContext({
      number: 4,
      title: "feat(portal): add a thing",
      body: goodFeatBody,
      head: { sha: "abc123def456" },
      user: { login: "sytone" },
    }),
    core,
  });
  assert.equal(state.failed, null);
  assert.equal(state.warnings.length, 0);
  assert.match(state.notices.join(" "), /passed/i);
});
