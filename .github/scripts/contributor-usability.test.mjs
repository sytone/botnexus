import { test } from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { evaluate, run, requiredSections } from "./pr-conventions-guard.mjs";
import { usability } from "./contributor-usability.mjs";

const valid = `## Contributor usability
Consumers: extension authors configuring Example; platforms: Windows and Linux.
Setup: public prerequisites and configuration documented in docs/user-guide/example.md.
Validation: clean temporary home, hermetic fixtures; Windows: \`node example.mjs\` 4 passed / 0 failed; Linux: \`node example.mjs\` 4 passed / 0 failed.
Private prerequisites: none.
`;
const findings = (body, changedPaths = ["src/extensions/Example/config.json"], title = "feat(config): add reusable configuration") =>
  evaluate({ title, body, changedPaths }).violations.filter(v => v.rule.startsWith("contributor-usability"));

for (const [name, body] of [["missing", ""], ["empty", "## Contributor usability\n"], ["vague", "## Contributor usability\nWorks for others."]]) {
  test(`${name} usability section reports actionable advisory clauses`, () => {
    const result = findings(body);
    assert.ok(result.length >= 4);
    for (const v of result) { assert.equal(v.advisory, true); assert.match(v.message, /Remediation:/); }
  });
}
test("portable reusable extension/config has both platform results", () => assert.deepEqual(findings(valid), []));
for (const [missing, successful] of [["Windows", "Linux"], ["Linux", "Windows"]]) {
  for (const missingFirst of [true, false]) {
    test(`comma-separated labels cannot borrow ${successful} results for ${missing} (${missingFirst ? "missing first" : "missing last"})`, () => {
      const absent = `${missing}: \`node example.mjs\` not run`;
      const executed = `${successful}: \`node example.mjs\` 4 passed / 0 failed`;
      const evidence = (missingFirst ? [absent, executed] : [executed, absent]).join(", ");
      const body = valid.replace(/^Validation:.*$/m, `Validation: clean temporary home; ${evidence}.`);
      assert.ok(findings(body).some(v => v.rule === "contributor-usability-platforms"));
    });
  }
}
for (const platforms of [["Windows", "Linux"], ["Linux", "Windows"]]) {
  test(`comma-separated successful results remain portable (${platforms.join(" first, ")})`, () => {
    const evidence = platforms.map(platform => `${platform}: \`node example.mjs\` 4 passed / 0 failed`).join(", ");
    assert.deepEqual(findings(valid.replace(/^Validation:.*$/m, `Validation: clean temporary home; ${evidence}.`)), []);
  });
}
test("usability heading follows the contract literally, including regex metacharacters", { concurrency: false }, async () => {
  const { generatedFiles } = await import("./sync-contributor-usability.mjs");
  const original = usability.section;
  // No awaits while shared contract state is changed; always restore it for later tests.
  try {
    usability.section = "Contributor usability (public) [v2].+";
    const renamed = valid.replace(`## ${original}`, `## ${usability.section}`);
    const generated = [...generatedFiles()].find(([url]) => url.href.endsWith("/pull_request_template.md"))?.[1];
    assert.ok(generated.includes(`## ${usability.section}\n`));
    assert.deepEqual(findings(renamed), []);
    assert.ok(findings(valid).length > 0);
    assert.ok(findings(renamed.replace(usability.section, "Contributor usability public v2xx")).length > 0);
  } finally {
    usability.section = original;
  }
});
test("Windows-only capability is allowed with documented scope", () => {
  const scoped = valid.replace("Windows and Linux", "Windows only").replace("public prerequisites", "Windows-only limitation, public prerequisites").replace(/; Linux:[^\n]+/, ".");
  assert.deepEqual(findings(scoped), []);
});
test("portable claim without Linux execution is advisory", () => {
  assert.ok(findings(valid.replace(/; Linux:[^\n]+/, ".")).some(v => v.rule.endsWith("platforms")));
});
for (const [name, replacement] of [
  ["private skills", "requires my private skills"],
  ["personal Windows path", "requires C:\\Users\\author\\settings.json"],
  ["personal Linux path", "requires " + ["", "home", "author", "settings.json"].join("/")],
  ["preseeded database", "requires a preseeded database"],
  ["redaction only", "only personal paths were redacted; works for others"],
]) {
  test(`${name} is rejected even alongside a success claim`, () => {
    const result = findings(valid.replace("Private prerequisites: none.", `Private prerequisites: none. ${replacement}; works for others.`));
    assert.ok(result.some(v => v.rule.endsWith("private")));
  });
}
for (const [name, before, after] of [
  ["unnamed consumer", "extension authors configuring Example", "everyone"],
  ["missing user docs", "docs/user-guide/example.md", "later"],
  ["author-home validation", "clean temporary home, hermetic fixtures", "existing author home"],
  ["no command", "`node example.mjs`", "ran it"],
  ["no numeric results", "4 passed / 0 failed", "passed"],
  ["placeholder", "public prerequisites", "TODO public prerequisites"],
]) {
  test(`${name} is not sufficient evidence`, () => assert.ok(findings(valid.replaceAll(before, after)).length > 0));
}
test("docs-only rationale uses changed paths, never the title", () => {
  const body = "## Contributor usability\nDocs-only: clarify an existing explanation; no runtime setup changes.\n";
  assert.deepEqual(findings(body, ["docs/development/example.md"], "chore: clarify wording"), []);
  assert.ok(findings(body, ["src/Example.cs"], "docs: clarify wording").length > 0);
  assert.ok(findings(body, []).length > 0);
  assert.ok(findings(body, ["docs/.vitepress/config.mts"]).length > 0);
  assert.ok(findings(body.replace("clarify an existing explanation; no runtime setup changes.", "N/A"), ["README.md"]).length > 0);
});
test("hidden guidance and adjacent sections do not satisfy usability", () => {
  assert.ok(findings(`<!-- ${valid} -->`).length > 0);
  assert.ok(findings(`## Contributor usability\n## Merge notes\n${valid.split("\n").slice(1).join("\n")}`).length > 0);
});
test("new section stays outside blocking requiredSections", () => assert.equal(requiredSections("feat").includes("contributor usability"), false));
test("generated PR and all public issue forms match the versioned contract", async () => {
  const { generatedFiles } = await import("./sync-contributor-usability.mjs");
  const files = generatedFiles();
  assert.equal(files.size, 10);
  for (const [url, expected] of files) assert.equal(readFileSync(url, "utf8"), expected, String(url));
});
test("usability findings alone do not fail the guard", async () => {
  const sections = requiredSections("feat").map(s => `## ${s}\nVerified content; Refs #3894; 1/0.`).join("\n");
  let failed = false;
  let warnings = 0;
  const summary = { addHeading() { return this; }, addRaw() { return this; }, async write() {} };
  await run({
    github: { paginate: async () => [], rest: { pulls: { listFiles() {} }, issues: { listComments() {} } } },
    context: { repo: { owner: "test", repo: "test" }, payload: { pull_request: { number: 1, title: "feat(config): add configuration", body: sections, head: { sha: "abc1234" }, user: { login: "contributor" } } } },
    core: { summary, info() {}, notice() {}, warning() { warnings++; }, setFailed() { failed = true; } },
  });
  assert.equal(failed, false);
  assert.equal(warnings, 1);
});

test("setup must disclose prerequisites as well as configuration and user docs", () => {
  assert.ok(findings(valid.replace("public prerequisites and configuration", "configuration")).some(v => v.rule.endsWith("setup")));
});
test("single-platform scope needs the limitation in the documentation declaration", () => {
  assert.ok(findings(valid.replace("Windows and Linux", "Windows only")).some(v => v.rule.endsWith("platforms")));
});
test("Linux-only scope has the same documented evidence requirements", () => {
  const body = valid.replace("Windows and Linux", "Linux only").replace("public prerequisites", "Linux-only limitation, public prerequisites").replace(/Windows:.*?; Linux:/, "Linux:");
  assert.deepEqual(findings(body), []);
});
test("unfilled generated PR template remains advisory", () => {
  assert.ok(findings(readFileSync(new URL("../pull_request_template.md", import.meta.url), "utf8")).length >= 4);
});
