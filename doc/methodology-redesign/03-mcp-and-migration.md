# MCP-сигнатуры и план миграции БД (v3)

## Часть A. MCP-поверхность

Принцип: **типизированный контракт остаётся стабильным и БЕЗ квартет-имён**. Квартет-сахар
(`specRef`/`ideaRef`) убирается; сахар остаётся только у универсального субстрата
(`blockedBy`/`supersedes`/`partOf`), всё объявляемое — через `links`. Потерю дискаверабельности
компенсируют генерируемые из данных сообщения об ошибках и скомпилированный guide (см. A.2).

### A.1. Определение методологии — `tasks_methodology_*_upsert`

Вход `MethodologyDefInput` расширяется полями из `01-model` (всё опциональное → back-compat входа
не ломается на уровне JSON):

```
MethodologyDefInput {
  name, description?,                    // +description
  strictMode?: bool,                     // +
  kinds: KindInput[],
  linkKinds: LinkKindInput[],            // расширяется
  tagAxes: TagAxisInput[]
}

KindInput {
  kind, description?,                    // +description
  singleton?: bool,                      // + (дефолт false); единственная ось — role в ядро не тащим
                                          // (процесс/utility решается ДОМОМ объявления, не полем —
                                          // 01-model §2a)
  boardName?,                            // +
  blocksGate?: { status, releaseTo },    // + (НОВОЕ) builtin blocks-гейтинг — параметризует
                                          // RequireBlockersAsync/CloseBlocksOnLeaveAsync/delete-unblock
                                          // статусами вместо литералов "Blocked"/"InProgress"
  quickAddAllowed?, workflows[],
  linkConstraints[], effects[], autoWireFrom?, delivery?, defaultView?, outlineReveal?
}
// delivery: { requiredTypes, defectTypes, link } — link ОБЯЗАТЕЛЕН (не default!): роллап сегодня
// считает по литералу task_spec (`ComputeSpecDeliveryAsync`); дефолт-строка вернула бы тот же зашитый
// литерал под видом поля данных

LinkKindInput {
  slug, description?,
  category?: "process"|"neutral",        // +
  direction?: { fromKind?, toKind?, label? }    // + fromKind/toKind = ориентация ХРАНИМОГО ребра
}                                         // upsertAlias НЕТ — declared-виды только через links

TransitionInput {
  from, to, description?,                 // +description
  requiresApproval?, checklist?,
  requiredArtifacts?: [{ slug, inline? }], // ОБЪЕДИНЯЕТ requiresReason + preconditionArtifact
  enforce?: { approval?, artifacts? }      // + (approval заменяет EnforceApproval; artifacts — reason+precondition)
}

StatusInput      { slug, category, description? }             // +description
LinkConstraintInput { type, link, description?, targetKind?, targetStatuses?,
                      requiredOnEveryWrite?, atStatus?, onTransitionTo? }
                      // +description, +cadence(requiredOnEveryWrite), +wildcard type:"*"
                      // +atStatus (статус-keyed state invariant, requiredOnEveryWrite:true — замена
                      // RequireBlockersAsync) +onTransitionTo (узкий вариант atStatus: только момент
                      // входа) — см. 01-model §6
EffectInput      { on?, onLeave?, link, direction, set, onlyFrom?, description? }
                      // +description, +onLeave (ровно одно из on/onLeave — вторая точка входа,
                      // 01-model §7)
```

### A.2. Запись узла — `tasks_upsert` (общий механизм + сахар только для builtin)

Сегодня `PlanNodeInput` несёт зашитую тройку `SpecRef/IdeaRef/BlockedBy` (+`Supersedes`). Из них
`specRef`/`ideaRef` — **квартет-хардкод** (методология может не иметь ни `spec`, ни `ideas`), поэтому
**убираются из контракта**. Остаётся сахар только для универсальных builtin (есть в любой методологии):

```
PlanNodeInput {
  ...,
  // Сахар — ТОЛЬКО универсальные builtin-связи (всегда существуют):
  blockedBy?, supersedes?, partOf?,
  // Общий механизм — ЛЮБОЙ объявленный/builtin вид, ключ = слаг вида:
  links?: { <kind>: <ref> | <ref>[], ... }   // напр. { "task_spec": "spec-leaf",
                                               //         "implements_story": ["s1","s2"] }
}
```

Правила:
- Связь адресуется парой **(kind, target-ref)**: `links` — словарь по слагу вида; значение — один
  ref (slug|NodeId) или список (несколько целей одного вида).
- `task_spec`/`idea_spec`/`issue_task`/кастом — только через `links`. Их имена агент берёт из
  скомпилированного guide (там же их описания и направление).
- Слаги видов уникальны в рамках методологии (валидируется на дефиниции) → ключ `links` однозначен.
- Один вид одному узлу нельзя задать дважды (и сахаром `blockedBy`, и `links.blocks`) → ошибка
  «задай связь одним способом».
- **Резолв слага цели — ПО ПИШУЩЕМУ КОНЦУ, не по `toKind`** (раунд 2: старая формулировка «резолвится
  по доскам вида `direction.toKind`» — ОШИБОЧНА, исправлено). Правило: пишущий узел занимает ТОТ
  конец `direction`, чей `kind` совпадает с его собственным; цель — ПРОТИВОПОЛОЖНЫЙ конец; слаг цели
  резолвится по доскам вида противоположного конца, в бакете активного инстанса (совпадает с
  `ResolveIdeaRefsAsync`, ныне `GuardEngine.ResolveIdeaRefs` — spec-узел пишет `ideaRef`,
  `idea_spec.toKind=spec`: spec занимает `toKind`, цель резолвится по `fromKind=ideas`). Старая
  формулировка была бы верна, только если ПИШУЩИЙ узел всегда на `fromKind`-конце — ложно для
  `idea_spec` (пишет `spec` = `toKind`-конец) и для `issue_task` (пишет `work`,
  `issue_task.toKind=work` — та же картина: пишущий на `toKind`, цель на `fromKind=intake`). NodeId —
  сквозной, без резолва; ссылки внутри того же батча upsert разрешены (как сегодня `blockedBy`);
  неоднозначность → ошибка с кандидатами. Пин `SpecBoard` (сегодня для `specRef`) обобщается — см.
  часть B.2, п.7.

  Вырожденные случаи:
  (a) **`fromKind == toKind`** (self-kind связь: оба конца — один вид, напр. гипотетический
      `duplicate_of` между двумя `work`-узлами): пишущий узел совпадает с ОБОИМИ концами по kind →
      неоднозначно, какой конец — цель. Правило v1: self-kind `direction` **не поддержан** —
      валидатор дефиниции отклоняет `LinkKind` с `fromKind == toKind` (не-null), явной ошибкой
      «self-kind связь требует явного признака конца-цели — не в v1».
  (b) **Kind пишущего узла не совпадает НИ С ОДНИМ концом** (ни `fromKind`, ни `toKind`): констрейн
      называет вид связи, которым этот вид узла писать не может — ошибка ВАЛИДАЦИИ ДЕФИНИЦИИ (не
      рантайм-сюрприз на записи): «`link` вида X недоступен виду Y — `direction` не включает Y ни
      одним концом».
  (c) **`null`-конец** (`fromKind`/`toKind` не ограничен): если пишущий совпал с НЕ-null концом,
      цель — null-конец; null значит «вид цели не ограничен `direction`» → резолв берёт
      `LinkConstraint.targetKind` этого же констрейна (если задан), иначе — ошибка «объяви
      направление»: методология обязана либо сузить `direction`, либо дать `targetKind` на
      констрейне — резолв не гадает, доски какого вида искать.
- **`relations_create` обязан валидировать `direction`, не только `kind`** (раунд 2, именованный
  пункт — не подразумеваемый «заодно» внутри резолва выше). Сегодня `relations_create` проверяет,
  что `kind` объявлен, но НЕ что ребро `from→to` соответствует `direction.fromKind/toKind`
  объявленного вида — реверснутое ребро (напр. `spec→ideas` вместо `ideas→spec` для `idea_spec`)
  создаётся молча и тихо отключает эффекты/констрейны, которые матчатся по направлению
  (`Effect.direction`, `01-model` §7). Добавить проверку направления в `relations_create` для ЛЮБОГО
  вида с непустым `direction` — process и neutral-с-направлением одинаково.
- **Семантика записи:** `links` для вида — declarative-набор целей этого вида (замена набора, как
  `tags`/`commits`) — **кроме `blocks`, см. ниже**. Ключ-скип семантика: **опущенный ключ** в `links`
  = не трогать существующие связи этого вида; **пустой список** `links.<kind>: []` = явно снять все
  связи этого вида. Идемпотентность — по (kind, from, to).
- **`links.blocks` (и сахарный `blockedBy`) — ADD-ONLY, НЕ declarative-replace.** Движок ПОТРЕБЛЯЕТ
  `blocks`-рёбра при разблокировке (gating-consumption, `TaskTransitionEffects.RunTransitionEffectsAsync`, см.
  `01-model` §7) — декларативная замена набора воскресила бы уже потреблённые рёбра и задвоила бы
  срабатывание разблокировки. `blockedBy`/`links.blocks` в вызове ТОЛЬКО добавляет блокеры; снятие —
  через явный transition-эффект/delete-путь движка, не через пустой список.
- **Ошибки — из данных, а не хардкод-текст:** неизвестный ключ `links` → ошибка со списком
  объявленных видов; нарушенный констрейн → сообщение из `LinkConstraint`+`LinkKind`
  («provide links.task_spec — points at a `spec` node»). Это обязательная компенсация потери сахара,
  не «по возможности».

Итог для агента: типовой путь — `links:{...}` для процессных связей + `blockedBy` как раньше.
Ни одного квартет-именованного поля в контракте не остаётся.

### A.3. Guide / компиляция в скилл

- `tasks_methodology_guide` — рендер уже есть; начинает включать описания примитивов (данные их
  теперь несут). Формат вывода (markdown + `invariants[]`) не ломается — обогащается.
- **Компиляция — явной командой `petbox-wire`, без авто-вшивания в стартовый хук.** Авто-инъекцию
  процесса в стартовый контекст (и LKG/устаревание/бюджет баннера) **не делаем**. Пользователь/агент
  компилирует guide активной методологии в скилл, когда нужно.
- **Скилл самого `petbox-wire`** — чтобы владелец не учил команды: говорит агенту «настрой/обнови
  процесс», агент выполняет через скилл. (Отдельная маленькая работа по DX кита, не по движку.)
- **Сигнал устаревания** (единственная реальная дыра pull-модели: методологию меняют чаще скилла).
  `petbox-wire` вшивает в скилл версию/хеш дефиниции; рутинный ответ (`tasks_methodology_get`/
  `board_list`) несёт текущую версию; скилл инструктирует «версия не совпала → перекомпилируй».

### A.4. Что из MCP-описаний и read-пути чистится (sprawl)

Тексты тулов (`TasksTools`, `McpToolInputs`) сегодня хардкодят `quartet|specRef|ideaRef|spec_plan`
как ЕДИНСТВЕННЫЕ примеры. После редизайна — описания генерируются/нейтральны: `specRef`/`ideaRef`
исчезают из контракта; `links` документируется обобщённо; примеры перестают притворяться, что процесс
всегда квартет.

**Read-путь — тот же список чистки** (раунд 2: иначе write станет data-driven, а read молча
останется квартетным):
- `PlanNodeView.Spec`/`.LinkedTasks` строятся по литералу `task_spec` (`TasksService.GetBoardAsync`) —
  перевести на построение, driven `LinkKind.direction.label` (любой process-вид со своим label, не
  только `task_spec`).
- Панель связей detail-страницы — хардкод-таблица из 6 видов (`RelationPanelSpecs`,
  `TasksService.RelationPanelSpecs`) — та же замена: driven `LinkKind.direction.label`, включая ЛЮБОЙ
  объявленный вид, не только builtin-шестёрку.
- `tasks_board_set_spec` и параметр `specBoard` на `board_create`/`board_list` — квартет-именованная
  поверхность (пин specRef-резолва под другим именем). Обобщается вместе со `SpecBoard`-колонкой
  (часть B.1, схема; B.2 п.7, миграция) в per-link-kind пин без квартет-имени в самом названии
  тула/параметра.

### A.5. Редактирование, хранение, версии, доступ

- **Раунд 2: гранулярный патч ОТВЕРГНУТ. Принят split — whole-document STRUCTURE + отдельный
  прозовый глагол.** Разведка размера: живой rules-документ ($system-квартет) — **~7 КБ**, влезает
  в вызов свободно; давление на размер даёт ТОЛЬКО проза-на-примитивах (кириллица), не структура
  сама по себе. Значит адресация примитива внутри вложенного документа (сложность и атомарность
  гранулярного патча, которые владелец отметил как риск) — цена, за которую нечем платить. Решение:
  - **`tasks_methodology_rules_upsert`** — whole-document upsert СТРУКТУРЫ (kinds/workflows/
    linkConstraints/effects/…) с уже существующим version-CAS (`tasks_methodology_rules_get`
    отдаёт baseline; upsert несёт его версию — гонка конфликтует явно, не тихо перезаписывается).
  - **`methodology_describe(path, text)`** — НОВЫЙ отдельный глагол для прозы (`description` на
    примитивах), адресующий примитив ЕСТЕСТВЕННЫМ ключом его типа, не индексом/путём-в-дереве:
    `kind` (вид); `kind+status` (статус вида); `kind+from+to` (переход); `linkKind` (вид связи);
    `axis` (тег-ось). Описания эффектов/констрейнов адресовать так же естественно НЕЛЬЗЯ (внутри
    вида их может быть много однотипных) — в v1 либо не поддержаны, либо by-index+CAS; не решено,
    см. «Раунд 3» в открытых вопросах ниже.
  - Итог: **забота владельца закрыта СПЛИТОМ**, не примитив-адресующей машинерией внутри одного
    патч-глагола — структура и проза правятся разными вызовами, каждый простой механикой.
- **Хранение — JSON (пока), не реляционные таблицы.** Клинчер — версионирование: темпоральную историю
  одной JSON-строки вести тривиально, а граф таблиц (kinds/statuses/transitions/link_kinds с FK) —
  боль. Методология документо-образна, объём (см. выше — ~7 КБ) крошечный, целостность держит
  `MethodologyDefinitionValidator`. Реляционную раскладку пересматриваем, только если понадобятся
  запросы поперёк методологий.
- **Версии — уже есть.** `methodology_defs/instances` темпоральны (SCD-2). Whole-document upsert
  рождает новую версию на сервере под тем же CAS; `methodology_describe` — отдельный маленький
  апдейт той же версионируемой сущности, не отдельное версионирование примитивов.
- **Scope `methodology:write` — граница по «меняет действующие правила для СУЩЕСТВУЮЩИХ узлов», не
  по «структура vs проза»** (раунд 2: сегодня граница дырявая). `methodology:write` сейчас
  декоративен: `tasks_methodology_create` (новый инстанс с произвольными правилами) и
  `tasks_board_adopt` (доска входит в инстанс — начинает жить по его гейтам) выполняются под
  `tasks:write`, хотя ОБА меняют правила игры не хуже правки существующей дефиниции. Фикс: scope
  требуется на upsert правил, на `tasks_methodology_create` (когда несёт нетривиальные правила, не
  просто «применить seed как есть») И на `tasks_board_adopt` — все три меняют, по каким правилам
  живут узлы; рядовой `tasks:write`-ключ делает CRUD узла, не переписывает его constitution.
  `methodology_describe` (проза) может остаться под `tasks:write` — прозы не меняют enforcement.
  Стыкуется со спеком `auth-api-key-surface`.

## Часть B. План миграции БД (чистая, без шимов)

Ключевой факт, снижающий риск: **данные уже в основном совместимы.** `TaskBoards.Kind` и
`relations.Kind` — строки (не enum); доски `ideas/spec/work/intake/wiki` и связи `task_spec/idea_spec/
issue_task` уже лежат как строки. Значит миграция — это в основном **перенос семантики из кода в
данные**, а не переливание строк. Данные узлов/связей/досок переживают как есть.

### B.1. Что меняется в СХЕМЕ

Схема методологии хранит `MethodologyDefinition` как JSON (`methodology_defs`/`_templates`/
`_instances`). Новые поля (`description`, `singleton`, `boardName`, `strictMode`, `direction`,
`enforce`, `requiredOnEveryWrite`, `atStatus`, `onTransitionTo`, `blocksGate`, `Effect.onLeave`) —
это **эволюция JSON-формы, а не колонок** (никакого `role` в списке — решено не заводить его вообще,
см. `01-model` §2a). Отсутствуют в старых строках → читаются как дефолты — **кроме `delivery.link`**
(см. B.2 шаг 3: обязан быть проставлен явно на каждой строке, несущей `delivery`, дефолта у него
нет). Поэтому колоночных миграций почти нет.

Исключение — квартет-именованная колонка каталога досок **`TaskBoardMeta.SpecBoard`** (пин авто-вайра
и резолва `specRef`; `TaskBoardStore.CreateAsync`). Это НЕ JSON-эволюция, а колонка со квартет-семантикой.
Оставить её = ровно тот legacy-шов, который редизайн обещал убрать. Решение (п.B.2-8): обобщить в
per-link-kind пин (напр. `autoWireFrom` уже несёт вид-источник → пин выводится из него), либо
`wiredBoard`-указатель на доске без квартет-имени. Это самое крупное, что часть B раньше упускала.

Опциональная консолидация (предложение из разведки): три temporal-таблицы
`methodology_defs` (singleton) / `_templates` / `_instances` имеют идентичную форму
(`Key, Version, Json, ClosedAt?, PrevKey?, ActiveFrom, ActiveTo, Created, Updated`). Можно свести в
одну `methodology_docs` с дискриминатором `Kind ∈ {def, template, instance}`. Это **упрощение**, не
требование редизайна — вынести в отдельное решение (риск/выгода в критике Fable).

### B.2. Шаги (forward-only, без back-compat кода)

1. **Seed builtin как данные.** Квартет/classic/simple/wiki переписать из C#-пресетов
   (`MethodologyPresets.cs`) в стартовые дефиниции (`02-expressed`). Код-пресеты удаляются
   (не остаются шимом).
2. **Снять enum.** `BoardKind` → удалить; `Kind`-строки уже в БД. `ProcessRoleKinds`/`Methodological`
   → удалить; `singleton` читает `Kind.singleton` из активной дефиниции (НЕ `Kind.role` — такого
   поля нет, см. `01-model` §2). `boardName` из данных. Процесс/utility различаются ДОМОМ
   объявления (методология vs проектный utility-набор, §2a), не полем на `Kind` — см. шаг 12.
3. **Три связи — в данные.** `ProcessRelationKinds` ужать до универсального субстрата
   (`part_of/blocks/supersedes` + neutral). `idea_spec/task_spec/issue_task` объявить в seed-квартете
   как `LinkKind` с `direction` (ориентация хранимого ребра). `LinkField(...)`-маппинг и `specRef`/
   `ideaRef`-резолв → общий `links`-резолв (A.2, резолв по пишущему концу). Существующие строки-связи
   валидны против объявленных видов — переливать нечего. `delivery.link` проставить ЯВНО на каждом
   kind, несущем `delivery` — поле обязательно, дефолта `"task_spec"` нет (иначе миграция тихо
   воспроизводит тот же зашитый литерал под видом поля, ничего не решив; `01-model` §7).
4. **`blocks`-семантика в данные.** `RequireBlockersAsync` → **STATE invariant**
   `LinkConstraint{type:"*", atStatus: blocksGate.status, link: blocks, requiredOnEveryWrite:true}`,
   НЕ транзишн-гейт `onTransitionTo` (проверяется на КАЖДУЮ запись со статусом Blocked, включая
   рождение — не только момент входа; `01-model` §6/§7). `Kind.blocksGate{status,releaseTo}` —
   новый builtin-примитив на `work`, параметризующий и этот констрейн, и оба спутника ниже (заодно
   снимает завязку на имя вида `IsWorkKind`). **Все ТРИ литерала `"Blocked"`/`"InProgress"`**
   (`GuardEngine.RequireBlockers`; `TasksService.CloseBlocksOnLeaveAsync`; delete-разблокировка
   `TaskTransitionEffects.RunDeleteEffectsAsync`) читают
   `blocksGate.status`/`.releaseTo` вместо литералов; gating-consumption ребра `blocks`
   (`TaskTransitionEffects.RunTransitionEffectsAsync`) остаётся builtin — данными становятся только
   статусы-параметры.
5. **Строгость.** `EnforceApproval` → часть `GateEnforcement`; `strictMode` на дефиницию. Квартет
   мигрирует как `strictMode:false`, но **`reason=true` и `preconditionArtifact=true`** (оба жёсткие
   сегодня — `WorkflowEngine.Validate`) → поведение неизменно. (Прежний черновик ошибочно делал reason
   мягким.)
6. **Один активный инстанс на проект.** Сегодня код допускает N открытых инстансов с merge-резолвом
   (`TasksService.RuntimeAsync`) и legacy null-membership доски (`TasksService.CreateBoardAsync`).
   Под «одну активную
   методологию»: схлопнуть $system к одному открытому инстансу и **backfill membership** legacy-досок.
   Без этого «одна активная» — декларация, а не миграция. (Стыкуется с lifecycle-слоем, но точку
   «одна активная» фиксируем здесь.)
7. **`SpecBoard`-пин.** Обобщить квартет-колонку (см. B.1) — вывести пин из `autoWireFrom` либо
   `wiredBoard` без квартет-имени. Без шима.
8. **Аудит legacy relation-видов.** Если в БД есть рёбра видов вне нового builtin+declared набора
   (историч. `nfr`/`dup` и т.п.) — одноразовое решение: объявить, переименовать или оставить мёртвыми.
   Иначе `relations_create`-валидация и guide их не знают.
9. **Синхронный апдейт скиллов/доков.** Убирание `specRef`/`ideaRef` из контракта ломает hand-written
   скиллы (`petbox-methodology` кодирует «every write needs ideaRef»), `doc/methodology.md`, память
   агентов. «No shim» тут возможен без потери данных, но деплой обязан включать синхронную правку —
   иначе первый агент после релиза пишет в отвергнутый контракт.
10. **Backfill Json — ВСЕ инстансы × шаблоны × дефиниции, не только $system** (раунд 2 — прежняя
    формулировка называла только $system-квартет, этого недостаточно). Каждая строка
    `methodology_defs`/`_templates`/`_instances` (любого проекта) обязана получить `singleton`
    ЯВНО на каждом её `Kind` при переписывании в новую JSON-форму. `singleton:false`-дефолт
    ОПАСЕН: сохранённый квартет БЕЗ явного поля тихо потерял бы singleton-инвариант — тот же класс
    инцидента, что уже задокументированный `DefaultView`-производственный случай (merge читает
    отсутствующее поле как «нет мнения», а не «унаследуй пресет», `MethodologyRuntime.DefaultView`).
    Backfill проходит ПО ВСЕМ строкам ВСЕХ трёх temporal-таблиц во всех проектах, не по одному
    known-инстансу; членства досок не трогаются (кроме re-home п.12 и backfill п.6). Одноразовый
    data-fix, но с полным охватом.
11. **Описания.** Заполнить прозу на примитивах seed-дефиниций (`02-expressed`).
12. **Utility-слой: re-home сквозных досок $system** (владелец, раунд 2 — новый шаг). Utility-слой
    (`01-model` §2a) — project-owned виды, живущие вне активного инстанса. На `$system` сегодня
    доски `classic`, `client-issues`, `roadmap`, `wiki` либо не члены quartet-инстанса (legacy
    null-membership, `TasksService.CreateBoardAsync`), либо их членство спорно. Ре-хоумим их ЯВНО в
    проектный utility-набор (не в quartet-инстанс); `ideas`/`spec`/`work`/`intake` остаются в
    quartet-методологии — они и есть процесс. Закрывает флаг-день из шага 1: без явного re-home
    удаление code-пресетов осиротило бы эти доски (мигрировать членство некуда — «process» уже не
    про них). Одноразовая data-fix-миграция, тот же дух, что шаг 10.

### B.3. Риск-контур миграции

- **Низкий риск по данным:** строки досок/связей/узлов не меняются.
- **Риск по коду-паритету:** singleton, gate-enforcement, link-резолв, blocker-гейт меняют
  источник истины (код→данные). Здесь нужен тест-паритет: старое поведение квартета обязано
  воспроизвестись из seed-данных бит-в-бит. Это главный предмет проверки (и в критике Fable).
- **Правило владельца:** «disabled ≠ непротестировано» — общий data-driven путь прогоняется тестами,
  даже если UI показывает только квартет.
- **Обязательный охват `MethodologyDefinitionValidator`** (раунд 2 — список, не «как получится»):
  1. Уникальность слагов `Kind.kind`/`LinkKind.slug` в рамках методологии + отсутствие коллизии с
     builtin-видами (уже заявлено, `01-model` §5 — фиксируем как пункт списка, не отдельно).
  2. Никаких висящих ссылок на статусы в межвидовых `Effect.set`/`Effect.onlyFrom`/
     `LinkConstraint.targetStatuses` — статус обязан существовать в workflow ЦЕЛЕВОГО вида
     (`targetKind`/цель `Effect`), не только у объявляющего.
  3. `singleton:true` ОБЯЗАТЕЛЕН на любом виде, на который ссылается `autoWireFrom` или цепочка
     `delivery.link` — авто-вайр и SpecBoard-подобный пин корректны, только когда у источника РОВНО
     одна открытая доска; без `singleton:true` резолв неоднозначен по построению.
  4. Коллизия слагов статусов МЕЖДУ видами в рамках проекта — `KindOfSlug` сканирует project-wide
     (`MethodologyRuntime.KindOfSlug`: сначала пресет, потом все объявленные виды); два вида с
     разными `StatusCategory` на одинаковом слаге статуса дают неоднозначную классификацию там, где
     вызывающий код не знает kind (`IsTerminalSlug`).
- **Definition-change-под-живыми-досками — НЕ покрыто, требует решения** (раунд 2, найдено
  адверсариальным разбором). Что происходит, когда дефиницию правят, а доски уже открыты и несут
  данные: ставят `singleton:true` на вид, у которого УЖЕ две открытые доски; удаляют вид, у которого
  есть доски; удаляют вид связи, у которого есть живые рёбра. Валидатор/миграция обязаны определить
  поведение (отказ правки vs миграция-принуждение vs мягкое предупреждение) — сегодня не
  специфицировано; фиксируем как открытый вопрос раунда 3 (см. ниже), не как решённое.

## Открытые вопросы

**Раунд 1 (Fable) — закрыты и учтены в доках:**
- ~~Роли: расщепить `role`(lifecycle) + `singleton`(инвариант) — принято (кейс classic).~~
  **Superseded владельцем в раунде 2:** роли не будет вообще — «процесс vs сквозное» = ДОМ
  объявления вида (методология-инстанс vs проектный utility-слой, `01-model` §2a), не поле `role`
  на `Kind`. `singleton` остаётся единственной осью НА `Kind`, как и было решено здесь — просто без
  `role` рядом.
- Консолидация трёх methodology-таблиц — отдельно, после (изоляция риска).
- Строгость: `strictMode` + per-gate, дефолты пересчитаны по коду (reason/precondition жёсткие).
- `direction` = ориентация хранимого ребра, `null`=не ограничен — достаточно.
- Всё через `links` — приемлемо ПРИ обязательных data-генерируемых ошибках (учтено в A.2).

**Раунд 2 (адверсариальный) — закрыт, kill-list отработан:**
1. Utility-слой владельца — принят, folded (`00-overview`, `01-model` §2a, `02-expressed` §2-§4,
   `03` B.2 п.12).
2. `blocks`-механика получила данные (`blocksGate`, `Effect.onLeave`, `atStatus`-state-invariant) —
   `01-model` §4/§6/§7, `02-expressed` work, `03` B.2 п.4.
3. `links`-резолв в A.2 переписан (был неверен — резолвил по `toKind` вместо конца, который
   занимает пишущий узел); добавлена валидация направления в `relations_create`; определены
   вырожденные случаи (self-kind, kind вне обоих концов, null-конец).
4. `requiredArtifacts` — три правила закреплены (inline не гейтит рождение; inline-канал только
   `reason`; гетерогенные гейты на входах в статус запрещены валидатором) — `01-model` §4.
5. Read-путь (`PlanNodeView`/relation-панель/`specBoard`-поверхность) добавлен в cleanup-список
   (`03` A.4); governance-дыра (`tasks_methodology_create`/`tasks_board_adopt` мимо
   `methodology:write`) закрыта (`03` A.5).
6. Backfill расширен на ВСЕ инстансы×шаблоны×дефиниции (`03` B.2 п.10); validator scope перечислен
   списком (`03` B.3); definition-change-под-живыми-досками зафиксирован как открытый (раунд 3
   ниже), не потерян.
7. Гранулярный патч отвергнут → split (whole-document upsert структуры + `methodology_describe`
   для прозы) — `03` A.5. Док-нити (`onTransitionTo`/wildcard `type:"*"`/links key-skip/`blocks`
   add-only/`checklist`-конвенция) закрыты в `01-model` §4/§6 и `03` A.2.

**Раунд 3 (следующий адверсариальный проход) — явно искать:**
1. Definition-change-под-живыми-досками (`03` B.3) — поведение не специфицировано, только
   зафиксировано как дыра.
2. Self-kind `direction` (пишущий kind == оба конца) объявлен v1-unsupported (`03` A.2, вырожденный
   случай a) — достаточно ли этого, или найдётся реальный сценарий, которому это нужно.
3. Кастомный `requiredArtifacts[{inline:true}]` для слага ≠ `reason` объявлен v1-unsupported
   (`01-model` §4) — тот же вопрос: нужен ли реальной методологии второй inline-канал.
4. Описания эффектов/констрейнов через `methodology_describe` — адресация by-index+CAS не решена
   (`03` A.5) — решить или явно вычеркнуть из v1.
