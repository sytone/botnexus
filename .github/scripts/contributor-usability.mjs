import { readFileSync } from "node:fs";

const contract = JSON.parse(readFileSync(new URL("../pr-contract.json", import.meta.url), "utf8"));
export const usability = contract.contributorUsability;

// Deliberately a small labelled-field checklist, not a natural-language verifier.
export function evaluateUsability(visibleBody, changedPaths = []) {
  const heading = usability.section.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
  const section = new RegExp(`^#{1,4}\\s+${heading}\\s*\\r?\\n([\\s\\S]*?)(?=^#{1,4}\\s+|(?![\\s\\S]))`, "im").exec(visibleBody)?.[1]?.trim() ?? "";
  const docsOnly = changedPaths.length > 0 && changedPaths.every(raw => {
    const path = String(raw).replace(/\\/g, "/").replace(/^\.\//, "");
    return /\.md$/i.test(path) || /^(?:docs\/.*\.(?:mdx|png|jpe?g|svg|webp)|mkdocs\.yml)$/i.test(path);
  });
  const rationale = /^Docs-only:\s*(.+)$/im.exec(section)?.[1] ?? "";
  if (docsOnly && rationale.length >= 30 && !/\b(?:TODO|TBD|N\/A)\b/i.test(rationale)) return [];

  const values = Object.fromEntries(usability.clauses.map(clause => {
    const line = section.split(/\r?\n/).find(line => line.toLowerCase().startsWith(`${clause.label.toLowerCase()}:`));
    const value = line?.slice(clause.label.length + 1).trim() ?? "";
    return [clause.id, contract.placeholderPatterns.some(pattern => new RegExp(pattern, "i").test(value)) ? "" : value];
  }));
  const consumers = values.consumers ?? "";
  const setup = values.setup ?? "";
  const validation = values.validation ?? "";
  const windows = /\bWindows\b/i.test(consumers);
  const linux = /\bLinux\b/i.test(consumers);
  const scoped = /\b(?:Windows|Linux)[ -]only\b/i.test(consumers);
  const commandResults = /`[^`\r\n]+`[^;\r\n]*\d+\s+(?:passed|pass)\s*\/\s*\d+\s+(?:failed|fail)/i;
  // A command/result pair belongs only to its platform-labelled segment.
  const platformSegments = [...validation.matchAll(/\b(Windows|Linux):\s*([\s\S]*?)(?=\b(?:Windows|Linux):|$)/gi)];
  const platformEvidence = platform => platformSegments.some(([, label, evidence]) =>
    label.toLowerCase() === platform.toLowerCase() && new RegExp(`^${commandResults.source}`, "i").test(evidence));
  const checks = {
    consumers: consumers.replace(/\b(?:platforms?|Windows|Linux|and|only|portable|everyone|users|consumers|all)\b/gi, "").replace(/[^a-z0-9]/gi, "").length >= 8 && (windows || linux),
    setup: /\b(?:setup|configur\w*|install\w*)\b/i.test(setup) && /\bprerequisites?\b/i.test(setup) && /\bdocs\/[\w./-]+\.md\b/.test(setup),
    validation: /\b(?:clean|hermetic)\b/i.test(validation) && /\b(?:temporary|isolated|fixture\w*|fresh)\b/i.test(validation) && commandResults.test(validation),
    private: /^none\.?$/i.test(values.private ?? "") && !/(?:[a-z]:[\\/]Users[\\/]|\/(?:home|Users)\/[^\s/]+\/|\bprivate skills?\b|\bpre[- ]?seeded\s+(?:database|db)\b|\b(?:only|alone)[^\n]*(?:redact|paths?)[^\n]*(?:redact|paths?)|\bpath[- ]redaction[- ]only\b)/i.test(section),
  };
  const violations = [];
  const add = (id, failure, remediation) => violations.push({ rule: `contributor-usability-${id}`, advisory: true, message: `**Contributor usability / ${id}** — ${failure} Remediation: ${remediation}` });
  for (const clause of usability.clauses) if (!checks[clause.id]) add(clause.id, clause.failure, `${clause.label}: ${clause.prompt}`);
  if (!(scoped && windows !== linux && /\b(?:Windows|Linux)[ -]only\b/i.test(setup) && platformEvidence(windows ? "Windows" : "Linux")) && !(windows && linux && !scoped && platformEvidence("Windows") && platformEvidence("Linux"))) {
    add("platforms", usability.platforms.failure, usability.platforms.remediation);
  }
  return violations;
}
