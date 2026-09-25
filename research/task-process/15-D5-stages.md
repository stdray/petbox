# D5 Этапы — инвентарь

Дата 2026-09-25, автор Sonnet 5 (`claude-sonnet-5`). Источники просмотрены: 9 узлов tasks_search
(волна/этап/milestone), ~80 узлов доски `roadmap` (полный листинг), 1 relations_list,
`tasks_methodology_rules_get(quartet)`, `doc/methodology.md`@origin/main, `GitVersion.yml`@origin/main,
`build.cs`@origin/main, `git tag -l`, `research/spec-plan-convention.md`.

## 1. Суть проблемы

Группировки работы во времени/по объёму («MVP», «волна N», «этап N», «фаза N») в quartet
**нет как механизма** — есть только текст в заголовках узлов, не читаемый ни `groupBy`, ни
фильтром, не роллапящийся, ничего не архивирующий при закрытии. При этом PetBox **уже
строил** ровно такую сущность раньше (доска `roadmap`, ниже) и молча её оставил при переходе
на quartet — значит, вопрос не «изобрести», а «понять, почему прошлую бросили, и не повторить».

## 2. Доказательства

### 2a. Карточки

| узел | статус | суть |
|---|---|---|
| `ideas/spec-backfill-config-data` … `-observability` … `-access-protocol` … `-memory-sessions-misc` | все `accepted` | 4 идеи, буквально «Spec-backfill волна 1..4»; каждая — контейнер на МНОЖЕСТВО spec-листьев (idea_spec-рёбра: у волны 4 их 16, см. 2d), группировка чисто в title, тег только `area:*`/`concern:self-improvement` |
| `work/kql-coverage-decomposition` | Done | «декомпозиция на волны» — тоже только в title |
| `ideas/kql-aggregations` | accepted | «расширение волнами» — тоже только в title |
| `work/wire-qwen-scenario-decide` | Done | title «Этап 5: …», теги `area:agent-wiring, concern:dx, lane:standard` — номер этапа НЕ вынесен в тег |
| `roadmap/methodology` (умбрелла, partOf) → `phase0-adopt` «Фаза 0», `p1-review-gate`/`p1-report-issue`/`p1-type-field` «Фаза 1: …» | closed board, статусы InProgress/Todo | реальная Phase-иерархия через `partOf`, built до quartet |
| `roadmap/phase30` «Phase 30 — dogfooding readiness» → `prep-landed`, `deferred` | closed board | ещё одна фаза-умбрелла тем же паттерном |
| `roadmap/tree-3-levels-ui` | Done | title дословно: «UI показывает только 2 уровня дерева вместо 3 (**Phase/Wave/Task**)» — прямое доказательство, что 3-уровневая Phase→Wave→Task иерархия была ЗАДУМАННЫМ UI-паттерном, не случайностью |

### 2b. Наблюдения — не искал (не по направлению; см. §8 находка).

### 2c. Сессии — `session_search` по «этап волна milestone sprint релиз стадия» **упал таймаутом
(300с)**, не перезапрашивал (см. §9). Не проверено.

### 2d. Память/канон/код

- `tasks_methodology_rules_get(quartet)`: `tagAxes` = ровно `area`, `concern`, `lane`
  (lane — «класс обслуживания», описан как открытая конвенция из 4 значений). **Оси
  stage/milestone/wave нет** — факт, не гипотеза.
- `relations_list(spec-backfill-memory-sessions-misc)`: 16 рёбер `idea_spec`, **ноль** рёбер к
  другим «волна N»-идеям — волны 1-4 не связаны между собой никаким relation, только
  порядковый номер в title.
- `tasks_board_list`: доска `roadmap` — `kind:"simple"`, `methodologyInstance:"$utility"`,
  `closed:true`, создана 2026-05-31 (до quartet, 2026-06-02). Это и есть та самая
  Phase/Wave/Task-сущность — **закрыта, не мигрирована и не удалена**; узлы на ней (~80,
  вывод обрезан бюджетом) до сих пор находятся обычным `tasks_search` (статусы Todo/InProgress
  живые, найдены выше без специального statusKind).
- `doc/methodology.md`@origin/main, секция «## Iterations» (строки ~213-218): «An iteration =
  a **filtered backlog**… No sprint ceremony / story points. It closes with a **release**
  whose artifacts are: test results + version tag + build + deploy target (= our CI `ci.NNN`
  + `commits[]` + deploy)». Это уже канон — но про CI-релиз, не про MVP/стадию продукта;
  «adoption status» той же главы отсылает к `$system/roadmap` «(mcp-typing и methodology
  phases)» — т.е. канон САМ ссылается на закрытую доску как на источник фаз.
- `GitVersion.yml`@origin/main: `mode: ContinuousDelivery`, `next-version: 0.1.0`; ветка
  `main` → `increment: Patch`, `label: 'ci'`. Версия сегодня всегда вида `0.1.x-ci.NNNN` —
  **нет ручного шага бампа** вообще, ни на коммит, ни на релиз.
- `git tag -l`: `deploy`, `npm`, `npm-wire`, `nuget`, `pypi`, `deploy-origin` — фиксированные
  ИМЕНА, двигаются `git tag -f <tag> <sha>` на любой коммит (AGENTS.md §Deploy). Версия как
  число нигде не роняется вручную и не участвует в решении «что задеплоить».

## 3. Что уже решалось раньше

`roadmap` — это и есть прошлое решение D5 (Phase→Wave→Task через `partOf` на отдельной
simple-доске), причём решало заодно и D4: сама доска сейчас `closed:true`, то есть «закрыть
этап» когда-то буквально означало «закрыть доску целиком». Никакого артефакта о ПРИЧИНЕ отказа
от паттерна не нашёл (сессии не проверены, см. 2c) — это открытый вопрос, не факт.

## 4. Механизм

[гипотеза] Quartet's `ideas`→`spec`→`work` цепочка сама по себе не knows про группировку
шире одного идея-узла; `partOf`/`groupBy` существуют в движке (использованы для spec-дерева
и roadmap), но ничто в quartet не провоцирует агента их использовать для стадий — поэтому
агенты тянутся к самому дешёвому каналу: тексту в title («волна N», «Этап N»), который не
requires никакого нового primitive и ничего не ломает.

## 5. Индустрия (≤10 строк)

Time-boxed (фикс. календарная каденция, скоуп плавает): Scrum sprint, Linear **Cycle**
(авто-rollover недоделанного), Shape Up 6-week cycle + 2-week cooldown, SAFe PI
(8-10 нед, межкомандный). Scope-boxed (фикс. скоуп, дата плавает): GitHub **Milestone**
(due date опционален, issue↔milestone M:1, закрытие не удаляет issues), Linear **Project**
(target date, вложенные milestones), semver release trains. PetBox — одиночный владелец,
непрерывный деплой per-commit (не per-release), агентская нагрузка рваная — календарной
каденции синхронизировать НЕЧЕГО (нет команды, нет ритма встреч); doc/methodology.md уже
явно отверг sprint-ceremony и определил «итерацию» как CI-релиз. Значит если стадия и
нужна — она **scope-boxed** (milestone/wave), не time-boxed (sprint/cycle).

## 6. Варианты

**A. Только тег-ось** (`stage:mvp` / `stage:wave-4`, открытая ось как `lane`).
Цена: дёшево — один `tasks_methodology_rules_upsert`, сразу даёт `groupBy`/фильтр.
Ломает/не решает: не решает D4 (у тега нет «закрытия», нечего архивировать), нет FSM
(узел может нести два конфликтующих stage-тега), нет роллапа готовности стадии.

**B. Умбрелла-узел через `partOf`** (повторить старый Phase/Wave/Task-паттерн, но НА
действующих досках, а не на отдельной `roadmap`). Цена: почти нулевая — механизм `partOf` +
роллап уже есть (spec `delivery` считается так же). Ломает/не решает: `work`-доска не имеет
типа «эпик/стадия» в FSM (только feature/bug/chore) — статус самой умбреллы Done/Pending
семантически не определён; не решает D4 без отдельного архив-механизма; риск повторить
судьбу `roadmap` (заброшена, неизвестно почему).

**C. Первоклассная сущность Stage/Milestone** (новый `kind` доски, FSM open→closed, close
= архивировать вложенные Done). Цена: реальная движковая работа — новый kind, новый link,
и, главное, **архив-при-закрытии нигде не существует даже как primitive**: `roadmap.closed:
true` ничего не заархивировало — её ~80 узлов всё ещё живые и находятся обычным поиском
(2a/2d). Т.е. C требует СНАЧАЛА решить D4 (архив как операция), иначе C — это B с лишней
FSM.

**D. Milestone бампает GitVersion major/minor.** Цена: конфликтует с фактическим режимом —
`ContinuousDelivery`+`Patch`+`ci`-label, версия сегодня НЕ участвует в решении «что
задеплоено» (это делают floating-теги `deploy`/`npm`/…, двигаемые на коммит, не на релиз).
Заставить close-стадии дёргать major/minor значит либо вводить release/feature-branch
семантику GitVersion (конфликт с «push main → двигай deploy сразу», AGENTS.md), либо делать
чисто косметическую запись версии в вердикт-комментарий — что достижимо БЕЗ бампа вообще.

## 7. Несогласия

- Time-boxing (спринт/цикл как календарь) владельцем не предлагался прямо, но раз он
  упоминает «sprint» в одном списке с milestone/wave — стоит явно сказать: agent-driven +
  continuous deploy среда не имеет события, к которому привязать календарную рамку; canon
  (`## Iterations`) это уже признал. Любое решение D5 должно быть scope-boxed, не time-boxed.
- Идея «milestone бампает GitVersion»: сомневаюсь, что цена оправдана. Версия сегодня —
  побочный продукт CI (`ci.NNNN`), не ручной ритуал; единственный ручной ритуал — сдвиг трёх
  fixed-name тегов. Дешевле и без риска — фиксировать версию/коммит ПРОЗОЙ в вердикте
  закрытия стадии (как уже делает `roadmap`'s `deploy-*` узлы: title несёт `(ci.225)` и т.п.
  вручную), чем чинить сборку под это.
- Прежде чем строить B или C, стоит понять, ПОЧЕМУ `roadmap` бросили при переходе на
  quartet, а не мигрировали его Phase/Wave/Task на новые доски — я это не нашёл (session_search
  упал, см. §9). Без ответа велик риск построить то же самое второй раз и снова забросить.

## 8. Связи и попутные дефекты

- **D4**: прямая зависимость — «close стадии архивирует Done» требует архив-как-операцию,
  которой нет вообще ни на одной доске сегодня (см. находку ниже). Решение D5-C физически
  не отделимо от D4.
- **D2**: «волна 1..4» — это `ideas`-узлы, используемые НЕ как обсуждение, а как контейнер
  на 16 spec-рёбер каждый — тот же симптом «идеи как контейнер вместо доски», что находит D2.
- **Дефект (впечатан по правилу §03-FORMAT, не заведён на observations):**
  `tasks_board_close`/`closed:true` не архивирует и не скрывает содержимое доски — доска
  `roadmap` закрыта с 2026-06/07 (точная дата закрытия не проверена), но её ~80 узлов в
  статусах Todo/InProgress/Done ПО-ПРЕЖНЕМУ возвращаются `tasks_search` наравне с активными
  досками (voir 2a/2d, без явного `board`-фильтра). Если D4/D5 рассчитывают на «close = уйти
  с глаз», это сегодня ложно на уровне ДОСКИ, не только карточки.

## 9. Замечания к формату

`session_search` без board-скоупа завис на 300с и абортнулся один раз — не перезапрашивал
из бюджета времени; §2c поэтому помечен «не проверено», а не «пусто». Заметный риск для
следующего инвентаря: закладывать retry или сузить `sessions`/`hitsPerSession` заранее.
