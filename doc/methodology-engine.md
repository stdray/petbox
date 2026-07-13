# Движок методологии: определение, пресеты, рантайм

Как устроен движок user-defined методологии — слой, который делает жизненный цикл работы в
проекте **данными проекта, а не кодом сервера**. Процессный канон (зачем такой процесс и как
по нему работать на `$system`) — в [methodology.md](./methodology.md); здесь — как движок
работает: модель определения, встроенные пресеты, резолв, гейты, миграция и MCP-поверхность.

Поставлен 2026-07-02 (ci.455, merge `6971d71`), спека — дерево `methodology-from-primitives`
(+ `artifacts-from-definition`) на `$system/spec`.

## Идея в одну строку

Раньше словарь процессов жил в хардкоде `WorkflowCatalog` (типы/статусы/переходы пяти видов
досок) плюс россыпь императивных гейтов в сервисе. Теперь **всё это — данные**: проект может
объявить собственное определение методологии, а встроенные процессы (квартет + simple)
выражены тем же языком данных как **пресеты**. `WorkflowCatalog` удалён.

Принцип (из exemplar-аудита Jira/Linear): МАЛЫЙ набор примитивов с опинионированными
дефолтами, не безграничные ручки. Трассировка («тип требует связь») — опциональный per-type
констрейнт, никогда не глобальный закон.

## Модель определения

Определение (`MethodologyDefinition`, `src/PetBox.Tasks/Workflow/MethodologyDefinition.cs`) —
структурный документ:

```
{ name,                                   — slug определения
  kinds: [{                               — виды досок
    kind,                                 — СВОБОДНЫЙ slug (не enum!), напр. "support"
    quickAddAllowed,                      — можно ли создавать с бордовой quick-add формы
    workflows: [{                         — блоки FSM: один блок = одна машина состояний,
      types: ["ticket","incident"],         разделяемая всеми типами блока
      statuses: [{ slug, name?, kind }],  — kind: open | terminalok | terminalcancel
      transitions: [{ from, to,
        requiresApproval?,                — переход только владельца (approve-гейт)
        requiresReason?,                  — требуется причина в body при переходе
        preconditionArtifact? }]          — тег artifact:<slug>, который должен висеть
    }],                                     комментом на узле ДО перехода
    linkConstraints: [{ type, link }]     — «новый узел типа T обязан нести связь K при
  }],                                       создании»; K ∈ task_spec|blocks|idea_spec
  linkKinds: [{ slug, description? }],    — виды связей инстанса для relations_create
  tagAxes: [{ namespace, description? }]  — объявленные оси тегов (namespace) инстанса
}
```

Конвенции: `statuses[0]` — начальный статус; первый тип первого блока — тип по умолчанию для
quick-add; порядок объявления значим. Валидация целостности — на весь документ до записи
(slug-форматы, ссылочная связность переходов, уникальность типов внутри kind, констрейнты
только на upsert-выразимые виды связей и объявленные типы).

## Хранение

Одно определение на проект: temporal SCD-2 синглтон (`MethodologyDefRow`, Key="methodology",
таблица `methodology_defs` в `data/tasks/{project}.db`, миграция M010). Каждая правка — новая
ревизия; оптимистичная конкуренция по baseline-версии (конфликт называет текущую версию);
идентичный пересабмит коллапсится в no-op (`changed:false`).

## Резолв: MethodologyRuntime

`MethodologyRuntime` (`src/PetBox.Tasks/Workflow/MethodologyRuntime.cs`) — единственный шов,
через который сервис узнаёт FSM/типы/оси/констрейнты доски. **Merge-семантика:**

- kind, объявленный определением проекта, резолвится **из определения**;
- любой другой kind (и весь проект без определения) — **из пресетов**, как раньше;
- проект может объявить один кастомный kind и продолжать пользоваться квартетом.

Определение читается один раз на сервисный вызов (SQLite локален — кэша нет), резолв
синхронный до построения запросов.

Enum `BoardKind` (Intake|Ideas|Spec|Work|Simple) остался, но только как **process-role** для
семантик, которые пока не примитивы: гейт accepted-идеи на запись в спеку, FSM-эффекты
(авто-закрытие интейка по `issue_task` при work→Done, разблокировка `blocks`), правило
синглтона квартетных досок, вычисляемый roll-up `delivery` на спек-доске, валидация `specBoard`. Кастомный
kind process-role не имеет (эффекты на нём не срабатывают).

## Пресеты: MethodologyPresets

`MethodologyPresets` (`src/PetBox.Tasks/Workflow/MethodologyPresets.cs`) — квартет и simple,
записанные тем же языком данных (snapshot-тест гарантирует 1:1 со старым каталогом):

| kind   | типы (default первым)             | статусы (initial первым)                                  | ключевые гейты |
|--------|-----------------------------------|-----------------------------------------------------------|----------------|
| intake | issue                             | reported, triage, confirmed, done✓, duplicate✕, wontfix✕  | triage→duplicate/wontfix — reason; confirmed→done — владелец |
| ideas  | idea                              | raw, exploring, review, deferred, accepted✓, rejected✕    | exploring→review — artifact:spec_plan; review→accepted — владелец; →rejected — reason |
| spec   | spec                              | defined, deprecated✕                                      | — |
| work   | feature, bug, chore               | Pending, InProgress, Review, Blocked, Done✓, Cancelled✕   | Review→Done — владелец; feature/bug — linkConstraint task_spec |
| simple | task, bug, feature, chore, issue  | Todo, InProgress, Blocked, Done✓, Cancelled✕              | переходы свободные (all-pairs) |

(✓ = terminalok, ✕ = terminalcancel.)

Бывшие императивные гейты теперь пресет-данные:

- требование specRef у work feature/bug = `linkConstraints` (а **отсутствие** констрейнта на
  `chore` — это и есть chore-исключение, бывший интеримный хардкод);
- spec_plan-гейт идей = `preconditionArtifact:"spec_plan"` на переходе exploring→review;
- оси `area|concern` = `tagAxes` квартетных пресетов; simple осей не объявляет — и работает
  **одно правило**: нет осей → теги свободные, есть оси → только `<ось>:значение`.

## Гейты: что и как enforced

- **requiresApproval** — смоделирован в данных и рендерится в гайд («агент никогда не
  выполняет Review→Done»), но серверный enforce выключен (`enforceApproval=false` в
  `WorkflowEngine`) — сейчас это конвенция агента, не запрет.
- **requiresReason** — enforced: переход отклоняется без непустого body.
- **preconditionArtifact** — enforced (`RequirePreconditionArtifactsAsync`): переход (и
  рождение сразу в гейт-статусе) отклоняется, пока на узле нет активного коммента с тегом
  `artifact:<slug>`.
- **linkConstraints** — enforced при создании (`RequireDefinitionLinks`): новый узел
  констрейнтнутого типа обязан нести ссылку в этом же вызове (`task_spec`=specRef,
  `blocks`=blockedBy, `idea_spec`=ideaRef). Правки существующих узлов связь не перетребуют.
- **tagAxes** — enforced в `SetTagsAsync`/`TagStore` по **рантайму доски** (board →
  methodology instance membership → instance rules; иначе legacy project-singleton def):
  оси объявлены → namespace тега должен быть из списка, «голые» теги отклоняются.
- **Словарь связей** — `relations_create` принимает: процессные виды (`task_spec`,
  `issue_task`, `idea_spec`, `blocks`, `part_of`, `supersedes` — несут эффекты/гейты),
  встроенные нейтральные (`relates_to`, `depends_on`, `mirrors` — свободные смысловые рёбра
  без эффектов) и `linkKinds` **инстанса FROM-узла** (тоже без эффектов). Неизвестный вид —
  отказ со списком допустимых для этого скоупа. Валидация словаря — в сервисе
  (`ITasksService.ValidateRelationKindAsync`), стор словарь не знает.
- **Скоуп инстанса (methodology-instance-scoped-axes):** `tagAxes` и объявленные
  `linkKinds` — авторитет инстанса методологии, не project-global. Переходный dual-read:
  доски без `MethodologyInstance` всё ещё читают project-singleton `MethodologyDefRow`
  (старый `tasks_methodology_def_upsert`); новые инстансы несут свои axes/linkKinds в
  rules document и изолированы друг от друга.

## Смена схемы: декларативная миграция

Изменение определения валидируется по живым узлам ДО записи: каждый активный узел затронутых
досок (kind объявлен старым или новым определением) проверяется против нового резолва.
Несовместимое (тип/статус, неизвестный новой схеме) должно быть покрыто **декларативным
маппингом** в том же вызове:

```
migration: [{ kind, types?: [{from,to}], statuses?: [{from,to}] }]
```

Маппинг применяется только там, где текущее значение невалидно (валидное никогда не
переписывается). Что осталось несмапленным — отказ всего вызова с именами доска/узел/значение,
**ничего не записано**. Смапленные узлы переписываются новыми temporal-ревизиями (маппинг и
есть санкционированный переход — FSM-гейты на перезаписи не гоняются), `migrated` в ack.
Атомарность честная: определение коммитится первым, конкурентная запись в доску во время
перезаписи даёт ошибку с именем доски и указанием, что НЕ переписано.

Сюда же попадает и первый ввод определения, перекрывающего пресетный kind с живыми досками, и
выпадение kind из определения (доски откатываются к пресетному резолву — несовместимость
ловится той же машинерией).

## MCP-поверхность

| Глагол | Что делает |
|--------|------------|
| `tasks_methodology_def_upsert` | записать определение (baseline-версия; опц. `migration`) |
| `tasks_methodology_def_get` | прочитать определение; без него — `defined:false, preset:"builtin-presets"` |
| `tasks_methodology_guide` | агентский onboarding-гайд, выведенный из данных: markdown + структурные инварианты (`approval_gate\|reason_required\|precondition_artifact\|link_constraint\|tag_axes`), source = presets\|definition\|mixed |
| `tasks_workflow` | FSM конкретной доски (кастомной или пресетной), переходы с `preconditionArtifact` |
| `tasks_board_create` | принимает и пресетные, и объявленные определением kind'ы |

Не путать: `tasks_methodology_get` — это ИНДЕКС квартетных досок (узлы/статусы), не
определение процесса.

`tasks_methodology_guide` — это «артефакты из данных» v1: скилл-текст процесса порождается
рантайм-выводом из определения (инвариант «агент никогда сам не ставит Done/accepted» —
следствие `requiresApproval` в данных, а не рукописный текст). Кодоген в файлы-артефакты —
следующая фаза.

## Код (карта файлов)

- `src/PetBox.Tasks/Workflow/MethodologyDefinition.cs` — модель определения.
- `src/PetBox.Tasks/Workflow/MethodologyPresets.cs` — квартет+simple как данные + ParseKind.
- `src/PetBox.Tasks/Workflow/MethodologyRuntime.cs` — шов резолва (definition-over-presets).
- `src/PetBox.Tasks/Workflow/MethodologyGuide.cs` — рендерер гайда + инварианты.
- `src/PetBox.Tasks/Workflow/MethodologyMigration.cs` — маппинги миграции.
- `src/PetBox.Tasks/Workflow/Workflow.cs`, `WorkflowEngine.cs` — FSM-словарь и валидация.
- `src/PetBox.Tasks/Validation/MethodologyDefinitionValidator.cs` — целостность документа.
- `src/PetBox.Tasks/Data/MethodologyDefRow.cs`, `Data/Migrations/M010_MethodologyDefs.cs`.
- `src/PetBox.Tasks/Services/TasksService.cs` — RuntimeAsync, гейты, DefineMethodologyAsync.
- Тесты: `tests/PetBox.Tests/Tasks/Methodology*Tests.cs` (definition/runtime/primitives/
  presets/migration/guide).

## Сознательно вне движка

- UI-редактор определения (v2; сейчас только MCP).
- Серверный enforce approve-гейта (флаг есть, выключен — включим, когда практика дозреет).
- Идентичность узлов `{board}-{n}` — отдельная идея (большая миграция ссылок).
- Process-role семантики кастомных kind'ов (эффекты вроде issue_task-автозакрытия на
  user-defined видах) — потребует новых примитивов, когда появится сценарий.
