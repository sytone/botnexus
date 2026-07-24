// @ts-check
/**
 * PR conventions guard.
 *
 * Checks that a pull request follows the repository's PR + squash-commit
 * conventions (see `docs/development/pr-and-commit-conventions.md`):
 * a Conventional-Commits title, an issue link, the required body sections for
 * the change type, a root cause on fixes, numeric validation evidence, and
 * — for any PR touching the UI surface — screenshot/recording evidence that the
 * new capability actually works with real generating agents and conversations.
 *
 * ROLLOUT MODE (#2317): the guard currently runs in WARNING-FIRST mode. It
 * annotates and writes a job summary but does not fail the check, so the
 * in-flight PR queue can drain before the format is mandatory. Flip
 * `ENFORCEMENT_MODE` to "block" once the queue is clear.
 *
 * SAFETY MODEL (why this is safe to run on `pull_request_target`):
 *   - The workflow checks out ONLY the trusted base-branch copy of this script
 *     (`ref: base.sha`, `persist-credentials: false`). It never executes any
 *     code from the PR head, so an attacker cannot rewrite the guard in their PR.
 *   - The set of changed files is read from the GitHub API (compare base..head),
 *     not from a working tree, so no PR-head content is sourced.
 *   - PR title/body are treated as INERT TEXT: they are only ever regex-matched
 *     and echoed into a job summary, never executed or eval'd.
 *   - Approval is bound to the CURRENT head SHA. A later push to the PR
 *     invalidates a prior approval (no approve-then-sneak-a-commit).
 *   - Approval is only honored from users with `admin`, `maintain`, or `write`
 *     repository permission.
 *
 * This module exports a single `run({ github, context, core })` function so it
 * can be invoked from `actions/github-script` and unit-tested in isolation.
 */

/**
 * Enforcement mode. "warn" surfaces findings without failing the check;
 * "block" fails the check on any non-advisory violation.
 * @type {"warn" | "block"}
 */
export const ENFORCEMENT_MODE = "warn";

/** The comment command an authorized maintainer posts to waive a violation. */
export const APPROVE_COMMAND = "/allow-pr-convention-exception";

/** Repository permission levels allowed to waive a convention violation. */
const APPROVER_PERMISSIONS = Object.freeze(["admin", "maintain", "write"]);

/**
 * Automated external bot authors exempt from the conventions guard (#2317).
 *
 * These open mechanical dependency/tooling PRs with no root cause, no design
 * rationale and no UI evidence to give; holding them to the template produces
 * noise, not review value.
 *
 * NOTE: `agent-farnsworth[bot]` is deliberately NOT exempt. It authors the bulk
 * of substantive changes here and is precisely what this guard exists to hold
 * to the standard.
 */
export const EXEMPT_AUTHORS = Object.freeze([
  "dependabot[bot]",
  "dependabot-preview[bot]",
  "renovate[bot]",
  "github-actions[bot]",
  "copilot-swe-agent[bot]",
]);

/** Conventional Commits types permitted in a PR title. */
export const ALLOWED_TYPES = Object.freeze([
  "feat",
  "fix",
  "chore",
  "docs",
  "refactor",
  "test",
  "perf",
  "style",
  "ci",
  "build",
]);

/** Maximum PR title length; the title becomes the squash-commit subject. */
export const MAX_TITLE_LENGTH = 72;

/**
 * Path globs that mean "this PR changes rendered UI". A PR touching any of
 * these must supply screenshot/recording evidence.
 */
export const UI_PATH_PREFIXES = Object.freeze([
  "src/extensions/BotNexus.Extensions.Channels.SignalR.BlazorClient/",
  "src/extensions/BotNexus.Extensions.Channels.SignalR.BlazorClient.Mobile/",
  "src/extensions/BotNexus.Extensions.Channels.SignalR.BlazorClient.Core/",
]);

/** File suffixes that mean "this PR changes rendered UI". */
export const UI_PATH_SUFFIXES = Object.freeze([
  ".razor",
  ".razor.css",
  ".scss",
]);

/**
 * Normalizes a repository path for matching (backslashes, leading `./`).
 * @param {string} path
 * @returns {string}
 */
function normalizePath(path) {
  return String(path).replace(/\\/g, "/").replace(/^\.\//, "");
}

/**
 * Returns the subset of changed paths that represent rendered-UI surface.
 * Test files are excluded: changing a bUnit test does not change the UI.
 * @param {string[]} changedPaths
 * @returns {string[]}
 */
export function matchUiPaths(changedPaths) {
  const matched = [];
  for (const raw of changedPaths ?? []) {
    const path = normalizePath(raw);
    // A test project never renders user-visible UI; exclude before matching.
    if (path.startsWith("tests/")) {
      continue;
    }
    const isUi =
      UI_PATH_PREFIXES.some((prefix) => path.startsWith(prefix)) ||
      UI_PATH_SUFFIXES.some((suffix) => path.endsWith(suffix)) ||
      path.includes("/wwwroot/");
    if (isUi) {
      matched.push(path);
    }
  }
  return [...new Set(matched)];
}

/**
 * Parses a PR title as a Conventional Commit subject.
 * @param {string} title
 * @returns {{ ok: boolean, type: string | null, scope: string | null, breaking: boolean, description: string | null, reason: string | null }}
 */
export function parseTitle(title) {
  const fail = (reason) => ({
    ok: false,
    type: null,
    scope: null,
    breaking: false,
    description: null,
    reason,
  });
  if (typeof title !== "string" || title.trim().length === 0) {
    return fail("Title is empty.");
  }
  const match = /^([a-z]+)(\(([^)]+)\))?(!)?: (.+)$/.exec(title);
  if (!match) {
    return fail(
      "Title must match `<type>(<scope>): <description>` — e.g. `fix(#2317): latch the route guard`."
    );
  }
  const [, type, , scope = null, bang, description] = match;
  if (!ALLOWED_TYPES.includes(type)) {
    return fail(
      `Unknown type \`${type}\`. Allowed: ${ALLOWED_TYPES.join(", ")}.`
    );
  }
  if (/\.$/.test(description)) {
    return fail("Description must not end with a period.");
  }
  // Reject a Sentence-cased opening word, but allow acronyms and CamelCase
  // identifiers (`CLI`, `SignalR`, `GatewayHost`) which are legitimately
  // capitalized. The distinguishing signal is a SECOND capital or a digit
  // somewhere in the word: `Correct` is prose, `SignalR`/`CLI` are identifiers.
  const firstWord = description.split(/\s+/)[0] ?? "";
  const isSentenceCased =
    /^[A-Z][a-z]+$/.test(firstWord.replace(/[^A-Za-z]/g, ""));
  if (isSentenceCased) {
    return fail(
      `Description should be lowercase imperative — got \`${firstWord}\`.`
    );
  }
  return {
    ok: true,
    type,
    scope,
    breaking: Boolean(bang),
    description,
    reason: null,
  };
}

/**
 * Strips HTML comments (the template's guidance blocks) so unfilled boilerplate
 * is never mistaken for a completed section.
 * @param {string} body
 * @returns {string}
 */
export function stripComments(body) {
  return String(body ?? "").replace(/<!--[\s\S]*?-->/g, "");
}

/**
 * Returns the set of markdown headings present in a PR body, lowercased.
 * @param {string} body
 * @returns {Set<string>}
 */
export function extractHeadings(body) {
  const headings = new Set();
  for (const line of stripComments(body).split(/\r?\n/)) {
    const match = /^#{1,4}\s+(.+?)\s*$/.exec(line);
    if (match) {
      headings.add(match[1].trim().toLowerCase());
    }
  }
  return headings;
}

/**
 * True when the body links an issue via a closing keyword or a bare `#N`.
 * @param {string} body
 * @returns {boolean}
 */
export function hasIssueLink(body) {
  return /\b(closes|fixes|resolves|refs)\s+#\d+/i.test(stripComments(body));
}

/**
 * True when the body carries screenshot/recording evidence: a markdown image,
 * an uploaded GitHub asset link, or a video/image file reference.
 * @param {string} body
 * @returns {boolean}
 */
export function hasVisualEvidence(body) {
  const text = stripComments(body);
  return (
    /!\[[^\]]*\]\([^)]+\)/.test(text) ||
    /https:\/\/(user-images\.githubusercontent\.com|github\.com\/[^\s)]*\/assets\/)/i.test(text) ||
    /<(img|video)\b/i.test(text) ||
    /\.(png|jpe?g|gif|webp|mp4|mov|webm)\b/i.test(text)
  );
}

/**
 * True when the author explicitly declared the UI change has no visible delta.
 * @param {string} body
 * @returns {boolean}
 */
export function hasNoVisibleChangeDeclaration(body) {
  return /\b(no visible (ui )?(change|delta)|not user[- ]visible|n\/a\s*[—-]\s*no rendered)/i.test(
    stripComments(body)
  );
}

/**
 * True when validation evidence is numeric (`Gateway.Tests 4026/0/1`) rather
 * than an unfalsifiable claim ("all tests pass").
 * @param {string} body
 * @returns {boolean}
 */
export function hasNumericEvidence(body) {
  return /\d+\s*\/\s*\d+/.test(stripComments(body));
}

/**
 * Heading sets required per change type. `docs`/`chore`/`style` PRs are not
 * asked for Tests; only `fix` is asked for a Root cause.
 * @param {string | null} type
 * @returns {string[]}
 */
export function requiredSections(type) {
  const base = ["summary", "changes", "validation", "risk & rollback"];
  if (type === "fix") {
    return [...base, "root cause", "tests"];
  }
  if (["docs", "chore", "style", "ci", "build"].includes(String(type))) {
    return base;
  }
  return [...base, "tests"];
}

/**
 * Evaluates every convention rule for a PR.
 * @param {{ title: string, body: string, changedPaths: string[] }} input
 * @returns {{ violations: {rule: string, message: string, advisory: boolean}[], uiPaths: string[] }}
 */
export function evaluate({ title, body, changedPaths }) {
  /** @type {{rule: string, message: string, advisory: boolean}[]} */
  const violations = [];
  const add = (rule, message, advisory = false) =>
    violations.push({ rule, message, advisory });

  const parsed = parseTitle(title);
  if (!parsed.ok) {
    add("title", `**Title** — ${parsed.reason}`);
  } else if (title.length > MAX_TITLE_LENGTH) {
    add(
      "title-length",
      `**Title** — ${title.length} chars, limit is ${MAX_TITLE_LENGTH}. It becomes the squash-commit subject.`
    );
  }

  if (!hasIssueLink(body)) {
    add(
      "issue-link",
      "**Issue link** — body must contain `Closes #N` (or `Refs #N`)."
    );
  }

  const headings = extractHeadings(body);
  for (const section of requiredSections(parsed.type)) {
    if (!headings.has(section)) {
      add(
        "sections",
        `**Missing section** — \`${section}\` is required for a \`${parsed.type ?? "?"}\` PR.`
      );
    }
  }

  const uiPaths = matchUiPaths(changedPaths);
  if (uiPaths.length > 0) {
    const declared = hasNoVisibleChangeDeclaration(body);
    if (!hasVisualEvidence(body) && !declared) {
      add(
        "ui-evidence",
        "**UI evidence** — this PR changes rendered UI (" +
          `${uiPaths.length} file(s)) but includes no screenshot or recording. ` +
          "Attach a capture of the new capability exercised with real generating agents and " +
          "live conversations — a still of an empty shell does not show that streaming or " +
          "incremental rendering works. If the change genuinely has no visible delta, say so explicitly."
      );
    }
  }

  if (!hasNumericEvidence(body)) {
    add(
      "evidence",
      '**Validation evidence** — give suite counts (`Gateway.Tests 4026/0/1`), not "all tests pass".',
      true
    );
  }

  return { violations, uiPaths };
}

/**
 * Parses a waiver comment body for a head-SHA-bound approval token.
 * @param {string} body
 * @returns {string | null} the lowercased SHA token, or null if absent/malformed
 */
export function parseApprovalSha(body) {
  if (typeof body !== "string" || body.length === 0) {
    return null;
  }
  // Anchor to a line start so the command cannot be smuggled inside prose.
  const re = new RegExp(
    `(?:^|\\n)\\s*${APPROVE_COMMAND.replace(/[.*+?^${}()|[\]\\]/g, "\\$&")}\\s+([0-9a-fA-F]{7,40})\\b`
  );
  const match = re.exec(body);
  return match ? match[1].toLowerCase() : null;
}

/**
 * True when `candidate` is a valid hex prefix of (or equal to) `headSha`.
 * @param {string} candidate
 * @param {string} headSha
 * @returns {boolean}
 */
export function shaMatchesHead(candidate, headSha) {
  if (!candidate || !headSha) {
    return false;
  }
  const head = headSha.toLowerCase();
  return head === candidate || head.startsWith(candidate);
}

/**
 * Main entry point invoked by actions/github-script.
 * @param {{ github: any, context: any, core: any }} args
 * @returns {Promise<void>}
 */
export async function run({ github, context, core }) {
  const pr = context.payload.pull_request;
  if (!pr) {
    core.info("No pull_request payload present; nothing to guard.");
    return;
  }

  const owner = context.repo.owner;
  const repo = context.repo.repo;
  const prNumber = pr.number;
  const headSha = String(pr.head?.sha ?? "").toLowerCase();
  const author = pr.user?.login ?? "";

  // 0. Automated external bot PRs are exempt (#2317).
  if (EXEMPT_AUTHORS.includes(author)) {
    core.info(`Author @${author} is an exempt automated bot. Guard skipped.`);
    core.summary
      .addHeading("PR conventions guard: SKIPPED", 3)
      .addRaw(`@${author} is an exempt automated bot author.`);
    await core.summary.write();
    return;
  }

  // 1. Compute the set of files this PR changes (via the API, never the tree).
  const changedFiles = await github.paginate(github.rest.pulls.listFiles, {
    owner,
    repo,
    pull_number: prNumber,
    per_page: 100,
  });
  const changedPaths = changedFiles.map((f) => f.filename);

  // 2. Evaluate the conventions. Title/body are inert text — matched, never run.
  const { violations, uiPaths } = evaluate({
    title: String(pr.title ?? ""),
    body: String(pr.body ?? ""),
    changedPaths,
  });

  if (violations.length === 0) {
    core.notice("PR conventions guard: all checks passed.");
    core.summary
      .addHeading("PR conventions guard: PASSED", 3)
      .addRaw(
        uiPaths.length > 0
          ? `UI surface touched (${uiPaths.length} file(s)) and visual evidence was supplied.`
          : "All required sections present."
      );
    await core.summary.write();
    return;
  }

  // 3. An authorized maintainer may waive violations for the current head SHA.
  const comments = await github.paginate(github.rest.issues.listComments, {
    owner,
    repo,
    issue_number: prNumber,
    per_page: 100,
  });

  let approved = false;
  let approver = null;
  for (const comment of comments) {
    const candidate = parseApprovalSha(comment.body);
    if (!candidate || !shaMatchesHead(candidate, headSha)) {
      continue;
    }
    const login = comment.user?.login;
    if (!login) {
      continue;
    }
    let permission = "none";
    try {
      const res = await github.rest.repos.getCollaboratorPermissionLevel({
        owner,
        repo,
        username: login,
      });
      permission = res.data?.permission ?? "none";
    } catch (err) {
      core.warning(`Could not resolve permission for ${login}: ${err}`);
      continue;
    }
    if (APPROVER_PERMISSIONS.includes(permission)) {
      approved = true;
      approver = login;
      break;
    }
    core.info(
      `${login} posted a waiver but has '${permission}' permission (need admin/maintain/write).`
    );
  }

  const blocking = violations.filter((v) => !v.advisory);
  const list = violations
    .map((v) => `- ${v.message}${v.advisory ? " _(advisory)_" : ""}`)
    .join("\n");

  if (approved) {
    core.notice(
      `PR convention violations waived by @${approver} for head ${headSha.slice(0, 12)}.`
    );
    core.summary
      .addHeading("PR conventions guard: WAIVED", 3)
      .addRaw(`Waived by @${approver} (head \`${headSha.slice(0, 12)}\`).\n\n`)
      .addRaw(list);
    await core.summary.write();
    return;
  }

  const guidance =
    `${violations.length} convention issue(s) found:\n\n${list}\n\n` +
    `See \`docs/development/pr-and-commit-conventions.md\`.\n\n` +
    `A maintainer (admin/maintain/write) may waive with:\n\n` +
    `    ${APPROVE_COMMAND} ${headSha}\n\n` +
    `The waiver is bound to head \`${headSha.slice(0, 12)}\` — pushing a new commit ` +
    `invalidates it.`;

  if (ENFORCEMENT_MODE === "warn") {
    core.warning(
      `PR conventions guard (warning-only): ${violations.length} issue(s). ${blocking.length} would block once enforcement is enabled.`
    );
    core.summary
      .addHeading("PR conventions guard: WARNING (not blocking)", 3)
      .addRaw(
        `The guard is in warning-first mode while the PR queue drains (#2317). ` +
          `${blocking.length} of these would block once enforcement is enabled.\n\n`
      )
      .addRaw(guidance.replace(/\n/g, "\n\n"));
    await core.summary.write();
    return;
  }

  if (blocking.length === 0) {
    core.warning("Only advisory convention issues found; not blocking.");
    core.summary
      .addHeading("PR conventions guard: ADVISORY", 3)
      .addRaw(list);
    await core.summary.write();
    return;
  }

  core.summary
    .addHeading("PR conventions guard: BLOCKED", 3)
    .addRaw(guidance.replace(/\n/g, "\n\n"));
  await core.summary.write();
  core.setFailed(guidance);
}

export default run;
