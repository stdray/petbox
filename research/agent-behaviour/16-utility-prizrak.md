# 16 — Utility Prizrak (petbox-utility spawn after deletion). Инвентаризация. 2026-09-10
Автор: petbox-worker · источники: код/замер (архив transcripts)/[владелец] commit message

## Что здесь верно сейчас
- Baseline `roles --days 30` (Claude Code leg) shows petbox-utility n_calls=2 [замер] `C:\Users\stdray\.petbox\baselines\baseline_20260910_145437.txt:155`.
- Both calls located as real subagent transcripts (matched on `"agentType":"petbox-utility"` in `.meta.json`, exhaustive grep across `~/.claude/projects`, exactly 2 hits — matches baseline count) [замер]:
  - `…/D--my-prj-petbox/840b1c0c-a8c3-4848-b9ad-981bce56c6b6/subagents/agent-ad79e945082c13a4b.{jsonl,meta.json}`
  - `…/D--my-prj-petbox/e5f01519-9b89-475b-a7b6-489238be8e07/subagents/agent-ae5189d6220005327.{jsonl,meta.json}`
- Call timestamps: call1 `2026-08-28T16:06:45.554Z`; call2 `2026-08-20T10:05:08.106Z` [замер] transcript first line.
- Deletion commit `681affd6` timestamp `2026-08-29 11:16:30 +0300` [код] `git show -s --format=%ci 681affd6`.
- Both calls **predate** deletion (call1 by ~19h local, call2 by 9 days) — the role legitimately existed when both were spawned. Not a post-deletion anomaly.
- Both spawned explicitly by `subagent_type:"petbox-utility"` from a **claude-opus-5** root/orchestrator session, project `D--my-prj-petbox`, `spawnDepth:1` [код] parent-session `tool_use` block, e.g. `840b1c0c….jsonl:492` — `"model":"claude-opus-5" … "name":"Agent","input":{"subagent_type":"petbox-utility", …}`.
- Both ran on **model `claude-haiku-4-5-20251001`**, `attributionAgent:"petbox-utility"` throughout [замер] transcript tail usage blocks.
- Historical role default was haiku: pre-deletion `roles.ts` had `utility: "haiku"` in `DEFAULT_ROLE_MODEL_SEED`, removed by `681affd6` [код] `git show 681affd6 -- src/clients-ts/petbox-wire/src/roles.ts` — matches the observed model exactly.
- Call1 (2026-08-28, "Live end-to-end observation check") completed cleanly, no crash: it called `tasks_owner_digest`-adjacent tool discovery, found `tasks_observation_promote` absent from the live server (petbox.3po.su 2f89092), listed the actual available `tasks_*` tools, and stopped per its own brief's instruction not to work around it.
- Call2 (2026-08-20, "Create tails collector card") completed successfully: created work-board card `batch-12-q3-tails-2026-08-20` (type `chore`, board `work`, project `$system`), confirmed via its own final message.
- Current `~/.claude/agents/` (this machine) holds only `petbox-orchestrator.md, petbox-worker.md, petbox-worker-highstakes.md, petbox-explore.md, petbox-reserve.md` — **no** `petbox-utility.md` [замер] `ls`.
- Current render source `default-agents.json` has exactly 5 role slugs (`orchestrator, worker, worker-highstakes, reserve, explore`); `"utility"` appears only as a `"tier"` value under `explore` [код] `src/clients-ts/petbox-wire/src/default-agents.json:5,29,44,59,76-77`.
- `KNOWN_ROLES` (`tools/session-usage/session_usage.py:85-92`) and `HISTORICAL_ROLE_MODEL_SEEDS["claude-code"].utility` (`roles.ts:977-980`) still name `petbox-utility`/`utility` — both are explicitly backward-compat display/accounting lists for reading OLD archive data, not consulted by the Agent-tool spawn path.
- `profiles.example.json` is a documented example for the `money` subcommand's cost-profile calc ("Not authoritative for your setup - copy and edit", `profiles.example.json:1`), disconnected from role rendering/spawning.
- Known open defect exists and is named in the deletion commit itself [владелец] `681affd6` message: "apply does NOT remove petbox-utility.md from an existing checkout … orphans silently on all three harnesses" — filed as `observations/apply-orphans-artifacts-of-a-deleted-role`.

## Где механизм на самом деле живёт
`tools/session-usage/session_usage.py:85-92` — KNOWN_ROLES, backward-compat display list only.
`src/clients-ts/petbox-wire/src/roles.ts:977-980` — HISTORICAL_ROLE_MODEL_SEEDS, kept to interpret archived cost data, not to spawn.
`src/clients-ts/petbox-wire/src/default-agents.json:1-77` (loaded via `agent-definition.ts:106`, `DEFAULT_AGENT_DEFINITION`) — the actual roster `apply` renders into `~/.claude/agents/*.md` today; 5 roles, no `utility` slug.
`~/.claude/projects/D--my-prj-petbox/{840b1c0c…,e5f01519…}/subagents/agent-*.{jsonl,meta.json}` — the two real historical calls, both dated before deletion commit `681affd6` (2026-08-29 11:16:30+0300).

## Что НЕ проверено
- Whether spawning `subagent_type:"petbox-utility"` TODAY would error, silently fall back to a catch-all/default agent, or run on the session model — [НЕПРОВЕРЕНО], brief forbids a live spawn to test this. Indirect signal only: this very session's own available-agent-types listing (harness system-reminder) excludes `petbox-utility` alongside the 5 real roles — consistent with rejection, not proof of behavior on invocation.
- Whether the known `apply-orphans-artifacts-of-a-deleted-role` defect has actually left a stale `petbox-utility.md`/`.toml` on any OTHER harness dir (`.opencode/agent`, `.factory/droids`, `.codex/agents`, `.qwen/agents`) on this machine, or on any other machine/checkout — only `~/.claude/agents` on this machine was checked, and it is clean [НЕПРОВЕРЕНО].
- Whether any project besides `D--my-prj-petbox` still carries a call or a rendered artifact — grep for `"agentType":"petbox-utility"` was run over the full `~/.claude/projects` tree (all projects), returning exactly these 2 hits, so this specific question is answered; but rendered-artifact orphan files were not searched machine-wide outside `.claude/agents`.

## Противоречия
- None. Baseline figures, both transcripts, and the `roles.ts` diff agree on timing, model, and role identity.

## Что отсюда следует
The finding's framing ("a deleted role is still being spawned") does not hold up: both calls are historical, made while `petbox-utility` legitimately existed, and surface in the 30-day report only because its window (cutoff ≈2026-08-11) still overlaps dates before the 2026-08-29 deletion. An archive-wide grep for `"agentType":"petbox-utility"` found no call after the deletion commit. Both historical calls ran to completion on `claude-haiku-4-5-20251001` under an opus-5 orchestrator — one succeeded, one self-terminated cleanly on a missing tool, neither crashed or silently misrouted. The one real open item is the already-known, already-filed orphan-artifact gap, unconfirmed present on this machine; whether a fresh spawn attempt today would misroute silently is the one question genuinely unanswerable without violating the no-spawn constraint.
