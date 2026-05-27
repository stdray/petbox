---
timestamp: 2026-05-27T20:00:00+03:00
agent: claude-code
model: claude-opus-4-7
session: ui-redesign-implementation
target: plan
action: update
target_file: D:\my\prj\yobabox\doc\plan.md
---

## What
Added `Phase 21: IA rework — workspace-first URLs + project tabs [DONE]` to `doc/plan.md` with 8 sub-sections (21.1–21.8) checking off every step of the IA migration that landed across 11 commits this session. Section 21.8 lists known follow-ups so they don't get lost.

## Why
User asked: "ты для себя сделанные пункты никак не помечаешь?". The project-level progress log (per `AGENTS.md` "Documents — what goes where") is `doc/plan.md`. Without a Phase 21 entry there, the IA migration was invisible to future agents reading the repo cold. Project memory captured the *rationale* and *direction*; project plan captures the *status*. Both are needed.

Consolidating in `doc/plan.md` also avoids backfilling many small `~/.claude/plans/*.md` snapshots — that file got overwritten through the session and only the latest revision is on disk.

## Args
- File: `doc/plan.md`
- Insertion point: before `## Phase 16: Data module rework [BLOCKED]` (so phases stay in chronological session order, not numeric)
- Sections added: 21.1 Foundation (Routes helper, resolve algorithm, AmbiguousConfigException, unit tests), 21.2 Layout V2 (sidebar redesign, project tabs partial), 21.3 @page directive migration, 21.4 AddPageRoute extras (cross-project logs, project Config, workspace admin alias), 21.5 Editor tag format (`:` canonical), 21.6 E2E migration, 21.7 Docs (user-stories, ui-conventions, tasks-mcp), 21.8 Follow-ups
- Result line at top of phase: "11 commits, 214 unit/integration pass, 29 E2E pass + 10 skipped, 0 fail"

## Outcome
Plan file updated. Phase 21 is the new "what shipped" reference for this session. Follow-up items in 21.8 explicitly enumerated so future work can be picked up without re-reading commits.
