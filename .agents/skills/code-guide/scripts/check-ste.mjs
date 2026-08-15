// ASD-STE100 writing-rule checks over the reader-facing English of a code guide.
// Usage: node .agents/skills/code-guide/scripts/check-ste.mjs docs/<file>.html
//
// SCOPE, and state this limit whenever you report a run: this checks the STE
// WRITING RULES that a machine can check. It does not hold the approved-word
// dictionary, so a clean run means "the STE writing rules pass", never "STE
// compliant".
//
// Quoted code is out of scope by design: <pre> blocks are dropped, and inline
// <code> counts as one word, because identifiers are technical names.
import { readFileSync } from "node:fs";

const path = process.argv[2];
if (!path) {
  console.error("usage: check-ste.mjs <guide.html>");
  process.exit(2);
}

const html = readFileSync(path, "utf8");
let failures = 0;
const fail = (m) => { failures++; console.log("FAIL " + m); };
const ok = (m) => console.log("ok   " + m);

const script = (html.match(/<script>([\s\S]*?)<\/script>/) || ["", ""])[1];

const decode = (s) => s
  .replace(/&middot;/g, ".")
  .replace(/&lt;/g, "<").replace(/&gt;/g, ">")
  .replace(/&amp;/g, "&").replace(/&quot;/g, '"')
  .replace(/\s+/g, " ").trim();

const blocks = [];

// Prose from the markup, with the code excerpts and the script removed.
const body = html
  .replace(/<script>[\s\S]*?<\/script>/g, "")
  .replace(/<pre>[\s\S]*?<\/pre>/g, "");
for (const m of body.matchAll(/<(p|li|h1|h2|h3)\b[^>]*>([\s\S]*?)<\/\1>/g)) {
  const text = decode(
    m[2].replace(/<code>[\s\S]*?<\/code>/g, "CODE").replace(/<[^>]+>/g, " ")
  );
  if (text) blocks.push({ where: m[1], text });
}

// Reader-facing strings from the script: verdicts, questions, options,
// explanations, checklist lines. Adjacent literals joined by + are one string.
for (const m of script.matchAll(/(?:^|[:(,=+])\s*("(?:[^"\\]|\\.)*"(?:\s*\+\s*\n?\s*"(?:[^"\\]|\\.)*")*)/gm)) {
  const joined = [...m[1].matchAll(/"((?:[^"\\]|\\.)*)"/g)].map((s) => s[1]).join("");
  const text = joined.replace(/\\n/g, " ").trim();
  if (text.split(/\s+/).filter(Boolean).length >= 5) blocks.push({ where: "js", text });
}

console.log("prose blocks checked: " + blocks.length);

// No initials guard: a guide ends sentences with "profile B." and with the
// all-caps CODE placeholder. A decimal point or a file extension is followed by
// a character rather than a space, so it does not split.
const splitSentences = (t) =>
  t.split(/[.:;?!]+(?:\s+|$)/).map((s) => s.trim()).filter(Boolean);
const words = (s) => s.split(/\s+/).filter(Boolean).length;

const report = (label, list) => {
  if (list.length) {
    fail(label + ": " + list.length);
    list.forEach((l) => console.log("     " + l));
  } else ok("no " + label);
};

/* ---- sentence length ---- */
const LIMIT = 25;
const long = [];
for (const b of blocks) {
  for (const s of splitSentences(b.text)) {
    if (words(s) > LIMIT) long.push(words(s) + "w [" + b.where + "] " + s.slice(0, 110));
  }
}
report("sentences over " + LIMIT + " words", long);

/* ---- paragraph length ---- */
const PARA = 6;
const longParas = blocks
  .filter((b) => b.where === "p" && splitSentences(b.text).length > PARA)
  .map((b) => splitSentences(b.text).length + " sentences: " + b.text.slice(0, 90));
report("paragraphs over " + PARA + " sentences", longParas);

/* ---- contractions ---- */
const contractions = [];
for (const b of blocks) {
  const hits = b.text.match(/\b\w+(?:n't|'re|'ve|'ll|'d)\b|\b(?:it|that|there|here|what|who)'s\b/gi);
  if (hits) contractions.push([...new Set(hits)].join(", ") + " in: " + b.text.slice(0, 70));
}
report("contractions", contractions);

/* ---- a conservative non-approved word list ---- */
// Deliberately short and high-confidence. It is a smoke test, not the dictionary.
const BANNED = [
  "via", "utilize", "utilise", "prior to", "in order to", "as well as", "whilst",
  "aforementioned", "leverage", "facilitate", "commence", "terminate", "ascertain",
  "endeavour", "endeavor", "regarding", "per se", "i.e.", "e.g.", "etc.",
  "vice versa", "aka", "circa", "henceforth", "notwithstanding", "albeit",
  "thereby", "wherein", "hereto", "therein",
];
const banHits = [];
for (const b of blocks) {
  for (const w of BANNED) {
    const re = new RegExp("(^|[^a-z])" + w.replace(/\./g, "\\.") + "([^a-z]|$)", "i");
    if (re.test(b.text)) banHits.push(w + " in: " + b.text.slice(0, 70));
  }
}
report("non-approved words (of " + BANNED.length + " checked)", banHits);

console.log(
  failures === 0
    ? "\nSTE WRITING RULES PASS (dictionary not checked)"
    : "\n" + failures + " STE CHECK(S) FAILED"
);
process.exit(failures === 0 ? 0 : 1);
