# Снепшот состояния — измерено 2026-09-25

Источник: `tasks_methodology_list` / `tasks_board_list` / `tasks_workflow` / `tasks_search`
(`$system`, ключ проекта по умолчанию). Возраст = 2026-09-25 минус `createdAt` самого
старого открытого узла доски (найден через `tasks_search(statusKind:["open"], sort:{by:created})`,
дата декодирована из курсора пагинации — сами строки листинга `createdAt` не возвращают).

## Инстансы методологии (`tasks_methodology_list`)

| key | закрыт | доски | версия |
|---|---|---|---|
| `quartet` | нет | ideas, intake, spec, work | 23 |
| `smoke-classic-818` | да (2026-07-10) | — | 19 |
| `smoke-clone-818` | да (2026-07-10) | — | 20 |
| `smoke-verdict-gate` | да (2026-07-12) | — | 21 |

## Доски — узлы по статусу (open/terminalok/terminalcancel), terminal-доля, возраст старейшего открытого

| доска | слой | kind | закрыта | всего | open | terminal | terminal% | oldest open |
|---|---|---|---|---|---|---|---|---|
| ideas | quartet | ideas | нет | 227 | 54 | 173 | 76.2% | 2026-06-02 (~115д) |
| intake | quartet | intake | нет | 184 | 69 | 115 | 62.5% | 2026-06-04 (~113д) |
| spec | quartet | spec | нет | 483 | 457 | 26 | 5.4% | 2026-06-03 (~114д) |
| work | quartet | work | нет | 1102 | 131* | 971 | 88.1% | 2026-06-09 (~108д) |
| observations | $utility | observation | нет | 355 | 310 | 45 | 12.7% | 2026-08-28 (~28д) |
| classic | $utility | classic | нет | 6 | 2 | 4 | 66.7% | 2026-07-04 (~83д) |
| client-issues | $utility | simple | нет | 53 | 10 | 43** | 81.1% | 2026-07-08 (~79д) |
| roadmap | $utility | simple | **да** | 65 | 23 | 42 | 64.6% | 2026-05-31 (~117д, самый старый узел вообще) |
| wiki | $utility | wiki | нет | 18 | 18 | 0 | 0% | 2026-07-12 (~75д) |

\* work: 130 по штатным статусам (Pending117+InProgress4+Review9) + 1 узел со статусом
`pending` в нижнем регистре — не входит в FSM (`Pending`/`InProgress`/`Review`/`Blocked`/
`Done`/`Cancelled`), похоже на артефакт легаси-миграции (см. `e97e5e55`/`10869b42` в
`git log` этого репо — бэкфилл `NodeId`/нормализация статуса). Не проверено, попадает ли
он в обычные листинги.

\*\* client-issues: Done=38, Cancelled=4, `done` в нижнем регистре=1 (та же аномалия
регистра, что у work), Todo=9, InProgress=1 — терминал = 38+4+1=43.

## FSM по board kind (`tasks_workflow`)

| kind | open | terminalok | terminalcancel |
|---|---|---|---|
| ideas | raw, exploring, review, deferred | accepted, shipped | rejected |
| intake | reported, triage, confirmed | done | duplicate, wontfix |
| spec | defined | — (нет terminalok вовсе) | deprecated |
| work | Pending, InProgress, Review, Blocked | Done | Cancelled |
| observation | seen, promoted | fixed | declined |
| classic | Backlog, Todo, InProgress, Review | Done | Cancelled, Duplicate |
| simple (client-issues/roadmap) | Todo, InProgress, Blocked | Done | Cancelled |
| wiki | draft, live | promoted | stale |

Примечание: `spec` не имеет `terminalok`-статуса — узел спеки никогда не «завершается»,
только `defined` (457, 94.6%) или `deprecated` (26). `roadmap` — единственная закрытая
доска: узлы физически остаются `Todo`/`InProgress` (23 «открытых»), но доска не принимает
новых записей — де-факто заморожена целиком, а не по узлам.
