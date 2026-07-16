# Этап 0: извлечение движка методологии в чистую сборку

> Предусловие ко всему редизайну. Раньше реализации: вытащить движок (data-in → решение-out) в
> отдельную DB-free сборку под pure-тесты; тогда правки редизайна на нём — дешёвые и безопасные.
> Пометки: **[проверено]** — сверено по коду в этой сессии; **[оценка]** — прикидка первичной
> разведки, уточняется при реализации.

## Диагноз (обе гипотезы владельца подтверждены)

**Раздутие [проверено].** `TasksService.cs` = **2348 строк**, ~73 метода, 8 инжектируемых
коллабораторов. Один класс держит: выборку из БД, энфорсмент FSM, резолв связей, исполнение
эффектов, сборку view/DTO, поиск, delivery-роллап, комменты. Разбивка по строкам [оценка]:
~30% БД-доступ, ~17% сборка view, ~15% энфорсмент FSM, ~15% поиск, остальное — резолв связей,
эффекты, delivery, runtime-резолюция.

**Гипотеза A — размазанный FSM [проверено].** Ядро валидации перехода централизовано и **чисто**:
`WorkflowEngine.Validate` (77 строк, ноль стор-зависимостей) — единственная точка проверки
статус-переходов, аппрув-гейтов, reason, рождения-в-гейченный-статус. НО **энфорсмент-гайарды
переизобретают решения ВНЕ движка**, вперемешку с выборками:
- `RequireBlockersAsync` (`TasksService.cs:2073`) — инвариант «Blocked обязан назвать блокер»,
  читает relations сам.
- `RequirePreconditionArtifactsAsync` (`:1723-1763`) — гейт артефакта, лезет в comment-service
  по одному узлу (`:1759`).
- `RequireDefinitionLinks` (`:2108-2147`) — констрейны создания связей, читает и матчит инлайн.
- `CloseBlocksOnLeaveAsync` (`:2087-2096`) — эффект на ВЫХОД из Blocked, захардкожен на литерал.
То есть решение-логика есть где ей быть (WorkflowEngine), но вокруг неё в сервисе — слой guard'ов,
дублирующих FSM-суждения и сросшихся с БД.

**Гипотеза B — дублирующие выборки [проверено].** В `TasksService.cs` — **25** мест выборки
board-данных (`_boards.ListAsync/FindAsync` + `BuildNodeIndexAsync`). На один `UpsertAsync` [оценка]:
`_boards.ListAsync()` зовётся 5+ раз (в `BuildNodeIndexAsync`, `ResolveIdeaRefsAsync`,
`ComputeSpecDeliveryAsync`, `AutoWireSpecAsync`, поиске), `BuildNodeIndexAsync` — 2-3 раза, relations
и comments — по узлу, без батчинга. **Корень:** каждый guard реализован отдельным методом и тянет
данные независимо; нет предвычисленного пакета, который прокидывается в решатели.

## Хорошая новость: ~1400 строк движка УЖЕ чистые [проверено]

В `src/PetBox.Tasks/Workflow/` уже лежит DB-free ядро: `WorkflowEngine` (77), `MethodologyRuntime`
(336) — оба с **нулём** стор/DataConnection-зависимостей (сверено grep'ом), плюс
`MethodologyDefinition`, `MethodologyPresets`, `MethodologyGuide`, `Workflow.cs` — иммутабельные
данные + чистая резолюция. Единственное DB-bound в папке — `TaskTransitionEffects.cs` (109 строк,
инжектит `_boards/_relations/_tags`), и оно уже корректно вынесено как **post-write** эффект, не
решатель. То есть семя engine-сборки существует; извлекать — гайарды из сервиса, не переписывать
движок с нуля.

## Извлечение — шов

**Новая сборка `PetBox.Tasks.Engine` (чистая, ноль linq2db):**
- переносим уже-чистые: `WorkflowEngine`, `MethodologyRuntime`, `MethodologyDefinition`,
  `MethodologyPresets`, `MethodologyGuide`, `Workflow.cs`;
- **новый `GuardEngine`** (~200 строк) — чистые версии `RequireDefinitionLinks`,
  `ValidateLinkTargets`, `RequireBlockers`, `RequirePreconditionArtifacts`. Каждый guard режется на
  (а) выборку — остаётся в сервисе, (б) чистое суждение — уезжает в движок.

**`TasksService` становится IO-границей:** предвыбрать всё нужное ОДИН раз → отдать движку →
получить решение → записать + применить эффекты. Остаётся: board-CRUD, чтение/поиск, стейджинг
выборок, применение эффектов (`TaskTransitionEffects` — остаётся DB-bound), **минимальная**
валидация ввода. Основу валидации даёт движок.

**Вход/выход движка (форма — уточняется реализацией):**
```
MethodologyEngineContext(          // всё предвыбрано сервисом, БД внутри движка НЕТ
    runtime, kindSlug,
    desiredStates, priorStates,             // проекции NodeState, не linq2db-PlanNode (усл.4)
    // СЫРЫЕ кандидаты резолюции (движок резолвит сам, усл.3), не готовые NodeId:
    instanceIdeaBoards, specNodes, boardPriorDesired,
    nodeIndex,                              // активные узлы проекта — один раз
    blockerEdgesByNodeId,                   // активные blocks-рёбра в узлы (усл.2)
    partOfChildrenByNodeId,                 // active part_of дети (delete-guard, усл.2)
    commentTagsByNodeId )                   // теги коммов для precondition — предвыбраны
→ MethodologyEngineDecision(               // ТОЛЬКО пре-write вердикты (усл.1)
    workflowValidation, resolvedRefs,
    linkViolations, blockerViolations, artifactViolations )
    // effectsToApply НЕТ: эффекты остаются императивными post-write с live-чтениями,
    // по `landed`-подмножеству (усл.1)
```

**Два зайца:** предвыбор-в-контекст-однажды заодно **убивает гипотезу B** — дублирующие
`BuildNodeIndex`/`boards.ListAsync` схлопываются в один проход. Тестируемость + перф одним движением.

**Самые сросшиеся места (и как инвертировать):**
1. **Precondition читает комменты в середине решения** (`:1759`). → движок ДЕКЛАРИРУЕТ «нужны теги
   коммов для узла X», IO-слой предвыбирает все комменты доски один раз и отдаёт `commentTagsByNodeId`.
2. **Резолв `ideaRef` лезет в чужие доски** (`ResolveIdeaRefsAsync:1885-1932`). → сервис предвыбирает
   idea-доски инстанса, отдаёт map; движок не ходит в БД.
3. **`BuildNodeIndexAsync` зовётся дважды в пути.** → вычислить раз в `UpsertAsync`, прокинуть во все
   решатели.

## Выигрыш по тестам

Сегодня FSM-логику тестируешь через моки сторов + сборку данных в памяти (integration). После
извлечения — **чистые unit'ы без БД, детерминированные функции**:
- статус-переходы + аппрув/reason/precondition-гейты (`WorkflowEngine.Validate`);
- констрейны связей, инвариант блокера, precondition-артефакты (`GuardEngine`);
- delivery-роллап, singleton, эффекты (supersedes-каскад, blocks-разблокировка).
Паритетная сетка уже есть [проверено]: `tests\PetBox.Tests\Tasks\` — 386 `[Fact]/[Theory]`, прямо по
upsert-пути. После извлечения к ним добавляются быстрые pure-unit'ы на движок (детерминированные,
без стора); стандарт приёмки — усл.5 ниже.

## Почему ЭТАП 0, а не попутно

Редизайн (утилиты `01-03`) тяжело правит именно эти гайарды и резолверы. Делать его на god-class'е —
дорого и рискованно (прод-инцидент с DefaultView уже показал, чем оборачивается размытая логика).
Порядок: **(0) извлечь движок под чистые тесты → (1) редизайн на нём дешёвыми правками → (2)
помеченные открытые вопросы `03`**. Извлечение — behavior-preserving рефактор, приземляется с
бит-в-бит паритет-тестами; та же pure-сюита потом стережёт редизайн.

## Условия старта (Fable: GO WITH CONDITIONS, сверено с кодом)

Вердикт положительный; пять условий — контрактные уточнения, ни одно не рушит замысел. С усл.1-3
контракт движка (`runtime + состояния + сырые предвыборки → вердикты`) стабилен относительно ВСЕХ
новых примитивов редизайна (`blocksGate`, `Effect.onLeave`, `requiredArtifacts`, declared link-kinds,
resolve-by-writing-end) — пере-извлечения на Этапе 1 не потребуется.

1. **`Decision` несёт только пре-write вердикты; эффекты — НЕ решать заранее.** `CloseBlocksOnLeave`
   (:1271) читает live-relations ПОСЛЕ записи blocks-ребра (:1269) в том же вызове; `RunTransitionEffects`
   мутирует и перечитывает `stillBlocked` (:49, release последнего блокера); пост-write работает по
   `landed` (:1254-1259, определяется конфликтами `TemporalStore`, непознаваемыми пре-write). Эффекты
   остаются императивными post-write с live-чтениями. `effectsToApply` убран.
2. **В контекст добавить рёберные данные.** `RequireBlockersAsync` уже сегодня нужны активные
   blocks-рёбра в узел (:2080), delete-guard — active part_of дети (:1185, :2009); `blocksGate` из
   редизайна — то же. Без `blockerEdgesByNodeId`/`partOfChildrenByNodeId` не воспроизвести даже текущее.
3. **Резолюцию specRef/ideaRef/blockedBy втянуть В движок.** Её ошибки (`ResolveIdeaRefsAsync`
   :1912/:1927, `ResolveSpecRefs` :1867, `ResolveBlockedBy` :1954) — полноправные вердикты с `ForNode`
   в partial-каскаде retry-цикла. Оставить IO-side = кусок решателя без чистых тестов, и Этап 1
   (`resolve-by-writing-end`) всё равно сдвинул бы границу. Контекст несёт СЫРЫХ кандидатов, движок
   резолвит чисто.
4. **«Ноль linq2db» невозможен с текущим `PlanNode`** (`Data\PlanNode.cs:1` — `using LinqToDB.Mapping`).
   Решение до старта: **лёгкая проекция `NodeState`-record в Engine + маппинг в сервисе** (рекомендую —
   ниже риск), либо перевод `PlanNode` на fluent-mapping и вынос в чистую сборку. Кусок работы, учесть
   в объёме.
5. **Стандарт паритета — не «бит-в-бит».** NodeId — свежий Guid на каждый проход retry-цикла (:1800),
   буквальное равенство недостижимо и не нужно. Реальный стандарт: **вся существующая сюита зелёная**
   (`tests\PetBox.Tests\Tasks\` — 386 `[Fact]/[Theory]`/43 файла, прямо по upsert-пути: `QuartetTests`,
   `TasksMethodologyWorkFsmTests`, `BatchPartialApplyTests`, `TasksMethodologyRefsTests`,
   `WorkflowEngineTests`…) **+ равенство error-строк** в новых unit'ах; retry-цикл partial-режима
   сохранить структурно (движок переигрывается по стабильному контексту), не заменять одно-проходным
   сбором всех виолаций.

## Оговорка честности

Проверено фактом: 2348 строк, чистота `WorkflowEngine`/`MethodologyRuntime` (0 DB-зависимостей),
25 мест board-выборки, 4 guard'а вне движка, `TaskTransitionEffects` DB-bound. Проценты разбивки,
точный по-методный раскрой и «~150 тестов» — оценка первичной разведки (её делала Haiku), уточняется
при реализации. Форма `MethodologyEngineContext/Decision` — эскиз шва, не финальный контракт.
