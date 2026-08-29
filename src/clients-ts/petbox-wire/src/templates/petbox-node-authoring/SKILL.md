---
name: petbox-node-authoring
description: >-
  How to structure the BODY of a {{PROJECT}} node or comment so a person reads it, not an agent.
  Use before writing any node/comment body longer than a couple of lines, and before reaching for
  a diagram. Covers what GFM formatting already gives you for free, the callout convention (GFM
  alerts), the diagram convention (a sanitized inline-SVG subset, not mermaid) and its
  caption-states-the-claim discipline — and, the part that matters most, when a diagram is NOT
  worth drawing. Ships a zero-dependency validator to self-check a draft body before you write
  it.
petbox: managed
---

# Body authoring: structure for a reader, not for yourself

## (a) What the renderer already gives you, for free

A node/comment body is rendered as GFM: `##` headings, real paragraph breaks (a blank line — not
a literal `\n`, not `==heading==`), bullet and numbered lists, tables, fenced code blocks, links.
Typography, light/dark theming, line-length measure, and telling prose apart from identifiers are
all the renderer's job — you do not pay for any of it and should not invent your own ASCII
substitute (box-drawing dividers, hand-aligned columns, `**bold**` used as a heading). Write plain
GFM; the renderer does the rest.

## (b) Callout: GFM alerts, not a fake blockquote

A statement that must read as something OTHER than surrounding prose — a warning, a decision, a
caveat the reader must not skim past — is a GFM alert:

```
> [!NOTE]
> Point worth flagging but not urgent.

> [!WARNING]
> This will break X if skipped.
```

The five kinds are `NOTE`, `TIP`, `IMPORTANT`, `WARNING`, `CAUTION` — each renders with its own
label and styling; a plain `>` blockquote with no `[!KIND]` line is just a quote, not a callout.
Do not simulate one with bold text or a horizontal rule; the renderer only recognizes this exact
form.

## (c) Diagram: a sanitized inline-SVG subset, not mermaid

A body may contain raw `<svg>...</svg>` markup, sanitized at render time against a pinned tag/
attribute allowlist (`svg g path rect circle ellipse line polyline polygon text tspan marker defs
title desc use`, plus geometry/presentation/marker attributes). There is no mermaid fence and no
other diagram DSL — mermaid was considered and rejected for this feature. `<style>`, `<script>`,
`<foreignObject>` and `<image>` are stripped outright; an external `href`/`url(...)` reference is
rejected and only a local `#id` reference survives. Color it with `fill="currentColor"` /
`stroke="currentColor"` — that alone makes the diagram theme correctly in light and dark for free;
do not hardcode hex colors.

Every diagram is a `<figure>`, and both halves must carry the SAME claim:

```
<figure>
<svg viewBox="0 0 200 100" role="img">
<title>The bridge is struck through; the dashed line addresses a different id space.</title>
... shapes ...
</svg>
<figcaption>The bridge is struck through; the dashed line addresses a different id space.</figcaption>
</figure>
```

- `role="img"` + `<title>` is the drawing's own text alternative — write it as a full sentence
  stating what the picture shows, not a label like "Diagram 1".
- The `<figcaption>` states the same claim in prose, for a reader who does not parse the SVG.
- If you cannot write that one sentence, the diagram does not have a clear point yet — fix the
  diagram's content before worrying about markup.

The tag list above is enforced by the server at render time; section (e) ships a validator that
checks a draft against it before you write.

## (d) The rule that matters most: if a phrase says it faster, write the phrase

This skill exists to hold back drawing as much as to enable it. A diagram costs the author time to
build and the reader time to parse; prose costs neither when it already says the same thing.
Reach for a diagram only when one of these holds:

- the mechanism has **several distinct kinds of connection or state** that prose would have to
  enumerate one at a time, and the picture shows them at a glance instead;
- there is a genuine **divergence between two states or two paths** that is easier to see
  side-by-side than to describe in sequence;
- it is a flow where the **order of branching** itself is the point (which check happens before
  which).

Do NOT draw when:

- it is a **linear sequence** — "A, then B, then C" is a list, not a picture;
- it is **two boxes and one arrow** — that is a sentence: "A calls B";
- it would be an **"architecture diagram" for its own sake** — solidity is not a reason;
- it would just **redraw what the paragraph above it already said** — a diagram that repeats prose
  instead of adding to it has not earned its place.

When in doubt, write the caption sentence first. If the sentence is complete and clear on its own,
you very likely do not need the picture under it.

## (e) Verify before you write

Everything in (c) is enforced only at render time — on the server, after the body is already
written — and a wired project has no PetBox sources to read. So this skill ships its own check:
save the block below as `validate-body.mjs` next to your draft and run it BEFORE writing the body.
Exit 0 = clean; exit 1 = every author-facing violation, with line numbers. It checks the SVG tag
allowlist, the forbidden tags, the local-only `href`/`url(#id)` reference rules, and the GFM red
flags the renderer mangles (a literal backslash-n in prose, `==highlight==` / `--pseudo--`
headings). It deliberately does NOT replicate the sanitizer's output transforms (id suffixing and
friends) — those are not author-facing rules. Code fences and inline code are excluded from every
check (code never reaches the sanitizer).

````js
// validate-body.mjs — author-side self-check for a PetBox node/comment body draft.
//
// Zero dependencies, no PetBox sources needed. Usage:  node validate-body.mjs <draft.md>
// Exit: 0 = clean, 1 = violations listed below, 2 = bad usage.
//
// The two constants below are parsed by a drift-guard test in the PetBox repo (which owns the
// real sanitizer) — keep them machine-parseable: single line, double-quoted, this exact shape.

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
````

Run it with any Node 18+: `node validate-body.mjs draft-body.md`.

**Tool naming:** base verbs are underscore-delimited (`tasks_upsert`); opencode prefixes
`petbox_`, Claude Code prefixes `mcp__petbox__`.
