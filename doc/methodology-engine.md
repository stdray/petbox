# Движок методологии: правила, инстансы, пресеты, рантайм

Как устроен движок user-defined методологии — слой, который делает жизненный цикл работы в
проекте **данными проекта, а не кодом сервера**. Процессный канон (зачем такой процесс и как
по нему работать на `$system`) — в [methodology.md](./methodology.md); здесь — как движок
работает: документ правил, инстансы, встроенные пресеты, резолв, гейты, миграция и
MCP-поверхность.

Поставлен 2026-07-02 (ci.455, merge `6971d71`), спека — дерево `methodology-from-primitives`
(+ `artifacts-from-definition`) на `$system/spec`. Позже единственное определение на проект
было заменено **именованными инстансами** (`methodology-instance-core`,
`methodology-active-instance`, `methodology-utility-kinds`) — см. «Хранение» и «Резолв».

## Идея в одну строку

Раньше словарь процессов жил в хардкоде `WorkflowCatalog` (типы/статусы/переходы пяти видов
досок) плюс россыпь императивных гейтов в сервисе. Теперь **всё это — данные**: живой процесс
проекта — это **инстанс методологии**, несущий собственный документ правил, а встроенные
процессы (`quartet` | `classic` | `simple`) выражены тем же языком данных как **пресеты**
(и как встроенные шаблоны). `WorkflowCatalog` удалён.

Принцип (из exemplar-аудита Jira/Linear): МАЛЫЙ набор примитивов с опинионированными
дефолтами, не безграничные ручки. Трассировка («тип требует связь») — опциональный per-type
констрейнт, никогда не глобальный закон.

## Модель правил: один документ, три роли

`MethodologyDefinition` (`src/PetBox.Tasks.Engine/MethodologyDefinition.cs`) — структурный
документ процесса. Форма ОДНА, а ролей у неё три, и это ключ ко всей модели:

- **правила инстанса** — живой процесс (`tasks_methodology_rules_get` / `_rules_upsert`);
- **шаблон** — инертный документ, из которого инстанс рождается (`tasks_methodology_template_*`);
- **utility-слой проекта** — виды досок вне всякого процесса (`tasks_methodology_utility_get` /
  `_utility_upsert`).

```
{ name,          — прозвище документа; НЕ адрес (адресует `key`)
  strictMode,    — дефолт «сервер блокирует approve-гейт» для видов ЭТОГО документа
  kinds: [{
    kind,                 — СВОБОДНЫЙ slug (не enum!), напр. "support"
    quickAddAllowed,      — можно ли создавать с бордовой quick-add формы
    singleton,            — процесс-роль: ≤1 открытой доски этого вида В СВОЁМ МИРЕ
    workflows: [{         — блок FSM: одна машина состояний на все типы блока
      types: ["ticket","incident"],
      statuses: [{ slug, name?, kind }],        — kind: open|terminalok|terminalcancel
      transitions: [{ from, to, requiresApproval?, requiredArtifacts?, enforce?, checklist? }]
    }],
    linkConstraints: [{ type, link, targetKind?, targetStatuses? }],
                          — «новый узел типа T обязан нести связь K», и чем обязан быть адресат
    effects: [{ on, link, direction, set?, onlyFrom?, onLeave? }],
                          — кросс-узловая автоматика на входе/выходе статуса
    delivery: { requiredTypes, defectTypes, link },
                          — вычисляемый roll-up доставки по входящим рёбрам `link`
    autoWireFrom,         — авто-wire на единственную доску вида-источника
    blocksGate: { status, releaseTo },
                          — статус «обязан назвать блокера» и куда уходит освобождённый узел
    defaultView, outlineReveal, boardName      — представление и имя доски по умолчанию
  }],
  linkKinds: [{ slug, category?, direction?, description? }],
                 — виды связей ЭТОГО документа для relations_create
  tagAxes: [{ namespace, description? }]      — объявленные оси тегов (namespace)
}
```

`requiredArtifacts` — это `[{ slug, inline }]`: inline-артефакт (сегодня только `reason`) едет
полем того же вызова, не-inline обязан уже висеть на узле комментом с тегом `artifact:<slug>`.
`enforce: { approval?, artifacts? }` — что из объявленного сервер РЕАЛЬНО блокирует;
`checklist` — свободные условия, конвенция, сервер их не проверяет никогда.

Легаси-форма гейта на переходе — `requiresReason` / `preconditionArtifact`; она читается 1:1 в
`requiredArtifacts` (`reason` — это артефакт с `inline:true`, отдельного reason-гейта нет), но
смешивать обе формы на одном переходе валидатор запрещает. У каждого примитива (вид, статус,
переход, эффект, констрейнт, linkKind, tagAxis) есть ещё свободное `description` — правится
точечно по натуральному ключу через `tasks_methodology_set_description`, без CAS-перезаписи
всего документа.

Конвенции: `statuses[0]` — начальный статус; первый тип первого блока — тип по умолчанию для
quick-add; порядок объявления значим. Валидация целостности — на весь документ до записи
(slug-форматы, ссылочная связность переходов, уникальность типов внутри kind, констрейнты
только на upsert-выразимые виды связей и объявленные типы).

Запись правил — всегда **замена всего документа**: поле, опущенное где угодно внутри
`definition`, СТИРАЕТСЯ, а не остаётся как было. Поэтому цикл — прочитать, поправить, отправить
целиком; арх-тест `MethodologyKindContractParityTests` держит wire-контракт
(`MethodologyKindInput`) поле-в-поле с доменной моделью именно потому, что забытое поле здесь
молча вытирается на каждой правке.

## Хранение: инстансы, а не синглтон проекта

Живой процесс — это **именованный инстанс методологии**, адресуемый slug-ключом (`key`).
Проектов-синглтонов у методологии больше нет: в проекте может быть НЕСКОЛЬКО инстансов
одновременно, открытых и закрытых.

Всё лежит в `data/tasks/{project}.db`:

| таблица | что это | миграция |
|---------|---------|----------|
| `methodology_instances` | правила инстанса + флаг закрытия; Key = slug инстанса | M013 |
| `methodology_active_instance` | указатель проекта на активный инстанс (синглтон) | M017 |
| `methodology_templates` | именованные ИНЕРТНЫЕ шаблоны (досок не создают) | M012 |
| `methodology_defs` | документ **utility-слоя** проекта, Key="methodology" | M010 |

Все четыре — temporal SCD-2: каждая правка новая ревизия, оптимистичная конкуренция по
baseline-версии (конфликт называет текущую версию), идентичный пересабмит коллапсится в no-op
(`changed:false`).

`methodology_defs` — та самая бывшая таблица «одно определение на проект». Она никуда не
делась, но сменила смысл: теперь это **utility-слой** — виды досок, живущие на проекте, а не
внутри процесса, и потому переживающие смену методологии. Инстансы её не читают НИКОГДА.

Инстанс рождается одним актом — `tasks_methodology_create` — из ЯВНОГО источника (`source` =
`builtin` | `template` | `instance`, `sourceKey` — соответственно slug пресета `quartet` |
`classic` | `simple`, ключ шаблона или ключ другого инстанса): записывает правила и
провиженит по доске на каждый вид из источника. Молчаливого умолчания на квартет нет.
`tasks_methodology_close` закрывает инстанс вместе с досками (история читается, запись
отклоняется).

Членство доски в мире хранится не здесь, а на мете доски (`TaskBoards.MethodologyInstance` в
core-БД): либо ключ инстанса, либо зарезервированный сентинел `$utility`. Доска состоит ровно
в ОДНОМ мире; `tasks_board_adopt` переносит её из мира в мир.

## Резолв: MethodologyRuntime

`MethodologyRuntime` (`src/PetBox.Tasks.Engine/MethodologyRuntime.cs`) — единственный шов,
через который сервис узнаёт FSM/типы/оси/констрейнты доски. Сам рантайм — это обёртка вокруг
ОДНОГО документа с той же merge-семантикой, что и раньше:

- kind, объявленный документом, резолвится **из документа**;
- любой другой kind (и пустой документ) — **из пресетов**;
- значит, мир может объявить один кастомный kind и продолжать пользоваться квартетом.

Новое — то, ЧЕЙ документ попадает в рантайм. Когда доска на руках, решает её мир
(`RuntimeForBoardAsync`), и это три пути, а не два:

- сентинел `$utility` → utility-слой проекта (`methodology_defs`) — стабильно при любой смене
  активной методологии, в этом весь смысл слоя;
- имя инстанса → правила ЭТОГО инстанса;
- пусто (легаси-состояние до бэкфилла, новыми досками намеренно недостижимое) → проектный резолв
  ниже. К `methodology_defs` этот путь не откатывается.

Когда доски на руках НЕТ (гид, quick-add до выбора доски), работает проектный резолв
(`RuntimeAsync`, спека `methodology-active-instance`):

1. явный указатель активного инстанса (`tasks_methodology_active_get` /
   `tasks_methodology_set_active`), если он стоит и указывает на ОТКРЫТЫЙ инстанс;
2. иначе — единственный открытый инстанс, если открыт ровно один;
3. иначе — встроенные пресеты, а `tasks_methodology_guide` отдаёт ЯВНЫЙ гид «N открытых, ни
   один не активен», перечисляя их поимённо.

Третий пункт — принципиальный: прежняя эвристика молча СЛИВАЛА kinds/linkKinds/tagAxes
нескольких открытых инстансов («первый по имени выигрывает конфликт»). Слияния больше нет —
неоднозначность стала видимым состоянием. Указатель управляет только ДЕФОЛТАМИ: членство доски
в мире всегда сильнее него.

Документ читается один раз на сервисный вызов (SQLite локален — кэша нет), резолв синхронный
до построения запросов.

Enum `BoardKind` (Intake|Ideas|Spec|Work|Classic|Simple) остался, но только как **process-role**
для семантик, которые пока не примитивы, — сейчас это выбор пресета для необъявленного вида.
Часть бывших process-role семантик уже стала данными вида: правило синглтона — `singleton`,
авто-wire и валидация `wiredBoard` — `autoWireFrom` (`TasksService.ValidateWiredBoardAsync`:
источник — работа, чей вид объявляет `autoWireFrom`, а цель обязана быть ровно того вида, что
он называет — не enum-проверка), roll-up доставки — `delivery`, гейт accepted-идеи и требование
specRef — `linkConstraints` с `targetKind`/`targetStatuses`, авто-закрытие интейка и
разблокировка `blocks` — `effects`. Кастомный вид может объявить их все.

## Пресеты: MethodologyPresets

`MethodologyPresets` (`src/PetBox.Tasks.Engine/MethodologyPresets.cs`) — квартет, classic и
simple, записанные тем же языком данных (snapshot-тест гарантирует 1:1 со старым каталогом):

| kind    | типы (default первым)             | статусы (initial первым)                                        | ключевые гейты |
|---------|-----------------------------------|-----------------------------------------------------------------|----------------|
| intake  | issue                             | reported, triage, confirmed, duplicate✕, wontfix✕, done✓        | triage→duplicate/wontfix — reason; confirmed→done — владелец |
| ideas   | idea                              | raw, exploring, review, deferred, accepted✓, rejected✕          | exploring→review — artifact:spec_plan; review→accepted — владелец; →rejected — reason |
| spec    | spec                              | defined, deprecated✕                                            | КАЖДАЯ запись — linkConstraint `idea_spec` → узел `ideas` в статусе accepted |
| work    | feature, bug, chore               | Pending, InProgress, Review, Done✓, Blocked, Cancelled✕         | Review→Done — владелец; feature/bug — linkConstraint `task_spec` → узел `spec` |
| classic | task, feature, bug                | Backlog, Todo, InProgress, Review, Done✓, Cancelled✕, Duplicate✕ | среди открытых — свободно; →Duplicate — reason; Done ТОЛЬКО из Review, владелец |
| simple  | task, bug, feature, chore, issue  | Todo, InProgress, Blocked, Done✓, Cancelled✕                    | переходы свободные (all-pairs) |

(✓ = terminalok, ✕ = terminalcancel.)

Провижен-пресетов (то, что создаётся как единица) два: `quartet` — четыре singleton-доски
intake→ideas→spec→work с авто-wire work→spec, и `classic` — одна standalone-доска. Встроенных
ШАБЛОНОВ три: `quartet` | `classic` | `simple` — их читает `tasks_methodology_template_get`
с `source:"builtin"`, и любой из них годится в `sourceKey` для `tasks_methodology_create`.
`simple` не провижен-единица потому, что пустая доска и так создаётся видом `simple`.

Бывшие императивные гейты теперь пресет-данные:

- требование specRef у work feature/bug = `linkConstraints` (а **отсутствие** констрейнта на
  `chore` — это и есть chore-исключение, бывший интеримный хардкод);
- гейт «спека пишется только под принятую идею» = `linkConstraints` с
  `targetKind:"ideas"`, `targetStatuses:["accepted"]` (был хардкод `RequireAcceptedIdeaForSpec`);
- spec_plan-гейт идей = `preconditionArtifact:"spec_plan"` на переходе exploring→review;
- авто-закрытие интейка и разблокировка `blocks` при work→Done = `effects` вида `work`;
- «Blocked обязан назвать блокера» = `blocksGate` вида `work` (инвариант СОСТОЯНИЯ, проверяется
  на каждой записи, включая рождение, а не гейт перехода);
- оси `area|concern` = `tagAxes` квартетных пресетов; classic и simple осей не объявляют — и
  работает **одно правило**: нет осей → теги свободные, есть оси → только `<ось>:значение`.

Пресет — это **базовая линия там, где не применён открытый инстанс**: вид, который документ
мира не объявляет, резолвится отсюда. При этом провижен инстанса КОПИРУЕТ пресетные виды в
хранимый документ дословно — поэтому новое поле, добавленное в пресет позже, на уже
провижененном проекте само не появится.

## Гейты: что и как enforced

Объявление гейта и его СИЛА — две разные вещи (`methodology-gate-strictness`): переход говорит,
ЧТО требуется, а `enforce` — блокирует ли это сервер. Гид рендерит обе стороны, помечая гейт
как enforced или как конвенцию.

- **requiresApproval** — смоделирован в данных и рендерится в гайд («агент никогда не
  выполняет Review→Done»). Блокирует ли сервер, решает `enforce.approval` перехода, а при его
  отсутствии — `strictMode` документа. Во ВСЕХ встроенных пресетах не задано ни то, ни другое,
  поэтому approve-гейт квартета — конвенция агента, а не запрет.
- **requiredArtifacts / requiresReason** — enforced по умолчанию (`enforce.artifacts`, если не
  задан, читается как `true`): переход отклоняется без непустого поля `reason` в ТОМ ЖЕ вызове
  (не в body узла).
- **preconditionArtifact** — enforced (`RequirePreconditionArtifacts`): переход (и рождение
  сразу в гейт-статусе) отклоняется, пока на узле нет активного коммента с тегом
  `artifact:<slug>`. Ceiling v1: на переходе реально проверяются первый inline- и первый
  не-inline артефакт, сколько бы их ни было объявлено.
- **linkConstraints** — enforced при создании (`GuardEngine`): новый узел констрейнтнутого типа
  обязан нести ссылку в этом же вызове (`links:{kind:ref}`; `blockedBy` — сахар для builtin
  `blocks`), а `targetKind`/`targetStatuses` дополнительно проверяют, ЧЕМ обязан быть адресат.
  Правки существующих узлов связь не перетребуют — кроме `spec`, где констрейнт провенанса
  висит на каждой записи.
- **checklist** — не enforced никогда: свободные условия, которые гид показывает перед
  переходом. Конвенция по конструкции.
- **tagAxes** — enforced в `SetTagsAsync`/`TagStore` по **рантайму доски** (доска → её мир →
  документ мира: правила инстанса, либо utility-слой для `$utility`): оси объявлены → namespace
  тега должен быть из списка, «голые» теги отклоняются.
- **Словарь связей** — `relations_create` принимает: структурные builtin-виды (`blocks`,
  `part_of`, `supersedes` — их движок потребляет сам), встроенные нейтральные (`relates_to`,
  `depends_on`, `mirrors` — свободные смысловые рёбра без эффектов) и `linkKinds`, объявленные
  документом мира FROM-узла. Процессная тройка квартета (`task_spec`, `issue_task`,
  `idea_spec`) живёт именно там — это ОБЪЯВЛЕННЫЕ виды с направлением, а не builtin-код.
  Неизвестный вид — отказ со списком допустимых для этого скоупа. Валидация словаря — в
  сервисе (`ITasksService.ValidateRelationKindAsync`), стор словарь не знает.
- **Скоуп мира (methodology-instance-scoped-axes):** `tagAxes` и объявленные `linkKinds` —
  авторитет своего мира, не project-global. Инстансы изолированы друг от друга; доска на
  `$utility` читает utility-слой; dual-read'а «доска без членства читает project-singleton»
  больше нет.

## Смена схемы: декларативная миграция

Изменение правил валидируется по живым узлам ДО записи: каждый активный узел затронутых
досок СВОЕГО мира (kind объявлен старым или новым документом) проверяется против нового
резолва. Для `tasks_methodology_rules_upsert` это открытые доски ЭТОГО инстанса, для
`tasks_methodology_utility_upsert` — доски на `$utility`. Шаблоны (`template_upsert`) инертны:
досок не создают, живых узлов не трогают, планировщика миграции у них нет.
Несовместимое (тип/статус, неизвестный новой схеме) должно быть покрыто **декларативным
маппингом** в том же вызове:

```
migration: [{ kind, types?: [{from,to}], statuses?: [{from,to}] }]
```

Маппинг применяется только там, где текущее значение невалидно (валидное никогда не
переписывается). Что осталось несмапленным — отказ всего вызова с именами доска/узел/значение,
**ничего не записано**. Смапленные узлы переписываются новыми temporal-ревизиями (маппинг и
есть санкционированный переход — FSM-гейты на перезаписи не гоняются), `migrated` в ack.
Атомарность честная: документ коммитится первым, конкурентная запись в доску во время
перезаписи даёт ошибку с именем доски и указанием, что НЕ переписано.

Сюда же попадает и первое объявление вида, перекрывающего пресетный kind с живыми досками, и
выпадение вида из документа (доски откатываются к пресетному резолву — несовместимость
ловится той же машинерией).

## MCP-поверхность

Адрес инстанса везде один и тот же — параметр `key` (slug), тот самый, который читающие
глаголы возвращают в поле `key`. Прозвище документа (`name` внутри правил) не адресует ничего.

| Глагол | Что делает |
|--------|------------|
| `tasks_methodology_create` | создать инстанс из ЯВНОГО источника (`source` = builtin\|template\|instance + `sourceKey`): правила + по доске на вид |
| `tasks_methodology_list` / `tasks_methodology_get` | индекс инстансов / один по `key` (доски, гистограмма статусов; тел узлов нет) |
| `tasks_methodology_rules_get` / `tasks_methodology_rules_upsert` | документ правил живого инстанса; запись — замена целиком по `version`, опц. `migration` |
| `tasks_methodology_close` | закрыть инстанс вместе с досками |
| `tasks_methodology_active_get` / `tasks_methodology_set_active` | указатель проекта на активный инстанс (только ДЕФОЛТЫ; членство доски сильнее) |
| `tasks_methodology_utility_get` / `tasks_methodology_utility_upsert` | utility-слой видов проекта (мир `$utility`) |
| `tasks_methodology_set_description` | заменить прозу ОДНОГО примитива по натуральному ключу, без CAS всего документа |
| `tasks_methodology_template_get\|list\|upsert\|delete\|snapshot` | именованные инертные шаблоны (+ встроенные quartet\|classic\|simple) |
| `tasks_methodology_guide` | агентский onboarding-гайд, выведенный из данных: markdown + структурные инварианты (`approval_gate\|approval_gate_enforced\|reason_required\|precondition_artifact\|checklist\|transition_effect\|link_constraint\|tag_axes`), `source` = instance\|active\|ambiguous\|presets |
| `tasks_workflow` | FSM конкретной доски (кастомной или пресетной), переходы с `preconditionArtifact` |
| `tasks_board_create` / `tasks_board_adopt` | создать доску в мире (`methodologyInstance`: ключ инстанса или `$utility`) / перенести существующую |

Правки живого процесса (`_create`, `_rules_upsert`, `_utility_upsert`, `_set_active`, `_close`,
`_adopt`, а также board `create`-соседи `delete`/`close`/`reopen`/`set_wire`) требуют СВЕРХ
`tasks:write` ещё и скоуп `methodology:write` — это governance-акты над правилами, которые уже
управляют живыми узлами. Шаблоны инертны и таким гейтом не закрыты.

Не путать: `tasks_methodology_get` — это ИНДЕКС одного инстанса (доски, счётчики статусов), а
его ПРАВИЛА читает `tasks_methodology_rules_get`.

`tasks_methodology_guide` — это «артефакты из данных» v1: скилл-текст процесса порождается
рантайм-выводом из правил (инвариант «агент никогда сам не ставит Done/accepted» — следствие
`requiresApproval` в данных, а не рукописный текст). Кодоген в файлы-артефакты — следующая
фаза.

## Код (карта файлов)

- `src/PetBox.Tasks.Engine/MethodologyDefinition.cs` — модель документа правил.
- `src/PetBox.Tasks.Engine/MethodologyPresets.cs` — quartet/classic/simple как данные + ParseKind.
- `src/PetBox.Tasks.Engine/MethodologyRuntime.cs` — шов резолва (документ-над-пресетами).
- `src/PetBox.Tasks.Engine/MethodologyGuide.cs` — рендерер гайда + инварианты.
- `src/PetBox.Tasks.Engine/MethodologyMigration.cs` — маппинги миграции.
- `src/PetBox.Tasks.Engine/Workflow.cs`, `WorkflowEngine.cs`, `GuardEngine.cs`,
  `DeliveryEngine.cs` — FSM-словарь, валидация переходов, гейты записи, roll-up доставки
  (чистая половина, без IO; вход — `MethodologyEngineContext`).
- `src/PetBox.Tasks/Validation/MethodologyDefinitionValidator.cs` — целостность документа.
- `src/PetBox.Tasks/Services/Methodology/` — `MethodologyInstanceService` (инстансы, членство
  досок, активный указатель), `MethodologyTemplateService`, `MethodologyDefinitionService`
  (utility-слой), `MethodologyLiveMigration`.
- `src/PetBox.Tasks/Data/` — `MethodologyInstanceRow` (M013), `ActiveMethodologyInstanceRow`
  (M017), `MethodologyTemplateRow` (M012), `MethodologyDefRow` (M010), `MethodologyInstanceBackfill`.
- `src/PetBox.Tasks/Services/TasksService.cs` — резолв рантайма (`RuntimeForBoardAsync` /
  `RuntimeAsync`), гейты, `GetMethodologyGuideAsync`.
- `src/PetBox.Web/Mcp/TasksTools.cs` + `Mcp/MethodologyWire.cs` — MCP-поверхность и wire-модель
  документа (её же использует админ-редактор методологии).
- Тесты: `tests/PetBox.Tests/Tasks/Methodology*Tests.cs` (definition/runtime/presets/migration/
  guide/instances/templates/active-instance/utility-kinds/gate-strictness) и
  `tests/PetBox.Tasks.Engine.Tests/` (чистый движок).

## Сознательно вне движка

- Серверный enforce approve-гейта в квартете: механика есть (`enforce.approval`, `strictMode`),
  но встроенные пресеты её не включают — включим, когда практика дозреет.
- Идентичность узлов `{board}-{n}` — отдельная идея (большая миграция ссылок).
- Кастомный вид ОБЪЯВИТЬ `effects`/`blocksGate`/`delivery` может (это данные, не привилегия
  квартета), но end-to-end на user-defined виде из них проверена не вся механика — на
  `onLeave`-эффекте покрыта только чистая логика выбора триггера.
