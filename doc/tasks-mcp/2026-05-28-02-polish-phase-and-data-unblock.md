---
timestamp: 2026-05-28T03:00:00+03:00
agent: claude-code
model: claude-opus-4-7
session: plan-restructure
target: plan
action: update
target_file: doc/plan.md
---

## What
Большой restructuring `doc/plan.md`:

1. **Phase 22.8 Agent surface**: `[BLOCKED — design needed]` → `[SUPERSEDED by Phase 16 Wave 4]`. `/agent/` REST endpoint dropped. MCP-сервер через `ModelContextProtocol.AspNetCore` SDK реализуется в Data модуле Wave 4 (shared host pattern, единый `/mcp`). Tools от других модулей добавятся инкрементально.

2. **Phase 16 Data module**: `[BLOCKED]` → `[READY]`. Полностью переписана:
   - 7 resolved decisions inline (storage, API, mapping, scopes, schema management, MCP, существующая DataTables не трогаем)
   - 5 Wave'ов (Wave 0 critique gate, Wave 1 foundation+APIs, Wave 2 UI rework, Wave 3 real pet integration, Wave 4 MCP, Wave 5+ future)
   - Source of truth: `~/.claude/plans/noble-sniffing-bear.md` (session-plan с полной discovery/critique историей)
   - Старые 8.6/8.7 (DataTables onboarding) оставлены `[x]` для истории — исчезнут в Wave 2 rework

3. **NEW Phase 25: Polish [DEFERRED]** — единая точка для всех "пора, но не сейчас". 4 подсекции:
   - 25.1 UI navigation polish (sidebar tree state persistence, editor auto-tag, admin tabbed landing, cross-project log annotation, health dots)
   - 25.2 Validation polish (reserved-name validation, reserved path prefix validator)
   - 25.3 Auth/Settings polish (master key rotation CLI, DefaultHome.LastProject через MembershipSettings, [SettingsSection] group attribute)
   - 25.4 Polish from session plans (proud-waddling-naur.md "Phase 2: UI polish")

4. **Старые polish-секции neutralized**:
   - 21.8 → одна строка "перенесено в Phase 25"
   - 23.6 last bullet → перенесено
   - 23.7 → bullet list ссылок на 25.2/25.3, kept "L1 → L2 monitoring" note
   - 24.6 → "→ Phase 25 Polish"

## Why
Пользователь: "/agent/ REST endpoint выбрасываем (polish задача). Polish-задачи собираем в единую фазу. добиваем вопросы, которые мешают делать Data module (первый приоритет)".

- /agent/ REST: разрешено drop, MCP уже выбран в Data Wave 4 — нет смысла держать вопрос открытым.
- Polish consolidation: после Phase 23/24 audit стало видно что polish-items рассыпаны по 4 секциям + одной dropped секции (22.8). Единая фаза = понятнее scope, легче planning'а "когда взяться".
- Phase 16 unblock: вопросы answered в parallel-session plan `noble-sniffing-bear.md` — mirror'им решения в doc/plan.md чтобы Phase 16 был actionable.

## Args
Sections edited in `doc/plan.md`:
- 22.8 (line ~723-732) — STATUS изменён, описание переписано как SUPERSEDED
- Phase 16 (line ~609-642) — переписана полностью: header [READY], 7 resolved decisions inline, 5 waves, ссылка на session-plan
- 21.8 (line ~598) — bullets removed, one-line link to Phase 25
- 23.6 last bullet (line ~869) — replaced with link to 25.3
- 23.7 (line ~871) — header → POLISH, bullets replaced with brief links to 25.2/25.3
- 24.6 (line ~964) — replaced with link to Phase 25
- NEW Phase 25 (after 24, before 30) — full structure with 4 subsections

## Outcome
Plan актуален. Открытые задачи теперь сфокусированы:
- **Phase 16 Data module** (READY) — первый приоритет, можно стартовать Wave 0
- **Phase 25 Polish** (DEFERRED) — единая корзина для quality-of-life работы после Data + dogfooding

Phase 22.8 убрана как design-blocker (subsumed by Data Wave 4). Phase 30 Tasks/Memory остаётся DEFERRED pending experiments.
