# Tasks methodology — design (draft, на ветке `design/tasks-methodology`)

> Статус: **дизайн, не реализация.** Это выгрузка модели из обсуждения в схемы + текст, чтобы её можно
> было посмотреть глазами и поймать нестыковки. Диаграммы — в [`tasks-methodology.puml`](./tasks-methodology.puml).
> Полный план (с фазами и миграцией) — в `~/.claude/plans/sequential-noodling-thimble.md`.

## Как смотреть схемы
- **Уже отрендерено:** [`diagrams/*.svg`](./diagrams) (7 файлов `1-db-schema.svg` … `7-dogfood-e2e.svg`) — открой в браузере/превью, ничего ставить не надо.
- VS Code + расширение **PlantUML** → открыть `.puml`, Alt+D (preview).
- Или: `plantuml doc/design/tasks-methodology.puml` → 7 изображений (`1-db-schema.png` … `7-dogfood-e2e.png`).
- Или: скопировать любой `@startuml…@enduml` блок на **plantuml.com/uml**.

## Зачем (рамка)
Разработка идёт **агентами**; дизайним **под команду**; код стал дёшев → узкое место не «написать», а
**помнить и обозревать** проект. В этой рамке **спека — главный актив** (из неё — приёмочные тесты, аудит,
пересборка агентом). Боли: `Done`≠approved; теряется мышление; состояние размазано + ссылки гниют; «backlog»
— не доска, а статус.

## Согласованные решения
1. **Связи (relations) — сущность первого рода, с самого начала.** Направленные типизированные темпоральные
   рёбра `{kind, from:NodeId, to:NodeId}`. У ребра нет своего статуса. Индекс по обоим концам.
2. **Стабильный `NodeId`** на `TemporalRow` — переживает ревизии и переименование (`Key` меняется, `NodeId`
   нет). Рёбра держат концы за `NodeId`, не за `Key` → не гниют. *(Проверено: сейчас стабильного id нет ни у
   узла, ни у доски.)*
3. **Единая БД tasks-домена** (реестр досок + все узлы с колонкой `Board` + `relations`). FK возможен; `NodeId`
   глобально уникален → конец ребра = просто `NodeId`. Memory/Sessions/Core — пока отдельно.
4. **Типы задач = `feature` | `bug`** (`chore` выброшен — не маппится на требование). Не-спека-обслуживание →
   `free`-доска. Типы — данные/пресеты.
5. **Forcing function спеки:** завести feature/bug на work = сослаться на лист спеки (`task→spec-leaf`).
6. **Доски разделяют быстрый захват и обязательную работу:** ideas/intake — без spec-link; work — со spec-link.
7. **FSM с эффектами — ядро.** Переход = (from→to) + guard(ы) + effect(ы). Guard: предикат (approve-gate — это
   guard). Effect: DI-обработчик после коммита, ходит по связям (пример: work-bug→`done` ⇒ закрыть связанный
   intake-issue). Автомат и обработчики тестируются порознь.
8. **Approve-gate** = guard: агент (ambient identity, A6) не ставит терминальный `Done`, потолок `Review`.
9. **`board.kind`** ∈ {spec, ideas, intake, work, free}, default free. Kind задаёт типы/статусы **и инварианты/эффекты**.

## Сущности
- **Session** — сырьё. **Idea/Deliberation** (ideas) — нить мышления + исход. **Spec/Feature** (spec) —
  темпоральное дерево определённых требований, статус вычисляемый. **Task** (work) — feature/bug, обязан
  линковать лист спеки. **Issue** (intake, source agent|user) — быстрый захват; confirmed → порождает task.
  **Relation** — типизированное ребро (концы = `NodeId`).

## Виды рёбер
`task→spec-leaf` (реализует, питает rollup) · `issue→task` (провенанс + авто-закрытие) · `idea→spec` (почему
лист такой) · `task blocks task` (оживляет `blocked`) · `spec-leaf↔NFR-leaf` (M:N) · возможно `task dup task`.
Минимум первой поставки: `task→spec-leaf` + `issue→task`.

## Фазы (каждая shippable; B раньше C/D/E)
- **A** — фундамент: снять прод-BLOCKER per-file записи + неопаковые MCP-ошибки.
- **B** — единая БД + `Relation` + `NodeId`.
- **C** — Spec board + обязательный `task→spec` + `Review`/approve-gate.
- **D** — ideas + intake + промоут `issue→task` по ребру.
- **E** — FSM-движок guard+effect; первый эффект — кросс-бордное авто-закрытие.
- **Позже** — вычисляемый rollup спеки; NFR-ветки; редактируемые воркфлоу + DSL/UI; team-эффекты.

## Открытые вопросы (до кода)
1. **Spec board как сущность** — форма дерева, что есть «лист», версии, как идея → версия спеки.
2. **Первый коммит** — Phase A (BLOCKER) или сразу B (единая БД + NodeId + relations)?
3. **Объём слияния БД** — только tasks-домен или сразу втянуть Memory/Sessions?
