# 22 — Roster change: counted procedure. 2026-09-10
Автор: petbox-worker (Claude Sonnet 5) · источники: код (read-only), README, доска `observations`

## Scenario A — rename `reserve`→`reserve1`, add `reserve2`, change `worker`'s model

TWO reachable paths exist; the brief's own location list (kit source, roles.json, layer dirs,
rendered files, test ratchets, seeds in roles.ts, README, KNOWN_ROLES) maps to the CONTRIBUTOR
path below. No `KNOWN_ROLES`/`KnownRoles` symbol exists anywhere in the repo — `AgentRole.slug`
is a plain `string` in TS and C#, confirmed by prior inventory `11-roli...:44-46` — correction to
the brief's premise, not a defect.

### Path 1 — contributor, editing the kit's own git repo (12 steps)
1. `src/common/default-agents.json:1-88` — rename `reserve`→`reserve1`, add `reserve2` (tier,
   requiredCapabilities, spawn, escalation, **notes** = prose, a string field here, not a file).
   *manual edit*
2. Same file — fix every OTHER role's `spawn.allowedRoles`/`escalation.targets` that still name
   `reserve` (orchestrator's spawn list at minimum), or apply ships a dangling instruction,
   caught only at build as error E1. *manual, undocumented interaction*
   [код `definition-integrity.ts:1-16`]
3. `roles.ts:531-560` — rename the `reserve` key and add a `reserve2` key in all 4 seed maps
   (`DEFAULT_ROLE_MODEL_SEED`/`CODEX_ROLE_MODEL_SEED`/`QWEN_ROLE_MODEL_SEED`/
   `HARNESS_ROLE_MODEL_SEEDS`), or the new role never auto-gets a model binding.
   *manual, undocumented interaction*
4. `default-agents-baseline.test.ts:23-29` `EXPECTED_SLUGS` — swap the slug list or the test
   ratchet fails. *manual edit*
5. `node scripts/sync-default-agents.mjs` (or `npm test`/`prepack`) regenerates the gitignored
   copy at `petbox-wire/src/default-agents.json`. *automated by existing command*
6. Rebuild `PetBox.Core` (same source, embedded resource) + redeploy the server. *automated step*
   [код `DefaultAgentDefinition.cs:14-20`]
7. Publish/bump the npm kit.
8. Per machine: `npx petbox-wire model set worker <model> [--agent|--all-agents]`. *automated*
   [код `wire.ts:2211-2279`]
9. Per machine, per harness: `model set reserve2 <model> --agent <id>` — step 3 only fixes a
   FRESH `roles.json`; an existing one already has every harness key, and the seeder only fills
   a harness that is **totally absent**: "a harness that IS already present... is left
   completely alone, roles included" [код `roles.ts:568-575`] — `new-role-never-gets-local-
   model-binding-on-existing-installs` biting exactly here. *manual, undocumented interaction*
10. `npx petbox-wire apply` per machine — renders 5 files per new slug (`.claude/agents/`,
    `.opencode/agent/`, `.factory/droids/`, `.codex/agents/`, `~/.qwen/agents/`) = 10 files,
    *automated* [код `apply-artifacts.ts:29-31,103-116`]. Old `petbox-reserve.*` (5 files) are
    swept by the same apply loop [код `wire.ts:1196`, `apply-orphans.ts:1-45`] — **unresolved
    contradiction**: board card `apply-orphans-artifacts-of-a-deleted-role` (`observations`,
    status `seen`) reports this reproduced live TODAY; the code reads as already wired in. Not
    re-run here (read-only task) — flagged, not adjudicated.
11. Repeat 8-10 on every other machine (→ Scenario C).
12. `npx petbox-wire doctor` — recommended sanity check, not required.

**Count, Path 1: 12 actions** (traps: step 2, step 9); **steps 1-4 sit inside the kit's own
git repository.**

### Path 2 — third-party user, no repo access, via the layer cascade (10 steps)
Layers exist so a non-contributor never needs the kit repo: base(kit) < user (`~/.petbox/
agents`) < project (`<root>/.petbox/agents`) [код `definition-source.ts:67-100`], documented in
the shipped `README.md:126-166` (ships with any npm install). Steps: create the layer dir;
`layer.json` `{"name":"user","mode":"overlay"}`; `petbox-reserve.json`
`{"slug":"reserve","removed":true}` tombstone; `petbox-reserve1.json` — a COMPLETE role patch
(ADD needs every field, nothing inherited); `petbox-reserve2.json` complete; a CHANGE patch on
`orchestrator.json` fixing `spawn.allowedRoles` (else E1, same trap as Path 1 step 2 — the
README's own worked example never shows a rename, so this is not spelled out); `model set` for
worker and reserve2 (same seed-map gap as Path 1 step 9 — bindings are NOT part of the layer
system [код `roles.ts:1-8`]); `petbox-wire layers` to confirm; `apply`.
**Count, Path 2: 10 steps, all reachable without forking** — but carries both of Path 1's
undocumented-interaction traps unchanged.

## Scenario B — the one-space edit: preserved, overwritten, or refused?

**Overwritten, silently, unless the user first flips one marker line.** Every file `apply`
renders carries frontmatter `petbox: managed` [код `origin-marker.ts:19`]. `writeArtifact` —
the only write path `apply` uses — checks that marker: file exists, marker present, content
differs → overwrite, no diff shown, no prompt; the comment calls this "the routine, expected
re-apply case" [код `apply-write.ts:72,86,101`: `!hasPetboxMarker` guards refusal, `existing ===
content` is the only branch that skips the write]. One added space is not byte-identical, so it
is not "unchanged" — the next `apply` (kit update or a bare re-run) overwrites it. The only
escape is `petbox: manual` [код `origin-marker.ts:20,66` — outranks even `--adopt`], undocumented
as a per-role "protect my edit" recipe; the README's actual answer is "move it into a layer"
(Path 2), never "hand-edit the rendered file and mark it manual." **Apply never refuses** — exit
0 either way (write "own" or "unchanged"); there is no third outcome. [код `apply-write.ts:53-101`]

## Scenario C — same user, second machine

Nothing above syncs anywhere. `~/.petbox/roles.json` is explicitly local-machine, "NEVER the
source of truth" server-side [код `roles.ts:1-8`]; `~/.petbox/agents` is machine-local by
definition, never in any git repo. Only `<project>/.petbox/agents` can travel, and only if the
project's own repo happens to track it (not automatic either way). Machine 2, single harness:
re-create/pull the roster (0-5 files depending on path) + 2× `model set` (worker, reserve2) +
`apply` = **3-4 steps**; 5-harness case climbs to **~11 steps**, since `roles.json` has no
cross-machine mechanism of any kind.

## Reachability for a non-contributor (npm install only)

Reachable: all of Path 2 (layers, `model set`, `apply`, `layers` diagnostic, `doctor`) — CLI- or
README-surfaced, no git needed. **Unreachable without forking:** Path 1 steps 1-4
(`default-agents.json`, `roles.ts`'s 4 seed maps, `EXPECTED_SLUGS`) and steps 6-7 (C# rebuild,
npm publish) — **6 of Path 1's 12 locations**: exactly the steps that change the DEFAULT roster
shipped to every OTHER user, as opposed to one person's own override.

## Verdict on the counts (dry)

Not independent: B's answer (silent clobber on the rendered-file path) is why the README steers
everyone toward Path 2, and Path 2's 10-step count is what a genuinely external user pays again,
per machine, per harness, in Scenario C.