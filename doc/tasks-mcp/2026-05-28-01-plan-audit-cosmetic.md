---
timestamp: 2026-05-28T02:00:00+03:00
agent: claude-code
model: claude-opus-4-7
session: plan-audit
target: plan
action: update
target_file: doc/plan.md
---

## What
Косметическое обновление статусов фаз в `doc/plan.md` по итогам аудита (что фактически было сделано vs что помечено в плане).

Изменения:
- **Phase 2** Port yobalog Log: заголовок `[DONE]`. Пункт 2.5 "Remote auth real second instance" помечен `[-]` SKIPPED (нет окружения; логика покрыта unit-тестами).
- **Phase 3** Test parity: заголовок `[DONE]` (все пункты уже были `[x]` + `[-]` skips).
- **Phase 4** Dashboard + /admin route fix: заголовок `[DONE]`. Все 4 unchecked пункта помечены `[x]` с уточнениями реальной реализации (4.1 — после Phase 24 admin/index это sys overview, не редирект; 4.2 — dashboard на `/ui/{ws}` после Phase 21 IA rework, Index.cshtml имеет полный OnGetAsync с DefaultHome).
- **Phase 7** Remaining UI + polish: заголовок `[DONE]`. Все 7 пунктов `[x]` с реальными testid'ами и путями.
- **Phase 8** KpVotes E2E flow: заголовок `[DONE]` (внутри уже всё было `[x]`).
- **Phase 9** Config resolve priority: заголовок `[DONE]`.
- **Phase 10** ApiKey scope enforcement: заголовок `[DONE]`.

НЕ помечено DONE (реально требует работы или решения):
- **Phase 16** Data module rework — `[BLOCKED]`, нужно user clarification (3 вопроса).
- **Phase 22.8** Agent surface — `[BLOCKED — design needed]`, MCP vs REST.

## Why
В предыдущей итерации пользователь спросил "надо идти по активным и неполным, понять почему и что там не готово". Я провёл аудит — оказалось 6 из 8 "активных" фаз сделаны de facto, просто план не обновлялся синхронно с кодом. Пользователь подтвердил "да" на предложение применить cosmetic updates.

Это правильно: разрозненные `[ ]` чекбоксы в реально DONE фазах создают шум при чтении плана, маскируют реальные открытые задачи (16, 22.8).

## Args
Sections edited in `doc/plan.md`:
- Phase 2 heading + 2.5 last bullet (line ~144, ~186)
- Phase 3 heading (line ~190)
- Phase 4 heading + 4.1 (2 items) + 4.2 (4 items) (line ~212-226)
- Phase 7 heading + 7.1 (2) + 7.2 (2) + 7.3 (3) (line ~313-329)
- Phase 8 heading (line ~333)
- Phase 9 heading (line ~373)
- Phase 10 heading (line ~386)

## Outcome
Plan актуален. Открытые задачи сфокусированы на: 16 (Data module) + 22.8 (Agent surface) — оба требуют design-решения от пользователя. Polish-задачи разбросаны по 21.8, 23.6/23.7, 24.6.
