---
name: petbox-node-authoring
description: >-
  How to structure the BODY of a {{PROJECT}} node or comment so a person reads it, not an agent.
  Use before writing any node/comment body longer than a couple of lines, and before reaching for
  a diagram. Covers what GFM formatting already gives you for free, the callout convention (GFM
  alerts), the diagram convention (a sanitized inline-SVG subset, not mermaid) and its
  caption-states-the-claim discipline — and, the part that matters most, when a diagram is NOT
  worth drawing.
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

**Tool naming:** base verbs are underscore-delimited (`tasks_upsert`); opencode prefixes
`petbox_`, Claude Code prefixes `mcp__petbox__`.
