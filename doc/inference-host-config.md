# Inference host config → rerank truncation link

This is a **pointer**, not a copy. The rerank cross-encoder runs on infrastructure that
lives outside this repo (`llama-tooling`, a separate repo/host, its own lifecycle). PetBox
only consumes one number from it — the local route's size ceiling — and derives its own
input-truncation defaults from that number. This doc records the link so a change on
either side doesn't silently drift from the other.

## Where the ceiling lives

- File: `D:\my\prj\llama-tooling\router\models.ini`, section `[qwen3-rerank-0.6b]`
  (this repo does not vendor or copy it — see "Do not" below).
- Service: `llama-router`, installed as a WinSW service.
- **No hot-reload.** A `models.ini` edit takes effect only after the service is restarted.

## Which knob actually caps a request

The ceiling on a query+document pair is set by **`--ubatch-size`**, not `--ctx-size`.
`--batch-size` is kept equal to it because llama.cpp requires `-b >= -ub`. All three are
set to the same number in `models.ini` today, which can read as if `ctx-size` were the
governing parameter — it is not; direct measurement (a size ladder against the running
service) pinned the refusal boundary to `ubatch-size`. Source: bug card
`rerank-oversize-falls-through-both-legs` body (the original 8192 measurement) and its
verdict comment.

## Warning: the same ubatch/ctx-size trap has already bitten once, silently

Before the 2026-08-27 ceiling work above, `home` ran with `ubatch-size` left at the
llama.cpp default (**512**) while `ctx-size` was set much higher — the config *looked*
generous but the actual per-request ceiling was 512. Verbatim from the `models.ini`
comment in `[qwen3-rerank-0.6b]`:

> ubatch-size is NOT redundant with ctx-size: pooling=rank scores the whole query+document
> sequence in ONE physical batch, so the per-request ceiling is `--ubatch-size`, not
> `--ctx-size`. Left at the llama.cpp default (512) the server answers anything longer with
> HTTP 500 "input (N tokens) is too large to process" — measured 2026-07-28: 319 tokens OK,
> 559 rejected. PetBox treats that 500 as transient and re-runs the rerank on a cloud leg,
> so the local GPU silently served only the shortest documents while OpenRouter did the
> rest.

The symptom is what makes this worth calling out on its own: the refusal never looked like
a refusal. `CapabilityRouter` treated the HTTP 500 as transient and retried on the cloud
leg, so **the local GPU appeared to be working the whole time** — it just quietly stopped
being used for anything but the shortest documents, and the rest of the traffic was paid
for and served by OpenRouter without anyone noticing. Fixed 2026-07-28 by raising
`batch-size`/`ubatch-size` to 8192 in `models.ini` (superseded by the 10240 alignment
below). Source: PetBox memory `m-5041a72ebde646e08f5b76dce5374d18` (scope `workspace`,
store `notes`) and the `models.ini` comment itself — not the card comments cited elsewhere
in this doc, which don't carry this incident.

This is a **separate** incident from the "computed from the stale 8192" mistake recorded
in the bug card's verdict comment (below) — two different errors that both land on the same
consequence: a truncation/ceiling number computed from the wrong input.

## Current value

**10240**, set 2026-08-27. Chosen to equal the smallest *nominal* cloud shoulder measured
at the time (`nemotron`'s 10240; `cohere` accepts at least 15k, exact floor not
measured) — the point was to stop routing outcome from depending on which shoulder
happens to answer a mid-size request. Measured and recorded in card comments
`a85af1e9d92e444d974e520c32b5f1ef` and `bede7926182f4d78bec1e6c6236f72af` on
`rerank-oversize-falls-through-both-legs` (board `work`).

## Effective budget ≠ nominal ceiling

Equal nominal ceilings do not mean equal usable budgets — each route spends part of its
ceiling on its own per-request overhead (query + prompt template), and that overhead
differs by route (measured 2026-08-27, comment `bede7926182f4d78bec1e6c6236f72af`):

| route      | nominal ceiling | overhead (measured) | effective document budget |
|------------|-----------------|----------------------|----------------------------|
| `home`     | 10240           | ~75 tokens           | **~10165**                 |
| `nemotron` | 10240           | ~11 tokens           | **~10229**                 |
| `cohere`   | ≥15000 (floor not fully measured) | — | ≥15000 (not the binding one) |

A document sized between ~10165 and ~10229 tokens passes on `nemotron` and is refused on
`home` — a ~64-token "ladder zone" where outcome still depends on the route. Truncation
must therefore target the **smallest effective budget**, not the nominal number in
`models.ini`.

## PetBox's derived defaults

`RerankTruncationSettings` (`src/PetBox.Core/Settings/RerankTruncationSettings.cs`,
consumed via `RerankInputTruncation` in `src/PetBox.Core/Search/RerankInputTruncation.cs`)
sets:

- `search.rerank.truncate.documentChars` = **10000**
- `search.rerank.truncate.queryChars` = **2000**

sized against `home`'s ~10165-token effective floor above. Worst case — the densest
Cyrillic tokenization observed (~1.5 chars/token) — is 10000/1.5 + 2000/1.5 + ~75
(`home`'s own overhead) ≈ **8075 tokens**, roughly **21% margin** under ~10165, and still
short of the ~10165–~10229 ladder zone. The full derivation and arithmetic live in the
doc-comment at the top of `RerankTruncationSettings.cs` — that file is the canonical
source for the numbers above; this doc restates them for discoverability only.

## What to do when the host ceiling changes

Recompute — do not assume the existing 10000/2000 defaults still fit:

1. Re-measure each route's ceiling with a size ladder (as in the sources above) and each
   route's per-request overhead, to get the new **effective** budgets.
2. Take the smallest effective budget across routes.
3. Recompute `documentChars`/`queryChars` so the worst-case dense-Cyrillic token estimate
   (`documentChars/1.5 + queryChars/1.5 + smallest-route-overhead`) stays comfortably
   under that smallest effective budget — a margin in the same ballpark as the current
   ~21% is a reasonable target, not a hard requirement.
4. Update the defaults in `RerankTruncationSettings.cs` (or set an explicit override at
   the appropriate settings scope) and update the numbers/date in this doc.

Skipping this after a ceiling change leaves the truncation threshold either useless (too
loose — size refusals return) or over-aggressive (clipping healthy traffic that used to
fit).

## Do not

This doc does not duplicate `models.ini`. If a number here looks stale, treat
`models.ini` and the card comments below as the source of truth, not this file.

## Sources

- `D:\my\prj\llama-tooling\router\models.ini` — not versioned in this repo; `llama-tooling`
  is a separate repository with its own history and lifecycle.
- `src/PetBox.Core/Settings/RerankTruncationSettings.cs`,
  `src/PetBox.Core/Search/RerankInputTruncation.cs` — canonical derivation of the
  10000/2000 defaults.
- Board `work`, card `rerank-oversize-falls-through-both-legs`: comments
  `a85af1e9d92e444d974e520c32b5f1ef` (cloud ceilings measured per-pair, ladder of nominal
  values), `bede7926182f4d78bec1e6c6236f72af` (10240 alignment + effective-budget table),
  and the verdict comment (arithmetic for the 8075-token / ~21%-margin worst case).
- PetBox memory `m-5041a72ebde646e08f5b76dce5374d18` (scope `workspace`, store `notes`)
  and the `models.ini` `[qwen3-rerank-0.6b]` comment — the 2026-07-28 default-512
  incident above. Not the card comments: they don't carry this one.
