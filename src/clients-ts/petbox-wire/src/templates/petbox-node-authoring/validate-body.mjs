// petbox: managed
// validate-body.mjs — author-side self-check for a PetBox node/comment body draft.
//
// Zero dependencies, no PetBox sources needed. Usage:  node validate-body.mjs <draft.md>
// Exit: 0 = clean, 1 = violations listed below, 2 = bad usage.
//
// Shipped as a REAL FILE next to SKILL.md (skill-files.ts's `extraFiles`), materialized by
// `petbox-wire apply`/`wire` on every skill surface (.claude/skills/petbox-node-authoring/,
// .factory/skills/petbox-node-authoring/) — never pasted from a fenced code block in SKILL.md.
// That used to be the delivery path (save the block below as validate-body.mjs) and it broke on
// at least one shell/tool pipeline that collapses backslash pairs on write (observation
// bash-quoting-collapses-backslashes-not-just-echo): a re-typed copy of this file's own
// `/\\n/g` regex silently became `/\n/g`, and the validator it produced flagged every real
// newline as a fake violation. Running THIS file — the one the kit actually wrote to disk, byte
// for byte — removes the re-typing step that failure mode depends on.
//
// The first line above is the origin marker (origin-marker.ts's comment-marker contract,
// mirroring the frontmatter `petbox: managed` markdown templates carry): it is what lets
// `petbox-wire apply` tell "our render, safe to refresh" apart from a file the project edited by
// hand, the same clobber-safety writeArtifact already gives every other managed file.
//
// The two constants below are parsed by a drift-guard test in the PetBox repo (which owns the
// real sanitizer: NodeAuthoringSkillSvgDriftTests.cs, reading THIS file, not SKILL.md's prose) —
// keep them machine-parseable: single line, double-quoted, this exact shape.

import { readFileSync } from "node:fs";

const ALLOWED_TAGS = "svg g path rect circle ellipse line polyline polygon text tspan marker defs title desc use";
const FORBIDDEN_TAGS = "script style foreignObject image";

const allowed = new Set(ALLOWED_TAGS.split(" "));
const forbidden = new Set(FORBIDDEN_TAGS.split(" "));
const urlFnAttrs = ["fill", "stroke", "marker-start", "marker-mid", "marker-end"];

const file = process.argv[2];
if (!file) {
  console.error("usage: node validate-body.mjs <draft.md>");
  process.exit(2);
}
const raw = readFileSync(file, "utf8");

// Code never reaches the sanitizer, so it is excluded from every check below. Fenced blocks and
// inline code spans are blanked WITHOUT deleting their newlines, so reported line numbers stay true.
const blank = (m) => m.replace(/[^\n]/g, " ");
const text = raw
  .replace(/^(```|~~~).*?^\1.*$/gms, blank)
  .replace(/`[^`\n]+`/g, blank);

const problems = [];
const lineAt = (idx) => raw.slice(0, idx).split("\n").length;
const flag = (idx, msg) => problems.push("line " + lineAt(idx) + ": " + msg);

// A literal backslash-n in prose renders as the letter n glued to whatever came before it —
// use a real blank line for a paragraph break (fenced code blocks are excluded above).
for (const m of text.matchAll(/\\n/g))
  flag(m.index, 'a literal backslash-n in prose renders as mush — use a real blank line ("\\n" is not a line break)');

// ==highlight== / --pseudo-heading-- : the renderer has no such syntax; the markers survive
// literally or the line mangles. Use a ## heading.
for (const m of text.matchAll(/^(==[^=\n]+==|--[^-\n]+--)\s*$/gm))
  flag(m.index, '"' + m[1] + '" is a pseudo-heading — the renderer has no such syntax; use a ## heading');

// Forbidden tags are stripped — WITH their contents — wherever they appear, inside or outside <svg>.
for (const m of text.matchAll(/<(script|style|foreignObject|image)[\s/>]/gi))
  flag(m.index, "<" + m[1] + "> is forbidden — the renderer strips it and its contents outright");

// Walk the tags inside <svg>...</svg> regions and check each against the allowlist and the
// local-only reference rules. Tags outside <svg> are ordinary (sanitized) HTML, not diagram rules.
const tagRx = /<(\/?)([a-zA-Z][a-zA-Z0-9:-]*)((?:"[^"]*"|'[^']*'|[^>"'])*)>/g;
let svgDepth = 0;
for (const m of text.matchAll(tagRx)) {
  const [whole, slash, name, attrs] = m;
  const tag = name.toLowerCase();
  if (slash) {
    if (tag === "svg") svgDepth = Math.max(0, svgDepth - 1);
    continue;
  }
  const insideSvg = svgDepth > 0;
  if (tag === "svg" && !whole.endsWith("/>")) svgDepth++;
  if (!insideSvg || forbidden.has(tag)) continue; // forbidden is reported by the whole-text scan

  if (!allowed.has(tag))
    flag(m.index, "<" + name + "> is not in the SVG allowlist — it is stripped at render time. Allowed: " + ALLOWED_TAGS);

  // href / xlink:href on an SVG element must be a fragment IN THIS DOCUMENT (an external SVG
  // reference is a real attack class; the sanitizer nulls anything not starting with #).
  for (const h of attrs.matchAll(/((?:xlink:)?href)\s*=\s*(?:"([^"]*)"|'([^']*)')/gi)) {
    const value = h[2] ?? h[3];
    if (value !== "" && !value.startsWith("#"))
      flag(m.index, h[1] + '="' + value + '" on <' + name + "> is an external reference — only a local #fragment survives");
  }

  // fill/stroke/marker-* may carry ONLY an exactly-local url(#id); anything else loses the whole
  // attribute. No paint-server element (gradient/pattern/filter) is on the allowlist, so there is
  // nothing else for a url() to legitimately point at.
  for (const attr of urlFnAttrs) {
    const v = new RegExp(attr + '\\s*=\\s*(?:"([^"]*)"|\'([^\']*)\')', "i").exec(attrs);
    const value = v && (v[1] ?? v[2]);
    if (value && /url\(/i.test(value) && !/^url\(#[A-Za-z][\w:.-]*\)$/.test(value.trim()))
      flag(m.index, attr + '="' + value + '" carries a non-local url(...) — the whole attribute is stripped unless it is exactly url(#id)');
  }
}

if (problems.length === 0) {
  console.log("OK — no author-facing violations in " + file);
  process.exit(0);
}
console.error(problems.length + " violation(s) in " + file + ":");
for (const p of problems) console.error("  " + p);
process.exit(1);
