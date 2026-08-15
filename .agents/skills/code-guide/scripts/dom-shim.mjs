// A DOM shim just large enough to run a code guide's <script> under node, so
// the lab decision logic can be driven and asserted without a browser.
//
// This is what actually proves the labs. The browser pass is for layout.
//
// Usage from a throwaway driver in the scratchpad:
//
//   import { loadGuide } from "<repo>/.agents/skills/code-guide/scripts/dom-shim.mjs";
//   const g = loadGuide("docs/2026-08-my-topic-code-guide.html");
//   g.pick("labAWorld", "data-a-world", "shipped");   // click a seg button
//   g.slide("labBLand", 4);                           // move a slider
//   assert.equal(g.text("labAStored"), "True");       // read a stat tile
//
// Assert against the behaviour you read out of the shipped C#, never against
// whatever the lab currently prints.
import { readFileSync } from "node:fs";
import vm from "node:vm";

class Node {
  constructor(tag) {
    this.tagName = tag;
    this.children = [];
    this.attrs = {};
    this._text = "";
    this.listeners = {};
    this.style = {};
    this.className = "";
    this.value = "";
    this.classList = {
      add: (...c) => { this.className = (this.className + " " + c.join(" ")).trim(); },
      remove: (...c) => {
        this.className = this.className
          .split(/\s+/).filter((x) => x && !c.includes(x)).join(" ");
      },
      contains: (c) => this.className.split(/\s+/).includes(c),
    };
  }
  get textContent() { return this._text; }
  set textContent(v) { this._text = String(v); this.children = []; }
  get innerHTML() { return ""; }
  set innerHTML(v) { if (v === "") { this.children = []; this._text = ""; } }
  appendChild(n) { this.children.push(n); return n; }
  setAttribute(k, v) { this.attrs[k] = String(v); }
  getAttribute(k) { return k in this.attrs ? this.attrs[k] : null; }
  removeAttribute(k) { delete this.attrs[k]; }
  addEventListener(ev, fn) { (this.listeners[ev] ||= []).push(fn); }
  fire(ev) { (this.listeners[ev] || []).forEach((f) => f.call(this)); }
  querySelectorAll(sel) {
    const attr = sel.replace(/^\[|\]$/g, "");
    return this.children.filter((c) => attr in c.attrs);
  }
  /** Text of this node and everything under it. */
  get deepText() {
    return (this._text + " " + this.children.map((c) => c.deepText).join(" "))
      .replace(/\s+/g, " ").trim();
  }
}

/**
 * Parse the guide, seed a node for every id in its markup, run its <script>,
 * and return handles for driving and reading the labs.
 */
export function loadGuide(htmlPath) {
  const html = readFileSync(htmlPath, "utf8");
  const script = html.match(/<script>([\s\S]*?)<\/script>/)[1];

  const registry = new Map();
  for (const m of html.matchAll(/\bid="([^"]+)"/g)) registry.set(m[1], new Node("div"));

  // Seed the segment buttons the script binds, from the markup rather than a guess.
  for (const seg of html.matchAll(/<div class="seg" id="([^"]+)">([\s\S]*?)<\/div>/g)) {
    const root = registry.get(seg[1]);
    for (const b of seg[2].matchAll(/<button[^>]*?(data-[a-z-]+)="([^"]+)"[^>]*>/g)) {
      const btn = new Node("button");
      btn.setAttribute(b[1], b[2]);
      root.appendChild(btn);
    }
  }
  for (const r of html.matchAll(/<input type="range" id="([^"]+)"[^>]*value="(-?\d+)"/g)) {
    registry.get(r[1]).value = r[2];
  }

  const document = {
    createElement: (t) => new Node(t),
    createTextNode: (t) => { const n = new Node("#text"); n.textContent = t; return n; },
    getElementById: (id) => {
      if (!registry.has(id)) throw new Error("shim is missing id: " + id);
      return registry.get(id);
    },
  };

  vm.runInNewContext(script, { document, console });

  const node = (id) => document.getElementById(id);
  return {
    html,
    script,
    registry,
    node,
    /** Click the button in `segId` whose `attr` equals `value`. */
    pick(segId, attr, value) {
      const btn = node(segId).children.find((c) => c.getAttribute(attr) === value);
      if (!btn) throw new Error("no button " + attr + "=" + value + " in " + segId);
      btn.fire("click");
    },
    /** Move a range input and fire its input handler. */
    slide(id, v) { const n = node(id); n.value = String(v); n.fire("input"); },
    /** textContent of a node, for stat tiles and slider readouts. */
    text: (id) => node(id).textContent,
    /** Text of a node and its descendants, for verdict panels. */
    deepText: (id) => node(id).deepText,
    /** data-tone of a node, for verdict and stat tone. */
    tone: (id) => node(id).getAttribute("data-tone"),
  };
}
