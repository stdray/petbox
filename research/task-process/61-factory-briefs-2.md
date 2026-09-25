# 61 — Черновики брифов исполнителям, пункты 6–9 зонтика `work/task-process-fixes-2026-09`

Дата 2026-09-25, автор Opus 5.5 (worker-highstakes, разведка). Черновик, не коммитить.
Код читался через `git show origin/main:…`. Живые данные — MCP `$system`.

Предусловие для 6 и 7: идея должна быть в статусе `accepted`, а принимает её только владелец. До этого
не заводить узлы spec и work. Для 8 и 9 спека уже есть, и можно сразу заводить work-карточку
(`partOf: task-process-fixes-2026-09`, `links.task_spec`).

---

## Пункт 6 — снуз и повторение (идея `ideas/recurring-run-scheduler`, review, spec_plan `29d93c59…`)

Цепочка после accept:
1. Spec: зонт `scheduled-wake` (partOf `flow-control-owner-queue-foundations`) и листья `node-snooze-until`,
   `snooze-wakes-without-a-human`, `snooze-wake-addressee`, `owner-digest-shows-woken`, `recurring-card-rule`,
   `recurring-card-no-pileup`. Текст листьев брать дословно из spec_plan, у каждого `links.idea_spec: recurring-run-scheduler`.
2. Work, две карточки `feature`:
   - `node-snooze-and-wake-job` (task_spec → 4 листа снуза).
   - `recurring-card-rules` (task_spec → 2 листа повторения; blockedBy первой — та же джоба).

### Бриф A: `node-snooze-and-wake-job`
- **Модель.** В `TaskNode` (`src/PetBox.Tasks/Data`, TasksDb, файл `data/tasks/{project}.db`) добавить `SnoozeUntil`
  (DateTime?), `SnoozeReason` (string?), `SnoozeWakeTo` (`agent|owner`, по умолчанию agent), `WokeAt` (DateTime?).
  FluentMigrator-миграция в модуле Tasks. **Round-trip тест INSERT→SELECT обязателен**: у PetBoxDb мы уже
  теряли колонку, которую не объявили в маппинге, а запись при этом отчитывалась об успехе. Проверить, как
  маппится TasksDb: атрибутами на модели или fluent — и объявить колонку там же.
- **MCP/REST.** В `tasks_upsert` на узле поле `snooze: {until, reason?, wakeTo?}`; `snooze: null` снимает
  отложенность. Узел без `until` — отказ в `conflicts[]` («без даты не всплывёт»). В `tasks_search` фильтры
  `snoozed:bool` и `woke:bool`. Поля выводить в `tasks_node_get`.
- **Джоба.** `BackgroundService`, раз в сутки плюс один проход при старте, по образцу
  `src/PetBox.Log.Core/Retention/RetentionService.cs`. Регистрировать под флагом `Features:Tasks`.
  Для каждого проекта: открытые узлы с `SnoozeUntil <= now` получают `WokeAt=now`, отложенность снимается; при
  `wakeTo=owner` ставится `decisionPending=true`. У терминальных узлов отложенность снимается молча. Ставить
  `TimeProvider`. Соединение брать только через фабрику (`IScopedDbFactory<TasksDb>`), инъекции нет.
  Идемпотентность: повторный прогон в тот же день ничего не меняет.
- **Дайджест.** В `OwnerDigestService` (`src/PetBox.Tasks/Services/OwnerDigestService.cs`) добавить секцию
  «проснулось»: количество за окно, сколько из них `owner` и их список. Когда появится секция Review из пункта 8,
  держать обе в одном порядке секций.
- **Данные инстанса.** Два узла `ideas` в `deferred` (`error-detail-budget`, `server-artifact-surface`) перевести в
  `exploring`, дать снуз (дату согласовать; предложение — +90 дней, wakeTo=owner) и убрать `deferred` из FSM
  `ideas` через `tasks_methodology_rules_upsert`. Образец — `WorkDeferredStatusMigrator`.
- **Приёмка.** Unit-тесты на поддельном времени: пробуждение один раз; agent — без флага; owner — с флагом;
  терминальный — без пробуждения. Cake `Test` зелёный. Живой смоук только на проекте `smoke` ключом sandboxOnly:
  снуз на вчера, дождаться прохода при старте после деплоя или дёрнуть джобу, проверить `woke:true` в
  `tasks_search` и секцию в `tasks_owner_digest`. После смоука убрать за собой.

### Бриф B: `recurring-card-rules`
- Правило — строка в TasksDb: `{id, projectKey, board, type, title, body, tags[], period: day|week|month, wakeTo,
  lastFiredAt, openNodeId?}`. Минимальный глагол `tasks_recurring_upsert` / `_list` / `_delete`, scope `tasks:write`.
  FSM нет, UI нет.
- В той же джобе второй проход: правило «созрело» (`lastFiredAt + period <= now`) → если `openNodeId` открыт,
  узел не создавать, а поставить на открытую карточку отметку о пропуске (счётчик в теле или теге
  `recurring-missed:N` — выбрать и объяснить); иначе создать карточку по шаблону со ссылкой на правило
  (relates_to или тег `recurring:<id>`), `decisionPending = (wakeTo==owner)`.
- Первое правило (завести после деплоя, НЕ в смоуке): «Месячный аудит взаимодействия агентов» → `work`, chore,
  тело — ссылка на `doc/agent-interaction-audit.md`, wakeTo=agent.
- Приёмка: тест «открытая карточка → второй не будет, пропуск посчитан»; живой смоук на `smoke`, период day.

---

## Пункт 7 — предупреждения (идея `ideas/discipline-rules-warn-in-tool-response`, review, spec_plan `88424c65…`)

После accept: зонт `write-response-warnings` (partOf `methodology-from-primitives`) и листья `write-warnings-channel`,
`convention-approval-gate-warns`, `terminal-ok-without-commits-warns`, `unlinked-intake-twin-warns`. Work:
одна `feature` `tasks-upsert-warnings` (task_spec → 4 листа). Добавить relation `relates_to` на
`work/methodology-issue-task-enforce-and-zombie-sweep`, а у той в пункте (a) дописать «в форме предупреждения — см. эту карточку».

### Бриф C: `tasks-upsert-warnings`
- `UpsertResultView` (`src/PetBox.Tasks/Contract/TaskViews.cs:157`) получает `IReadOnlyList<UpsertWarningView>? Warnings`
  вида `{Rule, Key, Message}`. Пусто — `null`, по образцу `Deduped`. Протащить в MCP (`TasksTools`), REST и типы SDK
  (ts, py, net). Раз SDK меняется, гейт — `Verify`, не только `Test`.
- **Правило `convention-approval-gate`.** Переход с `RequiresApproval && !EnforceApproval` при `actor.CanApprove==false`
  (актор уже вычисляется, `TasksService.cs:~3107`, MCP `TasksTools.cs:~1644`). Сообщение называет переход и говорит,
  что по конвенции его делает владелец.
- **Правило `terminal-ok-without-commits`.** Добавить в схему методологии признак типа `carriesCommits: bool`
  (по умолчанию false, чтобы не ломать сохранённые документы). Валидатор и гайд должны его знать (гайд — одна
  строка в инвариантах). В правилах инстанса `quartet` выставить его для `feature` и `bug`. Предупреждение:
  переход в TerminalOk, у узла `commits[]` пуст и после применения патча.
- **Правило `unlinked-intake-twin`.** При СОЗДАНИИ узла на доске, которая является целью `issue_task`, найти
  открытые узлы доски-источника без исходящей `issue_task` и сравнить title+body на лету через эмбеддер —
  тот же путь, что в `src/PetBox.Web/Tasks/ObservationDedupService.cs`. Порог — свой option, не 0.75 от
  наблюдений. Калибровать на паре `intake/add-chore-type-to-classic-preset` ↔ `work/chore-type-in-classic-preset`
  и на 3–5 несвязанных парах; числа занести в verdict. Без эмбеддера — тихо без предупреждения. Вторая ветка:
  переход intake в TerminalOk без исходящей `issue_task`. Вид связи и пару досок брать из `linkKinds` и
  link-constraint методологии, не из литералов.
- Проверка идёт ПОСЛЕ успешного применения и не может превратить `applied:true` в отказ. Исключение внутри
  проверки глотается, логируется и не пишется в warnings.
- **Приёмка.** Два теста на каждое правило (есть нарушение и нет его). Cake `Verify` зелёный. Живой смоук на
  `smoke` ключом без `tasks:approve`: Review→Done без коммитов даёт два предупреждения; intake-двойник даёт одно.

---

## Пункт 8 — секция Review в дайджесте (новая идея НЕ нужна)

Покрывает существующий лист `spec/owner-away-digest`: «дайджест … упорядоченный по **требуемому от него
действию**». Переход владельца из Review и есть такое действие. Кроме того, flow-control 28.08 уже решил,
что «ждёт вас» = `decisionPending:true ∪ узел в статусе, из которого выходит approval-переход` («правка
ЧИТАТЕЛЯ, а не модели»). Лист `owner-decision-pending-derived-from-gate` deprecated как раз в пользу этого.
Кода нет: `OwnerDigestService.AwaitingAsync` читает только флаг.

### Бриф D: work `feature` `owner-digest-shows-approval-gated` (task_spec → `owner-away-digest`, partOf зонтика)
- В `OwnerDigestService` новая секция: открытые узлы доски в статусе, из которого по FSM этой доски
  (`GetBoardWorkflowAsync`, уже вызывается) выходит переход с `requiresApproval`. Для work это Review,
  для ideas — review, для intake — confirmed. Группировка по тегу `area` (как у `NewCohorts`, с тем же бакетом
  NoArea), total на кластер, признак `crowded` при total > 5 (порог — константа с комментарием). Узлы, уже
  стоящие в `AwaitingDecision` по флагу, не дублировать.
- Протащить в MCP `tasks_owner_digest` и страницу `/ui/{ws}/{project}/digest/{board}` (Razor; `data-testid`;
  без inline JS).
- **Приёмка.** Тест рендера: срез с Review-карточкой без флага показывает её, кластер из 6 помечен.
  Живая проверка: `tasks_owner_digest board:work` на `$system` — чтение разрешено — показывает текущие Review
  (на 25.09 их 8, из них 7 в `area:agent-wiring`).
- **Решение владельцу:** не требуется — spec и решение 28.08 уже есть.

---

## Пункт 9 — корректность дедупа наблюдений (новая идея НЕ нужна)

Покрывает существующий лист `spec/observation-recurrence-is-ranked`: «При повторной находке **того же**
наблюдения система ДОЛЖНА накапливать рецидив на существующем узле». Он нарушается в обе стороны:
- ложное слияние: `observations/observation-semantic-dedup-folds-a-correcting-finding-onto-the-node-it-contradicts`
  — другая, опровергающая находка записана как рецидив;
- пропуски: `observation-reverify-by-edit-does-not-bump-recurrence` (перепроверка через правку тела),
  `observation-twin-merge-cannot-transfer-recurrence` (ручное слияние близнеца не переносит вес), RU/EN пара
  `codex-hook-trust-gate-recheck-0155-0157` ↔ `obs-dbd57e835dcf` (18-D8 §2a).
`intake/duplicate-of-relation-and-requireslink` сюда НЕ тянуть: она про общий статус Duplicate в classic/intake,
не триажирована («владелец не уверен») и требует нового уровня гейта `RequiresLink`.

### Бриф E: work `bug` `observation-dedup-false-merge-and-missed-repeat` (task_spec → `observation-recurrence-is-ranked`)
- **(1) Ложное слияние.** В `tasks_upsert` на доске observations добавить параметр узла «исправляет X»
  (например, `links.corrects: <key>`; если вид связи не builtin — объявить или использовать `relates_to` + флаг).
  При нём дедуп исключает X из кандидатов (оба прохода), а связь ставится. Проверить, отключается ли так
  случай из наблюдения. Порог 0.75 НЕ трогать (Р12).
- **(2) Ручное слияние.** Глагол `tasks_observation_merge {twin, original}`: переносит `recurrenceCount` (сумма),
  `lastSeenAt` (max), объединяет `originSessions`, закрывает близнеца `declined` с reason, ставит связь.
  Атомарно. Регрессионный сигнал (`recurredAfterFixAt`) у fixed-оригинала должен сработать так же, как при
  дедуп-хите.
- **(3) Перепроверка через правку.** В `ObservationDedupService` сейчас только create-батчи (`:52`). Добавить
  явный признак на апсерте существующего наблюдения (`reverified: true`), который бампает счётчик и
  `lastSeenAt` через тот же `RecordObservationRecurrenceAsync`. Бамп по любой правке тела отвергнут:
  опечатку нельзя считать рецидивом.
- **(4) RU/EN.** Сначала замер: косинус пары выше, на текущем эмбеддере. Если он ниже порога — варианты
  (эмбеддить только title, нормализовать язык через `llm_chat` перед эмбеддингом, отдельный кросс-языковой
  порог) принести с числами в verdict; порог глобально не двигать. Если на замере не чинится дёшево — отделить
  в свою карточку и сказать об этом, а не молча урезать.
- После фикса промоутить три наблюдения в эту карточку (`tasks_observation_promote`, obligation) — при Done
  они сами станут fixed.
- **Приёмка.** Четыре сценария из наблюдений воспроизведены тестами и зелёные; тесты на порог не менялись.
  Живой смоук на `smoke`: пара «утверждение + опровержение с `corrects`» даёт два узла; merge переносит счётчик.

---

## Развилки, которые видны уже сейчас

- **Пункт 6, адресат пробуждения.** Реестр 30 будит в `decisionPending`, резерв 50 — агента. Сделано по 50:
  по умолчанию агент, владелец — только явно. Если владелец ответит «нет», меняется одна строка в листе
  `snooze-wake-addressee` и значение по умолчанию.
- **Пункт 7, делегированный Done.** Предупреждение сработает и на карточках этого зонтика, где Done агенту
  разрешён. Подавление намеренно не делается, основание пишется в verdict.
- **Пункт 9, (4) RU/EN.** Может оказаться дорогим: ветка 4 вынесена в условную отдельную карточку.
