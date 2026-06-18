// @ts-check
/**
 * Unit tests for the security-sensitive boundary-file guard.
 * Run with:  node --test .github/scripts/security-sensitive-guard.test.mjs
 *
 * These cover the security-critical pure logic: which paths are sensitive,
 * how the head-SHA-bound approval token is parsed, and SHA matching. The
 * `run()` orchestration is exercised with a stubbed `github`/`context`/`core`
 * to assert the block/approve decision and the permission gate.
 */

import { test } from "node:test";
import assert from "node:assert/strict";
import {
  matchSensitivePaths,
  parseApprovalSha,
  shaMatchesHead,
  run,
  APPROVE_COMMAND,
} from "./security-sensitive-guard.mjs";

test("matchSensitivePaths flags .gitignore and .gitattributes", () => {
  assert.deepEqual(matchSensitivePaths([".gitignore"]), [".gitignore"]);
  assert.deepEqual(matchSensitivePaths([".gitattributes"]), [".gitattributes"]);
});

test("matchSensitivePaths flags any workflow file via prefix", () => {
  assert.deepEqual(
    matchSensitivePaths([".github/workflows/ci-build-test.yml"]),
    [".github/workflows/ci-build-test.yml"]
  );
});

test("matchSensitivePaths flags CODEOWNERS and the guard script/workflow", () => {
  assert.deepEqual(matchSensitivePaths([".github/CODEOWNERS"]), [".github/CODEOWNERS"]);
  assert.deepEqual(
    matchSensitivePaths([".github/scripts/security-sensitive-guard.mjs"]),
    [".github/scripts/security-sensitive-guard.mjs"]
  );
});

test("matchSensitivePaths ignores ordinary source/doc files", () => {
  assert.deepEqual(
    matchSensitivePaths([
      "src/gateway/BotNexus.Gateway/GatewayHost.cs",
      "docs/index.md",
      "README.md",
    ]),
    []
  );
});

test("matchSensitivePaths normalizes backslashes and ./ prefix", () => {
  assert.deepEqual(matchSensitivePaths(["./.gitignore"]), [".gitignore"]);
  assert.deepEqual(
    matchSensitivePaths([".github\\workflows\\release-cli.yml"]),
    [".github/workflows/release-cli.yml"]
  );
});

test("matchSensitivePaths de-duplicates", () => {
  assert.deepEqual(
    matchSensitivePaths([".gitignore", "./.gitignore", ".gitignore"]),
    [".gitignore"]
  );
});

test("parseApprovalSha extracts a line-anchored SHA token", () => {
  assert.equal(
    parseApprovalSha(`${APPROVE_COMMAND} abc1234`),
    "abc1234"
  );
  assert.equal(
    parseApprovalSha(`please proceed\n${APPROVE_COMMAND} DEADBEEFCAFE`),
    "deadbeefcafe"
  );
});

test("parseApprovalSha rejects command smuggled mid-line (not line-anchored)", () => {
  assert.equal(
    parseApprovalSha(`nonsense ${APPROVE_COMMAND} abc1234`),
    null,
    "command must start a line"
  );
});

test("parseApprovalSha rejects missing/short/non-hex tokens", () => {
  assert.equal(parseApprovalSha(`${APPROVE_COMMAND}`), null);
  assert.equal(parseApprovalSha(`${APPROVE_COMMAND} 12345`), null, "too short (<7)");
  assert.equal(parseApprovalSha(`${APPROVE_COMMAND} zzzzzzz`), null, "non-hex");
  assert.equal(parseApprovalSha(""), null);
  assert.equal(parseApprovalSha(null), null);
});

test("shaMatchesHead accepts a hex prefix and the full SHA, rejects mismatch", () => {
  const head = "abc1234def5678901234567890abcdef12345678";
  assert.equal(shaMatchesHead("abc1234", head), true);
  assert.equal(shaMatchesHead(head, head), true);
  assert.equal(shaMatchesHead("abc1235", head), false);
  assert.equal(shaMatchesHead("", head), false);
  assert.equal(shaMatchesHead("abc1234", ""), false);
});

// --- run() orchestration with stubs -----------------------------------------

/**
 * Builds a stub github/context/core trio for run().
 * @param {object} opts
 * @param {string[]} opts.changedPaths
 * @param {Array<{body:string, login:string}>} [opts.comments]
 * @param {Record<string,string>} [opts.permissions] login -> permission
 * @param {string} [opts.headSha]
 */
function makeHarness(opts) {
  const headSha = opts.headSha ?? "abc1234def5678901234567890abcdef12345678";
  const comments = opts.comments ?? [];
  const permissions = opts.permissions ?? {};
  const state = { failed: null, notices: [], summaryRaw: [] };

  const github = {
    paginate: async (fn, params) => fn(params),
    rest: {
      pulls: {
        listFiles: async () => opts.changedPaths.map((filename) => ({ filename })),
      },
      issues: {
        listComments: async () =>
          comments.map((c) => ({ body: c.body, user: { login: c.login } })),
      },
      repos: {
        getCollaboratorPermissionLevel: async ({ username }) => ({
          data: { permission: permissions[username] ?? "none" },
        }),
      },
    },
  };

  const context = {
    repo: { owner: "Sytone", repo: "botnexus" },
    payload: { pull_request: { number: 7, head: { sha: headSha } } },
  };

  const summary = {
    addHeading() { return summary; },
    addRaw(s) { state.summaryRaw.push(s); return summary; },
    write: async () => {},
  };
  const core = {
    info() {},
    warning() {},
    notice(m) { state.notices.push(m); },
    setFailed(m) { state.failed = m; },
    summary,
  };

  return { github, context, core, state };
}

test("run passes silently when no sensitive files changed", async () => {
  const h = makeHarness({ changedPaths: ["src/Foo.cs", "docs/x.md"] });
  await run(h);
  assert.equal(h.state.failed, null);
});

test("run blocks when a sensitive file changed and there is no approval", async () => {
  const h = makeHarness({ changedPaths: [".gitignore"] });
  await run(h);
  assert.ok(h.state.failed, "should setFailed");
  assert.match(h.state.failed, /\.gitignore/);
  assert.match(
    h.state.failed,
    new RegExp(APPROVE_COMMAND.replace(/[.*+?^${}()|[\]\\]/g, "\\$&"))
  );
});

test("run approves when an admin posts a head-SHA-bound ack", async () => {
  const headSha = "abc1234def5678901234567890abcdef12345678";
  const h = makeHarness({
    changedPaths: [".github/workflows/ci-build-test.yml"],
    comments: [{ body: `${APPROVE_COMMAND} ${headSha}`, login: "Sytone" }],
    permissions: { Sytone: "admin" },
    headSha,
  });
  await run(h);
  assert.equal(h.state.failed, null, "should not fail when approved");
  assert.ok(h.state.notices.some((n) => /approved by @Sytone/i.test(n)));
});

test("run rejects an approval from a non-maintainer (read permission)", async () => {
  const headSha = "abc1234def5678901234567890abcdef12345678";
  const h = makeHarness({
    changedPaths: [".gitignore"],
    comments: [{ body: `${APPROVE_COMMAND} ${headSha}`, login: "drive-by" }],
    permissions: { "drive-by": "read" },
    headSha,
  });
  await run(h);
  assert.ok(h.state.failed, "read-permission approval must not unblock");
});

test("run rejects an approval bound to a stale (different) head SHA", async () => {
  const h = makeHarness({
    changedPaths: [".gitignore"],
    comments: [{ body: `${APPROVE_COMMAND} 0000000`, login: "Sytone" }],
    permissions: { Sytone: "admin" },
    headSha: "abc1234def5678901234567890abcdef12345678",
  });
  await run(h);
  assert.ok(h.state.failed, "approval for a different SHA must not unblock");
});
