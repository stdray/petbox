# doc/tasks-mcp/

Bench of real plan/memory operations performed by coding agents while building YobaBox. Each file = one operation record (what / why / args).

## Purpose

Future YobaBox **Tasks/Plans module** is meant to unify task tracking across coding agents (claude code, factory droid, opencode, oh-my-pi, …) via MCP. To design it well, we need *real* examples of what agents actually do when they manage plans and memory. This directory collects them as they happen.

Eventually these records become:
- Example inputs for the Tasks-MCP API contract (what fields are actually needed?).
- A regression set for the Tasks UI (does it render real-world records sanely?).
- Anonymized fixtures for tests.

## When a record is created

Per `AGENTS.md` ("Recording plan/memory actions"), an agent writes a record here whenever it:
- Creates, edits, or deletes a plan file (project plan in `doc/plan.md`, or session plan in `~/.claude/plans/*.md`, or any agent-specific equivalent).
- Creates, edits, or deletes a memory file (in `~/.claude/projects/.../memory/` for Claude Code; analogous locations for other agents).

One operation → one file. Batch edits to the same file in the same turn collapse into one record.

## File naming

`{YYYY-MM-DD}-{NN}-{kebab-slug}.md` where NN is a 2-digit counter within the day.

Example: `2026-05-27-03-bump-tasks-module-priority.md`

## File format

```markdown
---
timestamp: 2026-05-27T17:45:00+03:00
agent: claude-code
model: claude-opus-4-7
session: optional-session-id
target: plan | memory
action: create | update | delete
target_file: relative or absolute path
---

## What
One paragraph describing the change.

## Why
Motivation: what user request triggered this, what problem it solves.

## Args
The actual content/arguments passed. For memory: name, description, type, body summary. For plan: section names touched, key diffs.

## Outcome
Did it succeed? Anything the operator should know.
```

## Conventions

- Write in the language of the conversation that produced it (often Russian for this project). Agent labels and frontmatter keys stay English.
- Records are append-only. Never edit an old record — if it's wrong, write a new one explaining the correction.
- Do not include secret values from actual memory/plan content. If a record references a secret, redact it.
