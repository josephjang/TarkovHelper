// Structural checks for an interactive code guide.
// Usage: node .agents/skills/code-guide/scripts/check-guide.mjs docs/<file>.html
//
// Covers points 1 to 3 of section 5 of references/workflow.md: the script
// parses, the file carries no forbidden character and no external request, and
// every id, rail link and segment control resolves.
import { readFileSync, writeFileSync, unlinkSync } from "node:fs";
import { execFileSync } from "node:child_process";
import { tmpdir } from "node:os";
import { join } from "node:path";

const path = process.argv[2];
if (!path) {
  console.error("usage: check-guide.mjs <guide.html>");
  process.exit(2);
}

const html = readFileSync(path, "utf8");
let failures = 0;
const fail = (m) => { failures++; console.log("FAIL " + m); };
const ok = (m) => console.log("ok   " + m);

/* ---- 1. the script block parses ---- */
const scriptMatch = html.match(/<script>([\s\S]*?)<\/script>/);
if (!scriptMatch) {
  fail("no <script> block found");
  process.exit(1);
}
const script = scriptMatch[1];

const tmp = join(tmpdir(), "code-guide-check-" + process.pid + ".js");
writeFileSync(tmp, script);
try {
  execFileSync(process.execPath, ["--check", tmp], { stdio: "pipe" });
  ok("script parses (" + script.split("\n").length + " lines)");
} catch (e) {
  fail("node --check failed:\n" + String(e.stderr || e.message));
} finally {
  try { unlinkSync(tmp); } catch { /* the check result is what matters */ }
}

/* ---- 2. forbidden characters and external requests ---- */
// The eyebrow's &middot; entity is the one established exception, and it is an
// entity rather than a literal character, so it cannot match here.
const forbidden = {
  "em dash": /—/g,
  "en dash": /–/g,
  "ellipsis character": /…/g,
  "Korean middle dot": /·/g,
};
for (const [name, re] of Object.entries(forbidden)) {
  const hits = html.match(re);
  if (hits) fail(name + ": " + hits.length + " occurrence(s)");
  else ok("no " + name);
}

// A guide must open from file://, so nothing may be fetched. Plain anchors to a
// tracker are text the reader clicks, not a request the page makes.
const external = [...html.matchAll(/(?:src|href)\s*=\s*"(?:https?:)?\/\/[^"]*"/g)]
  .map((m) => m[0])
  .filter((a) => !a.startsWith('href="http'));
if (external.length) fail("external asset reference: " + external.join(", "));
else ok("no external asset requests");

/* ---- 3. ids, rail links, segments ---- */
const markupIds = new Set([...html.matchAll(/\bid="([^"]+)"/g)].map((m) => m[1]));

const jsIds = [...new Set([...script.matchAll(/getElementById\("([^"]+)"\)/g)].map((m) => m[1]))];
const missing = jsIds.filter((id) => !markupIds.has(id));
if (missing.length) fail("getElementById targets absent from markup: " + missing.join(", "));
else ok(jsIds.length + " getElementById targets all present");

const railHrefs = [...html.matchAll(/<a href="#([^"]+)"/g)].map((m) => m[1]);
const badLinks = railHrefs.filter((h) => !markupIds.has(h));
if (badLinks.length) fail("rail links with no target: " + badLinks.join(", "));
else ok(railHrefs.length + " rail links all resolve");

const sectionIds = [...html.matchAll(/<(?:section|div class="lab") id="([^"]+)"/g)].map((m) => m[1]);
const orphans = sectionIds.filter((s) => !railHrefs.includes(s));
if (orphans.length) fail("sections not linked from the rail: " + orphans.join(", "));
else ok(sectionIds.length + " sections all linked from the rail");

const segRoots = [...script.matchAll(/bindSegment\(document\.getElementById\("([^"]+)"\),\s*"([^"]+)"/g)];
for (const [, rootId, attr] of segRoots) {
  const re = new RegExp(rootId + '"[\\s\\S]{0,800}?' + attr + "=");
  if (!re.test(html)) fail("bindSegment(" + rootId + ", " + attr + ") finds no buttons");
}
if (segRoots.length) ok(segRoots.length + " bindSegment roots have buttons");

for (const seg of html.matchAll(/<div class="seg" id="([^"]+)">([\s\S]*?)<\/div>/g)) {
  const pressed = (seg[2].match(/aria-pressed="true"/g) || []).length;
  if (pressed !== 1) fail("segment " + seg[1] + " has " + pressed + ' aria-pressed="true" defaults');
}
ok("segment defaults checked");

console.log(failures === 0 ? "\nSTRUCTURAL CHECKS PASSED" : "\n" + failures + " CHECK(S) FAILED");
process.exit(failures === 0 ? 0 : 1);
