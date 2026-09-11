# Existing GitHub write-tool contracts

This is a partial implementation of [#2735](https://github.com/Sytone/botnexus/issues/2735),
not delivery of the complete write-tool feature. The existing `github_issue_comment`
tool posts both issue and pull-request conversation comments through
`POST repos/{repository}/issues/{number}/comments`. It does not use GraphQL.

## Failure diagnostics

Tools using `GitHubToolBase.ErrorResult` return structured failure fields:
`tool`, `repository`, `identity` (when configured), `ok: false`, `status`, and `error`.
The `identity` value comes from `GitHubToolsConfig.Identity`, just as it does in the
existing comment success result. It is a **human-readable configured label**, not a
verified authenticated GitHub login, and can be unset. No identity is inferred from
a token. A 404 can mean a missing repository or inaccessible resource; this
projection preserves the status and does not claim to distinguish those causes.

This additive diagnostic also applies to read tools sharing the common projection.
It does not change credential resolution, schemas, request transport, or the
separate `github_api` escape-hatch result contract. No token, request headers, or
comment request body is added to the failure projection. The API client's existing
message projection remains unchanged; this is not general-purpose redaction of
arbitrary server messages or misconfigured labels.

## Regression coverage

`GitHubWriteToolContractTests` uses the existing `RecordingGitHubApiClient` and
`GitHubFixtures` to exercise the actual comment tool through argument preparation
and execution. `CommentWrite_UsesPostRestCommentsEndpoint_NotGraphQL` pins the HTTP
verb, complete REST path, single request, body shape, default/explicit repository,
and success identity label. Its exact path assertion rejects a `graphql` route.

`CommentWrite_AccessDenied_NamesRepositoryAndConfiguredIdentity_WithoutCredentials`
uses the real `HttpGitHubApiClient`, `CachedGitHubCredentialProvider`, and existing
`CountingTokenSource` with a transport-only handler. It covers 403 and 404, default
and explicit repositories, and distinct configured labels. It verifies credential
attachment actually occurred, exactly one request was made, no success comment is
returned, and neither the synthetic credential nor the private request body is
emitted. A synthetic credential in an unrelated raw response field also must not
be echoed. Caller-supplied identity/token arguments do not select either value.

Existing `GitHubToolSurfaceTests` schema and result/error credential assertions
remain unchanged. Tests must run remotely; a local build is compilation only.

## Executed mutation evidence

Remote core run `20260906091433-86817a3a` changed only the production comment
call's REST path to `graphql`, retaining its HTTP verb and payload. Both cases of
`CommentWrite_UsesPostRestCommentsEndpoint_NotGraphQL` failed on the actual recorded
path, alongside the existing
`GitHubToolsTests.IssueComment_PostsToTheRestCommentEndpoint_NotGraphQl`.
The GitHub project reported 125 passed / 3 failed; the core aggregate reported
18,532 total, 18,494 executed, 18,491 passed, 3 failed, 38 skipped, and zero fixture
failures (`isComplete: false`, reason `test-failures`). There were no collateral
failures. The original production file was restored byte-for-byte after the run;
the mutant is not part of the delivered diff. This establishes clause 6 for the
existing writer, not a live GraphQL or EMU service integration test.

The earlier pre-fix run `20260906081836-506ed3ca` failed all four new 403/404 cases
specifically on the missing `identity` field. With the projection fix,
`20260906083116-e7d76771` passed all 128 GitHub tests. A fresh clean core gate is
required after restoring the route and staging this documentation; its receipt
belongs in the PR validation record.

## Acceptance boundaries

| #2735 clause | This slice | Remaining work |
| --- | --- | --- |
| 1: five registered write tools | Existing comment tool only | `github_issue_create`, `github_issue_update`, `github_pr_create`, and `github_labels` remain absent. |
| 2: REST comment request | Named request-seam regression | No claim about absent tools or a live EMU deployment. |
| 3: no caller-selected credential/identity | Existing schema assertions retained; caller override behavior checked | Re-audit new tools when introduced. |
| 4: no credential in results/errors | Existing assertions retained; comment denial cases added | No universal sanitization claim for arbitrary remote text. |
| 5: repository/identity-bearing access denial | Configured-label diagnostics for the existing comment writer | No verified-login claim; absent tools and unset labels are not covered by a stronger identity guarantee. |
| 6: GraphQL mutation reddens named test | Actual production-route mutant failed both named REST cases, with one related existing test failure | No remaining mutation-evidence gap for the existing writer; absent tools remain outside this slice. |

The four missing tools, contributor registration, and extension overview are outside
this change. Keep #2735 open and use `Refs #2735`, not an automatic closing link.
