import { readFileSync, readdirSync, writeFileSync } from "node:fs";
import { fileURLToPath, pathToFileURL } from "node:url";
import { usability } from "./contributor-usability.mjs";

const root = new URL("../", import.meta.url);
const start = "<!-- BEGIN GENERATED CONTRIBUTOR USABILITY -->";
const end = "<!-- END GENERATED CONTRIBUTOR USABILITY -->";

// The only source for shared prompts is the versioned repository contract.
export function generatedFiles() {
  const files = new Map();
  const pr = new URL("pull_request_template.md", root);
  const original = readFileSync(pr, "utf8").replace(/\r\n/g, "\n");
  const block = `${start}\n## ${usability.section}\n\n<!-- Advisory checklist v${usability.version}. Fill the labelled lines; remove this comment.\n${usability.clauses.map(c => `${c.label}: ${c.prompt}`).join("\n")}\n${usability.docsOnly}\n-->\n\n${usability.clauses.map(c => `${c.label}:`).join("\n\n")}\n${end}`;
  const replaceBlock = (text, replacement) => {
    const first = text.indexOf(start);
    const last = text.indexOf(end, first);
    if (first !== -1 && last !== -1) return text.slice(0, first) + replacement + text.slice(last + end.length);
    return `${text.trimEnd()}\n\n${replacement}\n`;
  };
  files.set(pr, replaceBlock(original, block));
  const forms = new URL("ISSUE_TEMPLATE/", root);
  const description = [...usability.clauses.map(c => `${c.label}: ${c.prompt}`), usability.docsOnly].join(" ");
  for (const file of readdirSync(forms).filter(name => /^\d.*\.yml$/.test(name))) {
    const url = new URL(file, forms);
    const text = readFileSync(url, "utf8").replace(/\r\n/g, "\n");
    const formBlock = `  # ${start}\n  - type: textarea\n    id: contributor_usability\n    attributes:\n      label: '${usability.section}'\n      description: '${description.replaceAll("'", "''")}'\n    validations:\n      required: false\n  # ${end}`;
    // YAML comments keep markers inert; replace the whole generated item.
    const first = text.indexOf(`  # ${start}`);
    const last = text.indexOf(end, first);
    files.set(url, first !== -1 && last !== -1 ? text.slice(0, first) + formBlock + text.slice(last + end.length) : `${text.trimEnd()}\n${formBlock}\n`);
  }
  return files;
}

if (process.argv[1] && pathToFileURL(process.argv[1]).href === import.meta.url) {
  const check = process.argv.includes("--check");
  let stale = false;
  for (const [url, expected] of generatedFiles()) {
    if (readFileSync(url, "utf8") === expected) continue;
    if (check) { console.error(`Stale contributor guidance: ${fileURLToPath(url)}; run node .github/scripts/sync-contributor-usability.mjs`); stale = true; }
    else writeFileSync(url, expected);
  }
  if (stale) process.exitCode = 1;
}
