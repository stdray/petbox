# Новая структура методологии (модель данных) (v3)

> Отталкиваемся от нынешней `MethodologyDefinition` (она уже несёт 90% нужного). Помечаю:
> `[есть]` — поле существует сегодня; `[НОВОЕ]` — добавляем; `[в данные]` — было хардкодом, переносим.
> Синтаксис — псевдо-record'ы для читаемости; сериализация — JSON (camelCase), enum'ы строками.

## Обзор: что меняется по сравнению с сегодня

1. `Kind` перестаёт зависеть от enum: `kind`-слаг, `singleton`, **имя доски** — данные
   `[в данные]`. Вместо поля-роли — **дом объявления вида** (методология-инстанс vs проектный
   utility-слой, §2a) `[НОВОЕ]`.
2. Появляется **параметризуемый вид связи** — три квартет-связи (`idea_spec/task_spec/issue_task`)
   становятся объявляемыми данными, а не builtin `[в данные]`.
3. **Описание** добавляется на каждый примитив `[НОВОЕ]`.
4. Гейты получают **единообразные флаги строгости** `[НОВОЕ]` (сегодня только `EnforceApproval`);
   `blocks`-гейтинг получает данные-параметры (`blocksGate`), а `Effect` — вторую точку входа
   (`onLeave`) `[НОВОЕ]`.
5. Скомпилированный процесс (guide) начинает нести описания; компилируется в скилл **явной командой
   `petbox-wire`** (не авто-вшивание в стартовый хук) `[НОВОЕ]`.
6. **Utility-слой проекта** — виды project-homed, переживающие switch методологии, тем же
   `Kind`-примитивом, без отдельного `role` `[НОВОЕ]` (§2a).

---

## 1. Корень

```
MethodologyDefinition(
    name: string,                       // [есть] слаг методологии
    description: string?,               // [НОВОЕ] «что это за процесс, для кого»
    kinds: Kind[],                      // [есть]
    linkKinds: LinkKind[],              // [есть, расширяется] объявляемые виды связей
    tagAxes: TagAxis[],                 // [есть]
    strictMode: bool = false            // [НОВОЕ] глобальный дефолт серверной строгости гейтов
)
```

`strictMode` — дефолт для всех гейтов дефиниции; каждый гейт может переопределить точечно (см. §4).

## 2. Вид (Kind)

```
Kind(
    kind: string,                       // [есть] free-form слаг («ideas», «work», «wiki», «qa»…)
    description: string?,               // [НОВОЕ] «доска для…»; едет в скилл
    singleton: bool = false,            // [в данные] «≤1 открытой доски ЭТОГО вида на инстанс»; per-kind
    boardName: string?,                 // [в данные] имя/display доски; null → = kind (шаблон именует доски)
    quickAddAllowed: bool = true,       // [есть]
    workflows: Workflow[],              // [есть]
    linkConstraints: LinkConstraint[],  // [есть, расширяется]
    effects: Effect[],                  // [есть]
    autoWireFrom: string?,              // [есть] (сегодня AutoWireSpecFrom) — слаг вида-источника
    delivery: Delivery?,                // [есть]
    defaultView: string?,               // [есть] Kanban|Outline|Tree|Table
    outlineReveal: string?              // [есть] InlineLazy|Navigate
)
```

**`singleton` — единственная ось (никакого `role` в ядре).** Методология = виды + правила,
единообразно, вне зависимости от числа досок; classic (1 вид) и quartet (4 вида) структурно
одинаковы, classic НЕ особенный. `singleton` — свойство ВИДА: «≤1 открытой доски этого вида на
инстанс». Квартет-виды ставят `true` (две spec-доски сломали бы резолв idea→spec→work); утилитарные
— `false` (на $system `client-issues` и `roadmap` — две доски вида `simple` в одном инстансе). Автор
методологии ставит per-kind; никакого исключения для classic. Сегодня зашито на членство в enum
(`ProcessRoleKinds.Contains`, `MethodologyInstanceService.cs:328`) — переносим в поле.

> **Почему нет `role: process|auxiliary`.** Черновик нёс вторую ось «что трогает switch, а что
> сквозное (wiki)» как поле на `Kind`. Решено (владелец, см. §2a): эта ось — не поле, а **ДОМ**
> вида — объявлен ли `Kind` внутри `MethodologyDefinition` активного инстанса (process, трогает
> switch) или в проектном utility-наборе (переживает switch структурно, не по конвенции значения
> поля). `singleton` остаётся единственной осью НА самом `Kind`, вне зависимости от дома.
> (`ProcessRoleKinds`/`Methodological`, `MethodologyInstanceService.cs:328`, сегодня одним и тем же
> списком и гейтят singleton, и маркируют «не трогается при switch» — расщепляются по этому же
> принципу: дом решает и то, и то, поле не нужно ни для чего из этого.)

`boardName` полезен независимо: сегодня доски именуются слагом вида (`PickBoardName`), шаблон не
может дать доске собственное имя/display — это поле даёт.

## 2a. Utility-слой (project-owned kinds) `[НОВОЕ]`

Кроме процессных видов внутри активного `MethodologyDefinition`-инстанса, проект несёт
**utility-слой** — виды, homed НЕ в методологии, а на проекте, и потому переживающие `switch`
методологии структурно, а не по конвенции. Определяются ТЕМ ЖЕ `Kind`-примитивом (workflows/
statuses/transitions/линки — весь §2-§7), просто без домашнего инстанса:

```
ProjectUtilityKinds(
    projectKey: string,
    kinds: Kind[]                        // builtin `wiki`/`simple` + project-declared
)
```

Доска — член ровно одного из двух миров: активного инстанса методологии (process-доска,
`board.MethodologyInstance` указывает на инстанс) ИЛИ проектного utility-набора (utility-доска,
явное членство в utility-наборе, не «безымянная»). Это первоклассит сегодняшнюю «legacy
null-membership» доску (`TasksService.cs:106-117`) — сегодня null-membership исторический
fallback для pre-instance проектов, а не осознанный третий статус членства.

**Отсюда и вытекает `singleton`-семантика без `role`:** «процессный vs сквозной» — это ГДЕ вид
homed (инстанс методологии vs utility-слой), не поле на `Kind`. `singleton` остаётся ЕДИНСТВЕННОЙ
осью на самом `Kind` (см. §2) вне зависимости от дома — utility-виды тоже вольны объявить
`singleton:true` (напр. `wiki`, если методология хочет ровно одну вики-доску на проект) или
`false` (`simple` — можно несколько, как `client-issues`/`roadmap` на `$system`, обе `simple`,
`singleton:false`).

## 3. Workflow / Status

```
Workflow(
    types: string[],                    // [есть] типы, разделяющие один автомат
    statuses: Status[],                 // [есть] statuses[0] — начальный
    transitions: Transition[]           // [есть]
)

Status(
    slug: string,                       // [есть]
    category: StatusCategory,           // [есть] Open | TerminalOk | TerminalCancel
    description: string?                // [НОВОЕ] «этот статус значит…»; едет в скилл
)
```

## 4. Transition (переход + гейты)

```
Transition(
    from: string,                       // [есть]
    to: string,                         // [есть]
    description: string?,               // [НОВОЕ]

    // Гейты (объявление). Два концептуальных гейта, не три:
    requiresApproval: bool = false,     // [есть] гейт «КТО» — только владелец (это НЕ артефакт-коммент)
    requiredArtifacts: RequiredArtifact[],  // [ОБЪЕДИНЯЕТ reason + preconditionArtifact]
    checklist: string[],                // [есть] — КОНВЕНЦИЯ, не серверный гейт: не входит в
                                         // GateEnforcement/enforce, сегодня нигде не enforced
                                         // (`GateEnforcement`-семейство его не знает); едет только
                                         // в скомпилированный скилл как чеклист для агента

    enforce: GateEnforcement?           // [НОВОЕ] что сервер блокирует; null → strictMode дефиниции
)

RequiredArtifact(                       // reason — это precondition-артефакт со слагом «reason»
    slug: string,                       // «reason» | «spec_plan» | кастом → нужен коммент artifact:<slug>
    inline: bool = false                // true = подаётся В ЭТОМ ЖЕ вызове (как reason); false = должен уже быть
)

GateEnforcement(                        // [НОВОЕ] что сервер БЛОКИРУЕТ
    approval: bool = strictMode,        // сегодня EnforceApproval (у builtin false → конвенция)
    artifacts: bool = true              // reason И precondition сегодня оба жёсткие (WorkflowEngine.cs:56 / RequirePreconditionArtifactsAsync)
)
```

**Известное ограничение:** `enforce.artifacts` — один bool на ВЕСЬ список `requiredArtifacts`
перехода; нельзя сделать «`spec_plan` жёстким, `reason` мягким» на одном и том же переходе.
Осознанно принято для v1 (см. `03-mcp-and-migration` A.1) — фиксируем как известный компромисс,
не как забытый кейс.

Замысел: **объявление гейта** отделено от **его силы** (`enforce`). Объявление всегда компилируется в
скилл («агенту нельзя Review→Done без аппрува»). Сила решает, блокирует ли сервер. Два гейта:
`approval` (кто) и `requiredArtifacts` (какие комменты нужны — `reason` inline или `spec_plan`
пред-существующий). По факту кода артефакты (и reason, и precondition) жёсткие всегда; мягок по
умолчанию только approval (`EnforceApproval=false` у builtin). `strictMode=true` включает и approval.

> Замечание (сверено с кодом): сегодняшнее поведение = `strictMode=false`, `enforce.artifacts=true`
> (reason и precondition оба жёсткие), `approval` мягкий. Эти дефолты воспроизводят квартет/classic
> **без изменения** поведения. Прежний черновик ошибочно считал reason конвенцией — исправлено.

> **Правило «рождения в гейченный статус» — АСИММЕТРИЧНО между approval/precondition и inline
> `reason`** (уточнено по коду в раунде 2 — прежняя формулировка «тот же гейт применяется к прямому
> рождению» была неточна для `reason`). Три пункта, зафиксированные явно:
>
> (a) **`inline:true` НЕ гейтит рождение узла.** `reason` — единственный сегодняшний inline-
>     артефакт (см. (b)) — на рождении пропускается: `PersistReasonCommentsAsync` явно уходит
>     раньше проверки, когда нет предыдущего статуса (`TasksService.cs:1829`, комментарий в коде —
>     «birth — RequiresReason only applies to transitions»). Рождение не проходит через переход,
>     поэтому требование reason'а конкретного перехода к рождению не применяется.
>     **Не-inline precondition-артефакт (`spec_plan`) рождение, наоборот, ГЕЙТИТ**:
>     `RequirePreconditionArtifactsAsync` (`TasksService.cs:1738-1758`) — родиться СРАЗУ в статусе,
>     куда ведёт гейченный precondition-переход, нельзя без УЖЕ существующего `artifact:<slug>`-
>     комментария (комментарий физически не может предшествовать рождению узла, которого ещё нет —
>     отсюда и асимметрия с inline).
> (b) **Inline-канал содержимого — только `reason`.** Вызов сегодня несёт РОВНО одно inline-поле
>     (`NodePatch.Reason`). Кастомный `requiredArtifacts[{slug:X, inline:true}]` для `X ≠ "reason"`
>     в v1 **не поддержан** — нет вызова, куда положить содержимое. Правило v1: `inline:true`
>     легален только при `slug:"reason"`; констрейн с кастомным inline-слагом — ошибка валидации
>     дефиниции, не рантайм-сюрприз на первой записи.
> (c) **Рождение в статус с ГЕТЕРОГЕННЫМИ входящими гейтами** (часть переходов `*→S` гейчена
>     precondition-артефактом, часть — нет): сегодня `wf.Transitions.FirstOrDefault(...)` берёт
>     ПЕРВЫЙ гейченный переход по порядку объявления и требует ЕГО артефакт
>     (`TasksService.cs:1742-1746`) — результат зависит от порядка, не от намерения автора.
>     Правило: **валидатор дефиниции ЗАПРЕЩАЕТ гетерогенные гейты на входах в один статус** — если
>     хоть один переход `X→S` гейчен precondition-артефактом, ВСЕ переходы `*→S` обязаны требовать
>     тот же артефакт. Снимает зависимость от порядка вместо того, чтобы её документировать.

## 5. LinkKind — параметризуемый вид связи (ядро редизайна)

Сегодня шесть process-связей зашиты в `ProcessRelationKinds` с направленностью в коде. Делаем их
**объявляемыми**. Builtin остаётся только универсальный субстрат; квартет-цепочка — данные.

```
LinkKind(
    slug: string,                       // [есть] «idea_spec», «task_spec», «issue_task», кастомные
    description: string?,               // [есть] едет в скилл
    category: LinkCategory = neutral,   // [в данные] process | neutral
    direction: LinkDirection?           // [НОВОЕ] семантика концов (см. ниже); null для neutral
)
// Сахарных upsert-полей у declared-видов НЕТ — они выражаются общим links:{kind:ref} (см. 03-mcp).
// slug ОБЯЗАН быть уникален в рамках методологии и не совпадать с builtin-видом.

LinkDirection(
    fromKind: string?,                  // вид узла-ИСТОЧНИКА хранимого ребра; null = не ограничен
    toKind: string?,                    // вид узла-ЦЕЛИ хранимого ребра; null = не ограничен
    label: string?                      // человекочитаемое прочтение — в скилл (может отличаться от ориентации)
)
```

> **`direction` = ориентация ХРАНИМОГО ребра** (`relations.FromNodeId → ToNodeId`), не «семантическое
> прочтение». Это критично: `Effect.direction: incoming/outgoing` считается относительно неё. Реальные
> ориентации (сверено): `idea_spec` = ideas→spec (`RelationStore.cs:36`), `issue_task` = intake→work,
> `task_spec` = work→spec. Прочтение («спека реализует идею») кладём в `label`, а не в fromKind/toKind —
> иначе эффекты не сматчатся (напр. автозакрытие интейка молча умрёт).

**Builtin (всегда доступны, не объявляются):** `part_of`, `blocks`, `supersedes` (process-субстрат,
универсальны) + `relates_to`, `depends_on`, `mirrors` (neutral). Их семантика структурна для графа
задач, поэтому остаётся в движке.

**Объявляемые методологией (данные):** `idea_spec`, `task_spec`, `issue_task` и любые кастомные.
Движок больше не знает их имён — он знает лишь механику: направленный typed-edge, на который могут
ссылаться `LinkConstraint` и `Effect`.

**Сахар только для универсального субстрата.** `specRef`/`ideaRef` из типизированного контракта
**уходят** — они были квартет-хардкодом (методология может не иметь ни `spec`, ни `ideas`). Сахарные
upsert-поля остаются лишь у builtin, которые есть ВСЕГДА: `blockedBy` (→`blocks`), `supersedes`,
`partOf`. Всё объявляемое (`task_spec`/`idea_spec`/кастом) — через `links:{kind:ref}`; их имена и
смысл агент узнаёт из скомпилированного guide (`03-mcp`).

**Уникальность.** В рамках одной методологии слаги `Kind.kind` и `LinkKind.slug` обязаны быть
уникальны и не коллизить с builtin-видами связей. Валидируется на upsert дефиниции.

## 6. LinkConstraint (требование связи при создании/записи)

```
LinkConstraint(
    type: string,                       // [есть] тип узла, требующий связь; "*" = любой тип [НОВОЕ: wildcard]
    atStatus: string?,                  // [НОВОЕ] констрейн держит СТАТУС узла вместо/вместе с типом —
                                         // «узел, находящийся в этом статусе, обязан нести связь»
                                         // (STATE invariant — см. blocks-пример в §7)
    onTransitionTo: string?,            // [НОВОЕ] узкий вариант atStatus: гейтит только МОМЕНТ входа
                                         // в статус (переходом или прямым рождением в него), не
                                         // удержание после — для «разовых» гейтов входа
    link: string,                       // [есть] слаг вида связи (теперь любой объявленный, не только тройка)
    description: string?,               // [НОВОЕ]
    targetKind: string?,                // [есть] требуемый вид цели
    targetStatuses: string[]?,          // [есть] требуемые статусы цели
    requiredOnEveryWrite: bool = false  // [в данные] при `type`: «нужна при создании» vs «нужна на
                                         // каждую запись» (сегодня «spec требует ideaRef на КАЖДУЮ
                                         // запись» зашито в сервисе — переносим в данные). При
                                         // `atStatus` ОБЯЗАН быть true — state invariant, не разовый
                                         // гейт (иначе используй `onTransitionTo`)
)
```

`requiredOnEveryWrite` разделяет «нужна при создании» (work feature→specRef) и «нужна на каждую
запись» (spec→ideaRef — провенанс). Сегодня разница закодирована в сервисе; переносим в данные.

> **Правило рождения в `Blocked` без блокера** (`RequireBlockersAsync`, `TasksService.cs:2073-2084`)
> — STATE invariant, не транзишн-гейт: код проверяет КАЖДУЮ запись, чей итоговый статус — `Blocked`
> (включая рождение и повторные правки уже-`Blocked` узла), не только момент перехода. Выражается
> как `{type:"*", atStatus: Blocked, link: blocks, requiredOnEveryWrite:true}` — **НЕ**
> `onTransitionTo`: тот проверил бы только момент входа и пропустил бы, например, повторное
> сохранение уже-`Blocked` узла после ручной правки его связей. См. §7 для `blocksGate`, который
> параметризует статус-имя.

## 7. Effect / Delivery / TagAxis / BlocksGate (уже данные, +описание, +on-leave, +blocks-параметры)

```
Effect(                                 // [есть]
    on: string?,                        // [есть] статус ВХОДА — сработать, когда узел ВОШЁЛ в этот статус
    onLeave: string?,                   // [НОВОЕ] статус ВЫХОДА — сработать, когда узел ПОКИНУЛ этот
                                         // статус (ровно одно из on/onLeave задано). Сегодня движок
                                         // знает только вход (`entered`-проверка,
                                         // `TaskTransitionEffects.cs:35`) — onLeave даёт вторую точку
                                         // входа, нужную builtin-`blocksGate` (см. ниже) и потенциально
                                         // будущим авторским эффектам
    link: string, direction: "incoming"|"outgoing",
    set: string, onlyFrom: string?,
    description: string?                // [НОВОЕ]
)

BlocksGate(                             // [НОВОЕ, builtin] статус-гейт «blocks» + куда освобождать
    status: string,                     // «Blocked» сегодня — статус, в котором узел обязан нести
                                         // связь blocks (state invariant, см. §6)
    releaseTo: string                   // «InProgress» сегодня — куда переводит освобождение
                                         // ПОСЛЕДНЕГО блокера (Effect-каскад и delete-путь)
)
// Kind получает необязательное поле blocksGate: BlocksGate? — только у видов с blocks-гейтингом
// (напр. work); отсутствует → узел этого вида не подчиняется blocks-гейтингу вовсе.

Delivery(requiredTypes: string[], defectTypes: string[], link: string)
                                        // [в данные] `link` ОБЯЗАТЕЛЕН, БЕЗ дефолта (было
                                        // `= "task_spec"` — исправлено в раунде 2: дефолт-строка
                                        // вернула бы тот же зашитый литерал под видом поля данных,
                                        // ничего не решив). Роллап сегодня считает по литералу
                                        // "task_spec" (TasksService.cs:1028) — автор методологии
                                        // называет связь явно, компилятор дефиниции требует поле.

TagAxis(namespace: string, description: string?)           // [есть] описание уже есть
```

**`issue_task`-автозакрытие и `blocks`-разблокировка** — оба объявлены как `Effect` в пресете, но
`blocks` несёт ТРИ builtin-механики, которые генерик `Effect` не выражает; данными становятся
только их ПАРАМЕТРЫ-статусы (`blocksGate`), не сама механика:

1. **Gating-consumption ребра `blocks`** (потребить ребро при `set`; разблокировать, только когда
   снят ПОСЛЕДНИЙ блокер) зашита в исполнителе на литерале `"blocks"` (`TaskTransitionEffects.cs:45-
   51`) — builtin-поведение слага, НЕ выразимое генериком `Effect`. Кастомный вид связи gating не
   получает; `blocks` остаётся builtin.
2. **Рождение/удержание в `blocksGate.status` без блокера** — STATE invariant, не транзишн-гейт
   (`RequireBlockersAsync`, `TasksService.cs:2073-2084`, сегодня литерал `"Blocked"`, гейтится
   только на `IsWorkKind`-досках). В данных — `LinkConstraint{type:"*", atStatus: blocksGate.status,
   link: blocks, requiredOnEveryWrite:true}` (§6); заодно снимает завязку на имя вида «work».
3. **Уход из `blocksGate.status` вручную закрывает входящие `blocks`-рёбра**
   (`CloseBlocksOnLeaveAsync`, `TasksService.cs:2087-2096`) и **удаление узла-блокера освобождает
   цель** в `blocksGate.releaseTo` (`TaskTransitionEffects.cs:76-81`, сегодня литерал `"InProgress"`).
   Оба — builtin-поведение, ключ которого теперь данные (`blocksGate.status`/`.releaseTo`), не
   отдельные авторские `Effect`-записи: это ТОТ ЖЕ on-leave/on-delete крючок движка, что и
   generic `Effect.onLeave` выше, просто зашит на слаг `blocks` так же, как gating-consumption в п.1.

## 8. Компиляция в скилл (descriptions + гейты → стартовый контекст)

Рендер `MethodologyGuide.Render` уже эмитит markdown + структурные инварианты
(`approval_gate`, `precondition_artifact`, `link_constraint`, `transition_effect`, …). Правки:

- **Описания подхватываются автоматически** — рендер уже вставляет `description` для `LinkKind`;
  расширяем на статусы/переходы/виды/эффекты/констрейны. Прозы взять неоткуда сегодня — теперь есть.
- **Компиляция — по явной команде, НЕ авто-вшивание в стартовый хук.** Авто-инъекцию процесса в
  стартовый контекст (и всю возню с LKG/устареванием/бюджетом баннера) **выбрасываем**. Остаётся
  явная команда `petbox-wire`, которой пользователь/агент компилирует guide активной методологии в
  скилл, когда нужно. Чтобы пользователь не учил команды — **скилл самого `petbox-wire`**: владелец
  говорит агенту «настрой/обнови процесс», агент делает через этот скилл. Pull, а не push.
- **Гейты — главное для агента.** Компилируем не список статусов, а запреты: «Review→Done — владелец»,
  «spec требует ideaRef на accepted-идею», «переход требует reason/artifact». Уже есть как
  `invariants`; добавляем к ним человекочитаемые описания.

## 9. Что из кода исчезает (сводка «в данные»)

| Сегодня (код) | Становится |
|---|---|
| `enum BoardKind` + `ProcessRoleKinds`/`Methodological` | строковый `Kind.kind` + флаг `Kind.singleton` |
| singleton на `Enum.TryParse<BoardKind>` | флаг `Kind.singleton` per-kind из данных |
| `ProcessRelationKinds` (6 builtin) | builtin-субстрат (part_of/blocks/supersedes/neutral) + объявляемые `LinkKind` для цепочки |
| `LinkField(task_spec)→specRef` в коде | общий `links:{kind:ref}`; сахар только у универсальных builtin (blockedBy/supersedes/partOf) |
| имя доски = слаг вида (`PickBoardName`), нет display | `Kind.boardName` (имя/display из шаблона) |
| `DefaultProvisioningPreset="quartet"` | seed-дефиниция + выбор при онбординге |
| нет прозы на примитивах | `description?` на всех |
| `EnforceApproval` (частный флаг) | единый `GateEnforcement` + `strictMode` |
| `requiresReason` + `preconditionArtifact` (два гейта) | единый `requiredArtifacts:[{slug,inline}]` |
| Legacy null-membership доска (`TasksService.cs:106-117`) | Utility-слой: явное членство в проектном utility-наборе (§2a) |
| `RequireBlockersAsync`/`CloseBlocksOnLeaveAsync`/delete-unblock литералы `"Blocked"`/`"InProgress"` | `Kind.blocksGate{status,releaseTo}` + state-invariant `LinkConstraint{atStatus,...}` (§6/§7) |
| `Effect` срабатывает только на ВХОД (`TaskTransitionEffects.cs:35`) | `Effect.onLeave` — вторая точка входа (§7) |
| `Delivery.link` дефолт `= "task_spec"` | `Delivery.link` обязателен, дефолта нет (§7) |
