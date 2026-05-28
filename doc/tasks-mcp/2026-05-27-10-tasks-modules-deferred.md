---
timestamp: 2026-05-27T23:59:00+03:00
agent: claude-code
model: claude-opus-4-7
session: tasks-memory-design-critique
target: plan
action: update
target_file: D:\my\prj\yobabox\doc\plan.md
---

## What

Добавил `Phase 30: Tasks + Agent Memory modules [DEFERRED]` в конец `doc/plan.md`. Вместо детальной фазовой разбивки (которая обсуждалась как Phase 24/25/26 — backend / memory / skill) — одно отложенное phase-entry со ссылками на пропозал и критику, и списком экспериментов которые должны пройти ДО возврата к фазе.

Параллельные артефакты этой же операции (не plan, не memory — поэтому здесь только упоминание, не отдельные записи):
- `doc/proposals/tasks-memory-modules/proposal.md` — финальный дизайн обоих модулей.
- `doc/proposals/tasks-memory-modules/critique.md` — жёсткий разбор от стороннего ревьюера.
- `doc/decision-log.md` — новый файл (раньше не существовал), первая запись 2026-05-27 о deferring модуля.

## Why

Пользователь обсуждал дизайн Tasks + Memory модулей. После трёх итераций уточнений (per-project storage, MCP-канон, tree без ограничения глубины, junction-refs, 4-типовая memory + agent-tag) запросил критику через сторонний агент.

Reviewer дал verdict "не строить как сейчас, в честном — не делать вообще". Главные удары: compliance-парадокс (claude-code игнорирует более лёгкое требование записей в `doc/tasks-mcp/` — будет игнорировать и MCP-tools), велосипед (mcp-server-memory, Linear, Obsidian+git делают то же), bidirectional как write-only mirror, multi-machine дыры, дублирование с git.

Пользователь принял verdict и распорядился: пропозал + критику в отдельную папку, модуль в конец плана, запись в decision-log.

Альтернативы для оценки оставлены в Phase 30 (Stop-hook + git, готовый mcp-server-memory, минимальный Notes-модуль).

## Args

- File: `doc/plan.md`
- Insertion: после блока `### 23.7 — Follow-ups` (конец Phase 23 был last actual phase), новая секция `Phase 30` отделена `---`.
- Phase 30 body: short goal statement, status="DEFERRED", список экспериментов (Stop-hook, honest bench, real queries audit, multi-machine workflow), 5 альтернатив по убыванию value, sequencing note (Data → kpvotes → Tasks/Memory если эксперименты validate'нут).
- Phase numbering 30 (не 24): user'у выбран ясный gap чтобы не путать с возможным Phase 24/25/26 которые могут быть заняты другими работами впереди (Data, kpvotes).

## Outcome

Plan актуален. Tasks/Memory — explicit deferred, не "blocked" и не "in progress". Ссылки на artifacts (proposal + critique + decision-log) встроены прямо в Phase 30 чтобы будущий агент/ревьюер не искал контекст.

Decision-log создан с нуля — раньше файла не было, хотя AGENTS.md его упоминал. Первая запись — про deferring Tasks/Memory.
