---
timestamp: 2026-06-01T11:30:00+03:00
agent: claude-code
model: claude-opus-4-8
target: plan
action: update
target_file: doc/plan.md
---

## What
Добавлена новая секция «## Phase 31: Production / Ops» в конец `doc/plan.md`. Подсекции: 31.1 — инцидент с OOM-зависаниями сервера (статус MITIGATED, чек-лист уже сделанного: swap, остановка yoba*; опциональный GC-кап) и 31.2 — **новая задача на профайлинг памяти PetBox.Web** (TODO).

## Why
Пользователь попросил добавить в roadmap задачу на профайлинг: почему PetBox.Web сожрал ~560 МБ (в 2–3× больше, чем YobaLog ~104 МБ / YobaConf ~78 МБ при похожем стеке) и почему RSS рос в простое без нагрузки. В плане не было Ops/Production раздела — создан новый.

## Args
- target: doc/plan.md, append после «### 25.6 — Follow-ups outside Polish phase» (был последней строкой, line ~1235).
- Добавлено: Phase 31.1 (3×[x] + 1×[ ] опц. GC-кап) и Phase 31.2 (5 шагов: dotnet-counters baseline, сравнение GC-режима с yoba*, dotnet-gcdump diff по типам, аудит фоновых сервисов/кэшей, решение GC-настройка vs баг на утечку).
- Линк на auto-memory `project_server_hang_incident`.

## Outcome
Успешно. petbox MCP в сессии недоступен (инструменты не экспонированы, проверено повторно) — план ведётся в файле `doc/plan.md`, как канонический источник. Перенести Phase 31 в MCP-доску при доступности.
