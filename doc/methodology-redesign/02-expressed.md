# Выражение реальных методологий в новой модели (v3)

> Проверка: всё, что сегодня работает, выражается данными без потери семантики. Значения статусов/
> переходов/связей — из реального пресета (`MethodologyPresets.cs`). Описания заполнены как пример
> того, что поедет в скилл (сегодня их нет).
>
> **Нотация гейтов** (сокращённо): `{approval}` = `requiresApproval`; `{reason}` =
> `requiredArtifacts:[{slug:reason, inline:true}]`; `{artifact:X}` = `requiredArtifacts:[{slug:X}]`
> (пред-существующий). Ниже местами оставлены старые имена `{requiresReason}`/`{preconditionArtifact:X}`
> для читаемости — они означают то же самое в объединённой модели `requiredArtifacts`.

## 1. КВАРТЕТ

### 1.1. Объявляемые виды связей (были builtin — теперь данные)

```
# Все три — объявляемые виды; адресуются через links:{kind:ref}, сахарных полей нет.
linkKinds:
  - slug: idea_spec
    description: "Спека реализует принятую идею. Провенанс: каждый лист спеки восходит к идее,
                  которую владелец принял. Без этого спека — код без требования."
    category: process
    direction: { fromKind: ideas, toKind: spec, label: "реализует" }   # ориентация хранимого ребра: idea→spec

  - slug: task_spec
    description: "Задача поставляет обещание спеки. feature/bug несут способность/дефект против
                  конкретного листа спеки; chore — нет (чистка без нового обещания)."
    category: process
    direction: { fromKind: work, toKind: spec, label: "поставляет" }

  - slug: issue_task
    description: "Задача закрывает интейк-issue. Когда работа доходит до Done, входящий issue
                  автозакрывается."
    category: process
    direction: { fromKind: intake, toKind: work, label: "закрывает" }   # ориентация ребра: intake→work (эффект incoming на work)
```

### 1.2. Виды-доски

> Все четыре вида квартета несут enforced оси тегов `area`, `concern` (`MethodologyPresets.cs:283-293`);
> classic/simple — free-form (осей нет). Ниже `tagAxes` опущены для краткости, но в дефиниции присутствуют.
> Все четыре — `singleton: true` (по одной доске вида на инстанс).

**intake** (`singleton: true`, `boardName: intake`)
```
description: "Необработанный вход: замеченные проблемы до триажа."
quickAddAllowed: true, defaultView: table
workflows: [ types: [issue]
  statuses:
    reported(Open)  "замечено, не разобрано"
    triage(Open)    "в разборе"
    confirmed(Open) "подтверждено, ждёт работы"
    duplicate(TerminalCancel), wontfix(TerminalCancel), done(TerminalOk)
  transitions:
    reported→triage
    triage→confirmed
    triage→duplicate   { requiresReason }
    triage→wontfix     { requiresReason }
    confirmed→done     { requiresApproval }
]
```

**ideas** (`singleton: true`, `boardName: ideas`)
```
description: "Идеи: от сырой формулировки до принятой владельцем."
quickAddAllowed: true, defaultView: tree
workflows: [ types: [idea]
  statuses:
    raw(Open) "сырая мысль"
    exploring(Open) "исследуется"
    review(Open) "готова к решению владельца — несёт spec_plan"
    deferred(Open), accepted(TerminalOk) "владелец принял", rejected(TerminalCancel)
  transitions:
    raw→exploring
    exploring→review   { preconditionArtifact: spec_plan,
                         description: "перед review нужен spec_plan — набросок обещаний" }
    review→accepted    { requiresApproval, description: "принимает только владелец" }
    review→exploring
    review→rejected    { requiresReason }
    exploring→rejected { requiresReason }
    exploring→deferred, deferred→exploring
]
```

**spec** (`singleton: true`, `boardName: spec`)
```
description: "Каталог обещаний-способностей. Лист спеки — одно обещание, defined-born."
quickAddAllowed: false, defaultView: outline (InlineLazy)
workflows: [ types: [spec]
  statuses: defined(Open) "живое обещание"; deprecated(TerminalCancel) "снято"
  transitions: defined→deprecated
]
linkConstraints:
  - type: spec, link: idea_spec, targetKind: ideas, targetStatuses: [accepted],
    requiredOnEveryWrite: true,
    description: "любая запись листа спеки требует связь idea_spec (links) на принятую идею"
delivery: { requiredTypes: [feature], defectTypes: [bug], link: task_spec }
  # link ОБЯЗАТЕЛЕН (не дефолт) — роллап сегодня литералом task_spec, TasksService.cs:1028
```

**work** (`singleton: true`, `boardName: work`, `autoWireFrom: spec`,
`blocksGate: {status: Blocked, releaseTo: InProgress}`)
```
description: "Исполнение: feature/bug/chore от Pending до Done."
quickAddAllowed: false, defaultView: kanban
workflows: [ types: [feature, bug, chore]
  statuses:
    Pending(Open), InProgress(Open), Review(Open) "потолок агента",
    Done(TerminalOk) "выставляет владелец", Blocked(Open), Cancelled(TerminalCancel)
  transitions:
    Pending→InProgress, InProgress→Review, Review→InProgress
    Review→Done { requiresApproval, description: "Done ставит владелец, не агент" }
    InProgress→Blocked, Blocked→InProgress
    Pending→Cancelled, InProgress→Cancelled, Review→Cancelled
]
linkConstraints:
  - type: feature, link: task_spec, targetKind: spec, description: "feature несёт task_spec (links) на лист спеки"
  - type: bug,     link: task_spec, targetKind: spec, description: "bug несёт task_spec (links) на лист спеки"
  # chore — не назван → освобождён (изъятие = данные)
  - type: "*", atStatus: Blocked, link: blocks, requiredOnEveryWrite: true,
    description: "в статусе Blocked узел обязан нести связь blocks (на каждую запись, включая рождение)"
    # замена RequireBlockersAsync (TasksService.cs:2073-2084) — STATE invariant, не onTransitionTo:
    # держит инвариант, пока узел в Blocked, не только в момент входа (01-model §6)
effects:
  - on: Done, link: issue_task, direction: incoming, set: done,
    description: "закрыть входящий интейк-issue при Done"
  - on: Done, link: blocks, direction: outgoing, set: InProgress, onlyFrom: Blocked,
    description: "разблокировать зависящие задачи при Done"
  # blocksGate.status/.releaseTo (выше) также параметризуют on-leave-закрытие входящих blocks-рёбер
  # (CloseBlocksOnLeaveAsync) и delete-разблокировку — builtin-поведение движка, не отдельные
  # авторские Effect-записи (01-model §7)
```

**Замечание про сегодняшнее поведение (сверено с кодом):** `strictMode` квартета = `false`, но
`preconditionArtifact` И `reason` остаются жёсткими (reason блокирует `WorkflowEngine.cs:56-57` —
`triage→duplicate/wontfix`, `review→rejected` требуют причину на сервере). Мягок только `approval`
Review→Done (`EnforceApproval=false` → конвенция: агент не должен, но сервер не блокирует). Это ровно
нынешнее наблюдаемое поведение — новая модель его не меняет.

## 2. CLASSIC

```
kind: classic (singleton: false, boardName: classic)
# classic — обычная методология из ОДНОГО вида; правила единообразны, count досок к ним отношения не
# имеет. singleton: false — сегодняшний дефолт кардинальности вида (classic-досок можно много); это
# свойство ВИДА, а не особый случай classic.
description: "Одна доска в духе GitHub/Jira/Linear: свободные статусы, без пайплайна и гейтов."
quickAddAllowed: true
workflows: [ types: [task, feature, bug]
  statuses: Backlog, Todo, InProgress, InReview (Open); Done(TerminalOk);
            Cancelled, Duplicate (TerminalCancel)
  transitions: свободные между всеми Open; любой Open→Done; Open→Cancelled;
               Open→Duplicate{requiresReason}; любой терминал→Todo
]
# без linkConstraints/effects/delivery/autoWire; теги free-form
```

> **На $system классик — utility-доска, не процесс** (owner-решение, раунд 2 — см. `00-overview`
> «Методология vs utility-слой», `01-model` §2a). Классик как МЕТОДОЛОГИЯ (одновидовой пресет
> выше) — легитимный процесс, который можно завести отдельным инстансом. Но КОНКРЕТНАЯ доска
> `classic` на `$system` сегодня не член активного quartet-инстанса (легаси, `TasksService.cs:106-
> 117`) — в новой модели она re-homed в проектный utility-слой (`03-mcp-and-migration`, шаг
> B.2 п.12), как `wiki`/`client-issues`/`roadmap`. «classic-методология» и «classic-доска на
> $system» — два разных объекта, случайно одноимённых; путать их — источник ошибок.

## 3. SIMPLE

```
kind: simple (boardName: simple)
# utility-kind (01-model §2a): homed на проекте, не в методологии — не член singleton-инварианта
# quartet-инстанса, переживает switch структурно. singleton — per-kind, как у любого вида:
# client-issues/roadmap на $system — обе simple, обе singleton:false (несколько досок разрешено).
description: "Минимальная доска: пять статусов, свободные переходы, никаких требований."
workflows: [ types: [task, bug, feature, chore, issue]
  statuses: Todo, InProgress, Blocked (Open); Done(TerminalOk); Cancelled(TerminalCancel)
  transitions: все-ко-всем (свободные)
]
```

## 4. WIKI (utility-доска — пример project-homed вида)

Живёт на $system уже сейчас (`kind: "wiki"`, вне enum). В новой модели — utility-kind: `Kind`
объявлен в проектном utility-наборе (`01-model` §2a), а не внутри `MethodologyDefinition`
активного инстанса.

```
kind: wiki (boardName: wiki)
# utility-kind (01-model §2a) — homed в проектном utility-наборе, не в методологии
description: "Рабочая вики: как что устроено НА САМОМ ДЕЛЕ, на время разработки. Страница не
              авторитетна — объясняет код/спеку/конфиг и обязана назвать источник."
quickAddAllowed: true, defaultView: table
workflows: [ types: [page]
  statuses: draft(Open), live(Open) "актуальна", promoted(TerminalOk) "уехала в /doc",
            stale(TerminalCancel) "разошлась с кодом — правда за кодом"
  transitions: draft→live, live→promoted, live→stale, stale→live
]
```

Ключевое: сегодня wiki «не process-роль» лишь потому, что её слаг не в enum. В новой модели это —
не свойство самого `Kind` (никакого `role`), а **дом объявления**: `wiki` homed в utility-слое
проекта, а не в методологии, поэтому `switch` методологии её не касается СТРУКТУРНО, а не по
конвенции значения поля.

## 5. Пример «с нуля»: методология под разработку через user-story

Демонстрирует, что не-квартетный процесс со своими гейтами и связями выражается теми же данными.

**Замысел:** `story → design → build → verify`. Story описывает пользовательскую ценность; design —
как сделаем (гейт: перед build нужен набросок дизайна); build — код; verify — проверка на реальном
поведении (гейт: verify→done только владельцем). Связь `story_impl` привязывает build к story.

```
name: user-story-flow
description: "Лёгкий продуктовый поток: пользовательская история → дизайн → сборка → проверка."
strictMode: true          # весь процесс жёсткий на сервере

linkKinds:
  - slug: implements_story
    description: "Сборка реализует пользовательскую историю."
    category: process
    direction: { fromKind: build, toKind: stories, label: "реализует" }
    # сахара нет — адресуется через links:{implements_story: <story>}

kinds:
  - kind: stories (singleton: true, boardName: stories)
    description: "Пользовательские истории: ценность с точки зрения пользователя, не решение."
    workflows: [ types: [story]
      statuses: draft(Open), ready(Open) "принята в работу", shipped(TerminalOk), dropped(TerminalCancel)
      transitions: draft→ready{requiresApproval, description:"историю в работу пускает владелец"};
                   ready→shipped; ready→dropped{requiresReason}
    ]
  - kind: build (singleton: true, boardName: build, autoWireFrom: stories)
    description: "Сборка: реализация одной истории."
    workflows: [ types: [task]
      statuses: todo(Open), doing(Open), verify(Open) "ждёт проверки", done(TerminalOk), cut(TerminalCancel)
      transitions: todo→doing;
                   doing→verify { preconditionArtifact: design_sketch,
                                  description: "перед проверкой приложи набросок дизайна" };
                   verify→done  { requiresApproval, description: "приёмку делает владелец" };
                   doing→cut{requiresReason}
    ]
    linkConstraints:
      - type: task, link: implements_story, targetKind: stories, targetStatuses: [ready],
        description: "каждая сборочная задача несёт связь implements_story (links) на принятую историю"
```

**Пример использования** (как агент ведёт доску на этой методологии):
1. `story «пользователь логинится по magic-link»` создаётся в `draft`.
2. Владелец: `draft→ready` (аппрув; сервер блокирует без него — `strictMode`).
3. Агент создаёт `build`-задачу со связью `links: { implements_story: <story> }` (констрейн требует ready-историю).
4. Агент: `todo→doing→verify`. Перед `verify` кладёт комментарий `artifact:design_sketch` —
   иначе сервер блокирует переход (precondition).
5. Владелец: `verify→done` (аппрув).

Скомпилированный (явной командой `petbox-wire`) скилл этой методологии донёс бы агенту: доски
`stories/build`, что `verify` требует `design_sketch`, что `done`/`ready` — только владелец, что build
обязан нести `implements_story`. Всё — из описаний и гейтов в данных, без изучения команд вручную.
