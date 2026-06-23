// @ts-check
/**
 * Security-sensitive boundary-file guard.
 *
 * Blocks a pull request that modifies any file on the curated sensitive list
 * (e.g. `.gitignore`, which governs whether `.env` / secret files are ignored
 * before they can be accidentally committed) unless an authorized maintainer
 * posts a head-SHA-bound `/allow-security-sensitive-change <sha>` approval.
 *
 * SAFETY MODEL (why this is safe to run on `pull_request_target`):
 *   - The workflow checks out ONLY the trusted base-branch copy of this script
 *     (`ref: base.sha`, `persist-credentials: false`). It never executes any
 *     code from the PR head, so an attacker cannot rewrite the guard in their PR.
 *   - The set of changed files is read from the GitHub API (compare base..head),
 *     not from a working tree, so no PR-head content is sourced.
 *   - Approval is bound to the CURRENT head SHA. A later push to the PR
 *     invalidates a prior approval (no approve-then-sneak-a-commit).
 *   - Approval is only honored from users with `admin`, `maintain`, or `write`
 *     repository permission.
 *
 * This module exports a single `run({ github, context, core })` function so it
 * can be invoked from `actions/github-script` and unit-tested in isolation.
 */

/**
 * Files (exact paths) and path-prefixes considered security-sensitive boundary
 * surfaces. A PR touching any of these requires an explicit maintainer ack.
 */
export const SENSITIVE_EXACT = Object.freeze([
  ".gitignore",
  ".gitattributes",
  ".github/CODEOWNERS",
  ".github/scripts/security-sensitive-guard.mjs",
  ".github/workflows/security-sensitive-guard.yml",
]);

/**
 * Path prefixes considered security-sensitive. Any changed file whose path
 * starts with one of these requires the maintainer ack.
 */
export const SENSITIVE_PREFIXES = Object.freeze([
  ".github/workflows/",
]);

/** The comment command an authorized maintainer posts to approve. */
export const APPROVE_COMMAND = "/allow-security-sensitive-change";

/** Repository permission levels allowed to approve a sensitive change. */
const APPROVER_PERMISSIONS = Object.freeze(["admin", "maintain", "write"]);

/**
 * Returns the subset of changed paths that are security-sensitive.
 * @param {string[]} changedPaths
 * @returns {string[]}
 */
export function matchSensitivePaths(changedPaths) {
  const matched = [];
  for (const path of changedPaths) {
    const normalized = path.replace(/\\/g, "/").replace(/^\.\//, "");
    if (SENSITIVE_EXACT.includes(normalized)) {
      matched.push(normalized);
      continue;
    }
    if (SENSITIVE_PREFIXES.some((prefix) => normalized.startsWith(prefix))) {
      matched.push(normalized);
    }
  }
  // De-duplicate while preserving order.
  return [...new Set(matched)];
}

/**
 * Parses an approval comment body for a head-SHA-bound approval token.
 * Accepts `/allow-security-sensitive-change <sha>` where `<sha>` is a 7-40 char
 * hex prefix of the head SHA. The command must appear at the start of a line.
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
 * @param {string} candidate lowercased hex prefix from the approval comment
 * @param {string} headSha the current PR head SHA (full, lowercased)
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

  // 1. Compute the set of files this PR changes (via the API, never the tree).
  const changedFiles = await github.paginate(github.rest.pulls.listFiles, {
    owner,
    repo,
    pull_number: prNumber,
    per_page: 100,
  });
  const changedPaths = changedFiles.map((f) => f.filename);
  const sensitive = matchSensitivePaths(changedPaths);

  if (sensitive.length === 0) {
    core.info("No security-sensitive boundary files changed. Guard passes.");
    return;
  }

  core.info(
    `PR #${prNumber} touches ${sensitive.length} security-sensitive file(s): ${sensitive.join(", ")}`
  );

  // 2. Look for a head-SHA-bound approval from an authorized maintainer.
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
    // Verify the commenter currently has approver-level repo permission.
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
      `${login} posted an approval but has '${permission}' permission (need admin/maintain/write).`
    );
  }

  // 3. Surface the result. Fail the check when not approved.
  const fileList = sensitive.map((f) => `- \`${f}\``).join("\n");
  if (approved) {
    core.notice(
      `Security-sensitive change approved by @${approver} for head ${headSha.slice(0, 12)}.`
    );
    core.summary
      .addHeading("Security-sensitive file guard: APPROVED", 3)
      .addRaw(`Approved by @${approver} (head \`${headSha.slice(0, 12)}\`).\n\n`)
      .addRaw(`Sensitive files changed:\n${fileList}\n`);
    await core.summary.write();
    return;
  }

  const instructions =
    `This PR modifies security-sensitive boundary files:\n${fileList}\n\n` +
    `These files (e.g. \`.gitignore\`, which controls whether secret/\`.env\` files are ` +
    `ignored, and the CI workflows themselves) require an explicit maintainer ack.\n\n` +
    `An authorized maintainer (admin/maintain/write) must comment:\n\n` +
    `    ${APPROVE_COMMAND} ${headSha}\n\n` +
    `The approval is bound to the current head SHA \`${headSha.slice(0, 12)}\` — ` +
    `pushing a new commit invalidates it and requires a fresh ack.`;

  core.summary
    .addHeading("Security-sensitive file guard: BLOCKED", 3)
    .addRaw(instructions.replace(/\n/g, "\n\n"));
  await core.summary.write();
  core.setFailed(instructions);
}

export default run;
