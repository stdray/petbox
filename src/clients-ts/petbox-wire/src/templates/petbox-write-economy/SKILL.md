---
name: petbox-write-economy
description: >-
  Pay for the CHANGE you are making, not for the whole text. Use before any tasks_upsert /
  comments_upsert / memory_remember / memory_upsert / session_append call whose body is more
  than a few lines, and before any read that only needs headers or a couple of known keys.
  Covers bodyRef (upload text once, reference it by a short id), fragment (point-patch an
  existing body), why raw UTF-8 beats \uXXXX escaping, the bodyLen read contract — and, just as
  important, the cases where none of this is worth the extra call.
petbox: managed
petbox-digest: auto
---

# Write economy: pay for the change, not the text

A write call's cost to you is the tokens it takes to PRODUCE the arguments, not the bytes PetBox
stores. The four techniques below each remove one way that cost gets inflated past the size of
the actual change. Read the last section first if you are unsure whether any of this applies —
half of the value here is knowing when NOT to reach for it.

## (a) `bodyRef` — a body that already exists, by reference

Upload text once, then pass a short reference instead of retyping the text into the call:

```
curl -sS -X POST "https://petbox.3po.su/api/blobs/{{PROJECT}}" \
  --data-binary @report.md \
  -H "X-Api-Key: $PETBOX_API_KEY"
```
```
# PowerShell
Invoke-RestMethod -Method Post -Uri "https://petbox.3po.su/api/blobs/{{PROJECT}}" `
  -InFile report.md -Headers @{ "X-Api-Key" = $env:PETBOX_API_KEY }
```

The RAW file bytes are the request body — no JSON envelope, no multipart. The response is
`{ ref, bytes, chars, expiresAt }`, `ref` looking like `blob-<32 hex chars>`. Then:

```
tasks_upsert(projectKey:"{{PROJECT}}", nodes:[{ key:"...", bodyRef:"blob-<ref>" }])
```

- **Ceiling 10 MB.** **One-shot**: consumed by the write that references it (a REFUSED write does
  not consume it — retry with the same ref); an unconsumed blob expires 24h after upload.
- **Scoped to the project it was uploaded into**, and to **one item's body**, not one call — a
  batch writing N nodes needs N separate blobs, one per item.
- **Mutually exclusive with `body` and `fragment`.** Sending two of them is a REFUSAL (through
  `conflicts[]` on the batch verbs), never a silent precedence.
- **Where it is accepted** (check the field name — it always mirrors the field it replaces):
  - `tasks_upsert` — `bodyRef` alongside `body`
  - `comments_upsert` — `bodyRef` alongside `body`
  - `memory_upsert` — `bodyRef` alongside `body`
  - `memory_remember` — `textRef` (this verb's field is `text`, not `body`)
  - `session_append` — `contentRef`, **per message** (this verb's field is `content`; a batch may
    mix one referenced message with several inline ones)
- **Upload scope:** `tasks:write` OR `memory:write` — no separate upload scope exists, because
  uploading only ever grants a prefix of what one of those already grants.

## (b) `fragment` — patch, don't retype

A list of `{ old, new }` edits, applied **in order** against the CURRENT body (PATCH only — a
create has no current body to patch against):

```
tasks_upsert(projectKey:"{{PROJECT}}", nodes:[{
  key:"...", version:<n>,
  fragment:[{ old:"the exact text to replace", new:"its replacement" }]
}])
```

- Each `old` must occur **exactly once** in the current text. Zero matches or two-or-more matches
  is a REFUSAL through `conflicts[]` naming the count — never a first-match guess, never a
  partial apply. Re-read the body and rebase the edit.
- `new` is required; `""` deletes the matched text.
- Mutually exclusive with `body`.
- Available on `tasks_upsert`, `comments_upsert`, `memory_upsert`. Not on `memory_remember`
  (create-only) or `session_append` (no patch verb on that surface).

## (c) Raw UTF-8 in the call — never `\uXXXX`

Every `\uXXXX` escape prints **six characters** for one, and that cost is paid out of the
**model's own output budget**, not the wire. A long non-ASCII call (Cyrillic, CJK, …) can exhaust
that budget mid-generation — the call never finishes, and what the server sees (if anything) is a
parse error, not a large-but-valid body. This is not something PetBox can detect or fix from the
server side: by the time an escaped call is small enough to arrive intact, the damage already
happened upstream.

This is exactly why (a) and (b) exist as mechanisms rather than as a rule to remember: the same
warning has lived in tool descriptions and project canon for a long time and keeps recurring
anyway. Prefer moving text out of the call entirely (bodyRef) or shrinking it to just the change
(fragment) over trying to type a large body correctly under this constraint.

## (d) Reading cheaply — the `bodyLen` contract

Every body-carrying read/search/echo shares one knob, `bodyLen`: `0` = no body, `N > 0` = the
first N chars (`"…"` when cut), `-1` = the full body. (What an *omitted* `bodyLen` defaults to
varies by surface — a listing/search snippet, a compact write-ack, or a full pointed read — each
tool's own description says which.)

Cheap path: search/list with `bodyLen:0`, look at the row identities, then fetch the 1-3 rows you
actually need in full (`tasks_node_get` / `memory_get` / `comments_get`). Reserve `bodyLen:-1` for
when you already know the few keys you want — pulling full bodies across a wide result set "just
in case" routinely spends a large share of the response budget on text nobody reads.

## (e) When none of this pays off

This is a real constraint, not a footnote: apply the techniques above only where they actually
save something.

- **Text that is born in the call itself does not get cheaper.** A short note, a plan you are
  composing right now, a status update you are writing as you go — the model pays for that text
  once, in output tokens, no matter where it ends up. Writing it to a file and then uploading it
  as a `bodyRef` costs the SAME output tokens as `body` would, plus a second call. There is no
  version of "upload it instead" that is cheaper for text that only exists because this call
  produced it.
- **The mechanism earns its keep where the text already exists outside the model's own output**:
  a log tail, a git diff, a command's stdout, a subagent's report, a file already in the repo.
  There the saving is not "twice down to once" — it is "once down to **zero**": the text moves
  without the model ever retyping it into an argument at all.
- **`fragment` does not pay off on an edit that rewrites most of the body.** A list of ten
  `{old, new}` pairs covering nearly the whole text costs more — in call size and in the risk of
  a stale slice failing to match — than one `body` replacement. Reach for `fragment` for a
  handful of localized edits; reach for `body` for anything that reads like a rewrite.

**Tool naming:** base verbs are underscore-delimited (`tasks_upsert`); opencode prefixes
`petbox_`, Claude Code prefixes `mcp__petbox__`.
