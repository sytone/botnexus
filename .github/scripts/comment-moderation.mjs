// @ts-check
/**
 * Comment moderation guard.
 *
 * Restricts who may leave a durable comment on an issue or pull request in this
 * repository to a CLOSED allow-list: the maintainer (`sytone`) and the platform
 * agent (`agent-farnsworth[bot]`). Any comment from another author is minimized
 * as spam immediately after it is posted.
 *
 * WHY THIS EXISTS (and why it is not merely spam hygiene):
 *   The autonomous maintenance loop reads issue bodies, PR bodies and comments.
 *   Every trust decision in this repository is AUTHOR-KEYED - the
 *   `/allow-security-sensitive-change` ack, the `/allow-pr-convention-exception`
 *   waiver, and the agent-side `Get-TrustedGitHubContent.ps1` filter all classify
 *   by author login against a closed list. A third-party comment is therefore a
 *   prompt-injection surface: it cannot forge an author, but it can sit in a
 *   thread and be read as context by every human and agent that later loads it.
 *   Minimizing it at write time removes the payload rather than relying on every
 *   downstream reader to filter it.
 *
 * WHAT THIS CANNOT DO:
 *   GitHub Actions cannot PREVENT a comment - the comment is created before any
 *   workflow can fire. Prevention is only available as a repository interaction
 *   limit (Settings -> Moderation -> Interaction limits), which expires after at
 *   most 6 months and leaves no code trail. This workflow is the durable,
 *   reviewable half of a two-part control; see
 *   docs/development/comment-moderation.md for the other half.
 *
 * SAFETY MODEL:
 *   - Runs on `issue_comment` / `pull_request_review_comment` /
 *     `pull_request_review`, never checks out or executes ANY repository or PR
 *     content. There is no `actions/checkout` of a head ref anywhere in the path.
 *   - The comment BODY is never parsed, matched or echoed. Only the author login
 *     and the comment node id are read, both from the trusted event payload.
 *   - The allow-list is exact-match after case folding. No prefix, suffix or
 *     substring matching, so `sytone-attacker`, `sytone[bot]` and
 *     `agent-farnsworth-evil` all correctly reject.
 *
 * This module exports pure functions plus a `run({ github, context, core })`
 * entry point so it can be invoked from `actions/github-script` and unit-tested
 * in isolation.
 */

/**
 * Logins permitted to comment. Closed, exact-match (case-insensitive, matching
 * GitHub's own login semantics) - never a prefix or substring test.
 *
 * Adding an entry here is a security-boundary change: this file is listed in
 * `SENSITIVE_EXACT` in security-sensitive-guard.mjs and in CODEOWNERS, so it
 * requires a maintainer ack on the PR.
 */
export const ALLOWED_COMMENT_AUTHORS = Object.freeze([
  "sytone",
  "agent-farnsworth[bot]",
]);

/**
 * How a disallowed comment is handled.
 *
 * `minimize` hides the comment behind a "marked as spam" fold via the GraphQL
 * `minimizeComment` mutation. It is the DEFAULT because it is reversible and
 * leaves an audit trail: a false positive can be un-minimized, whereas a deleted
 * comment is unrecoverable and a false positive becomes invisible to everyone
 * including the maintainer. Set to `delete` only with a deliberate decision.
 *
 * @type {"minimize" | "delete"}
 */
export const MODERATION_ACTION = "minimize";

/**
 * True when `login` is on the allow-list.
 *
 * Comparison is exact after case folding. GitHub logins are case-insensitive for
 * identity purposes (`SYTONE` and `sytone` are the same account), so folding is
 * correct; anything beyond folding - trimming a `[bot]` suffix, stripping
 * punctuation, prefix matching - would open the spoofing hole this list exists to
 * close.
 *
 * @param {unknown} login
 * @returns {boolean}
 */
export function isAllowedAuthor(login) {
  if (typeof login !== "string" || login.length === 0) {
    return false;
  }
  const normalized = login.toLowerCase();
  return ALLOWED_COMMENT_AUTHORS.some((allowed) => allowed.toLowerCase() === normalized);
}

/**
 * @typedef {object} ModerationTarget
 * @property {string} kind        human-readable event kind, for logging
 * @property {string} nodeId      GraphQL node id of the comment/review
 * @property {string} login       author login
 * @property {string} htmlUrl     link to the comment, for the job summary
 * @property {number | null} issueNumber owning issue/PR number when known
 */

/**
 * Extracts the moderation target from a webhook payload, normalizing the three
 * event shapes into one record.
 *
 * `issue_comment` and `pull_request_review_comment` both carry `payload.comment`;
 * `pull_request_review` carries `payload.review` instead. Returning `null` for an
 * unrecognised or incomplete payload makes the caller fail SAFE (no moderation)
 * rather than acting on a shape it does not understand.
 *
 * @param {string} eventName
 * @param {any} payload
 * @returns {ModerationTarget | null}
 */
export function extractTarget(eventName, payload) {
  if (!payload || typeof payload !== "object") {
    return null;
  }

  /** @type {any} */
  let node = null;
  let kind = "";

  if (eventName === "issue_comment") {
    node = payload.comment;
    kind = "issue/PR comment";
  } else if (eventName === "pull_request_review_comment") {
    node = payload.comment;
    kind = "PR review comment";
  } else if (eventName === "pull_request_review") {
    node = payload.review;
    kind = "PR review";
  } else {
    return null;
  }

  if (!node || typeof node !== "object") {
    return null;
  }

  const nodeId = node.node_id;
  const login = node.user?.login;
  if (typeof nodeId !== "string" || nodeId.length === 0) {
    return null;
  }
  if (typeof login !== "string" || login.length === 0) {
    return null;
  }

  const issueNumber =
    payload.issue?.number ?? payload.pull_request?.number ?? null;

  return {
    kind,
    nodeId,
    login,
    htmlUrl: typeof node.html_url === "string" ? node.html_url : "",
    issueNumber: typeof issueNumber === "number" ? issueNumber : null,
  };
}

/** GraphQL mutation that folds a comment away as spam. */
const MINIMIZE_MUTATION = `
  mutation MinimizeAsSpam($id: ID!) {
    minimizeComment(input: { subjectId: $id, classifier: SPAM }) {
      minimizedComment { isMinimized minimizedReason }
    }
  }
`;

/**
 * Main entry point invoked by actions/github-script.
 * @param {{ github: any, context: any, core: any }} args
 * @returns {Promise<void>}
 */
export async function run({ github, context, core }) {
  const target = extractTarget(context.eventName, context.payload);

  if (!target) {
    // Unrecognised or incomplete payload: do nothing. Failing safe here is
    // deliberate - moderating a shape we cannot parse risks hiding the wrong
    // thing, which is strictly worse than leaving a comment for a human.
    core.info(
      `No moderatable comment found in '${context.eventName}' payload; nothing to do.`
    );
    return;
  }

  if (isAllowedAuthor(target.login)) {
    core.info(`${target.kind} by @${target.login} is allow-listed. No action.`);
    return;
  }

  core.warning(
    `${target.kind} by @${target.login} is not on the comment allow-list ` +
      `(${ALLOWED_COMMENT_AUTHORS.join(", ")}). Applying '${MODERATION_ACTION}'.`
  );

  let outcome = "";
  try {
    if (MODERATION_ACTION === "delete") {
      await github.graphql(
        `mutation Delete($id: ID!) { deleteIssueComment(input: { id: $id }) { clientMutationId } }`,
        { id: target.nodeId }
      );
      outcome = "deleted";
    } else {
      await github.graphql(MINIMIZE_MUTATION, { id: target.nodeId });
      outcome = "minimized as spam";
    }
  } catch (err) {
    // A moderation failure must be LOUD. A silently-failing guard is worse than
    // no guard, because the repository looks protected when it is not.
    core.setFailed(
      `Failed to ${MODERATION_ACTION} ${target.kind} by @${target.login}: ${err}`
    );
    return;
  }

  const where = target.issueNumber ? `#${target.issueNumber}` : "(unknown thread)";
  core.notice(
    `${target.kind} by @${target.login} on ${where} was ${outcome}.`
  );
  core.summary
    .addHeading("Comment moderation: ACTION TAKEN", 3)
    .addRaw(
      `A ${target.kind} by \`@${target.login}\` on ${where} was **${outcome}**.\n\n` +
        `Only \`${ALLOWED_COMMENT_AUTHORS.join("` and `")}\` may comment on this ` +
        `repository. See \`docs/development/comment-moderation.md\`.\n`
    );
  await core.summary.write();
}

export default run;
