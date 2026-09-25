# 60 — Брифы исполнителям, пакет 1 (пункты 1–5 зонтика `task-process-fixes-2026-09`)

Дата 2026-09-25. Автор — Opus 5.5 (worker-highstakes, разведка). База: `origin/main` `3ff72f15`, прод `e97e5e5`.
Все пять карточек на `work` — дети зонтика (`partOf`), Pending, с тегом `concern:task-process`. Полномочия
на Done — в теле зонтика (Done только после живой проверки). Общие правила для каждого брифа: сначала
worktree в `.claude/worktrees/<slug>` от `origin/main`; гейт в foreground (`./build.ps1 -Target Test`, для
`src/clients-ts/**` — `Verify`); коммит, push, в карточке `commits[]`; вердикт-комментарий при закрытии.

## Сводка

| # | Р | карточка | тип | код | данные quartet | кит |
|---|---|---|---|---|---|---|
| 1 | Р1 | `spec-plan-definition-invisible-to-agents` (переиспользована) | chore (тип неизменяем) + `task_spec` → `methodology-primitive-descriptions` | да, малый | да | нет |
| 2 | Р11 | `status-normalizer-misses-case-variants` (новая) + наблюдение `status-normalizer-case-only-variants-survive` | bug → `primitives-types-statuses-transitions` | одна строка + тест | нет | нет |
| 3 | Р2 | `intake-routing-research-is-chore` (новая) | chore | нет | да (описания видов) | да |
| 4 | Р7 | `stage-tag-axis-quartet` (новая) | chore | нет | да (tagAxes) | нет |
| 5 | Р9 | `node-authoring-why-and-term-definitions` (новая) | chore | валидатор .mjs + тест | нет | да |

Наблюдение `guide-omits-spec-plan-content-convention` связано с карточкой 1 через `observation_obligation`,
а новое наблюдение — с карточкой 2. Оба сами перейдут в `fixed`, когда карточки дойдут до Done.

## Ограничения последовательности (важно)

1. **Документ правил quartet заменяется целиком** (`tasks_methodology_rules_upsert`: всё, что не прислали,
   удаляется; CAS по `version`, сейчас v23). Пункты 1, 3 и 4 правят один и тот же документ. Порядок такой:
   правки делаются **последовательно**, каждый раз свежий `rules_get` → правка → upsert с его `version`.
   Лучше всего — один исполнитель, одна запись со всеми тремя изменениями (описание перехода
   `ideas exploring→review`, описания видов `ideas`/`work`, ось `stage`). Параллельные записи вслепую
   потеряют чужие правки молча, если кто-то соберёт документ из устаревшего чтения.
2. **Кит** (пункты 3 и 5): файлы разные (`templates/petbox-methodology/SKILL.md` и
   `templates/petbox-node-authoring/*`), конфликтов по тексту нет. Ветки независимы, мёржить по очереди, затем
   **один** выпуск `npm-wire` (`git tag -f npm-wire <sha> && git push origin npm-wire --force`). Гейт — `Verify`.
3. **Деплой**: 1 и 2 требуют серверного кода → тег `deploy`. 3 и 4 на сервере — только данные и работают сразу.
   Doc/кит из 3 деплоя не требуют, но doc правится в том же коммите, что и поведение.
4. `doc/methodology.md:278-281` (мёртвая ссылка на `roadmap`) трогают 3 и 4 — делает тот, кто мёржит
   первым, второй только дописывает фразу про `stage`.
5. Данные в пункте 1 можно записать до деплоя: guide уже показывает `description` перехода как `note:`
   в списке переходов. Частичный выигрыш сразу, полный — после деплоя кода.

## Бриф 1 — Р1: гейт `exploring → review` объясняет, что писать в `spec_plan`

**Факт (проверено по коду).** Предположение Р1 «одна правка данных» неверно. У `RequiredArtifactDef(Slug, Inline)`
(`src/PetBox.Tasks.Engine/MethodologyDefinition.cs:364`) нет описания. Строку гейта генерирует код
(`MethodologyGuide.cs:226-237`), текст отказа — тоже (`GuardEngine.cs:408-414`). Данными задаётся
`MethodologyTransitionDef.Description` (`MethodologyDefinition.cs:305`), но он виден только как `note:`
в списке переходов (`MethodologyGuide.cs:177`).
**Рекомендуемая форма (минимальная, без изменения схемы и MCP-контракта):** использовать `Description` перехода.
- `Workflow.cs:37` — `WorkflowTransition` получает `string? Description = null`. Заполнить в
  `MethodologyDefinition.cs:265` (там, где собирается `new WorkflowTransition(...)`).
- `MethodologyGuide.cs:~229-237` — в строку GATES для не-inline артефакта дописать ` — {t.Description}`,
  а в `detail` инварианта `precondition_artifact[_convention]` — ` — {Description}`, если он есть.
- `GuardEngine.cs:~407-414` — обе ветки отказа (создание сразу в статусе и переход) дописывают описание
  (`gated.Description` / `tr.Description`).
- `MethodologyPresets.cs:279` — `new("exploring", "review", PreconditionArtifact: "spec_plan")` получает
  `Description` с тем же текстом, что и данные ниже, на английском или русском по стилю пресета: там русские
  описания linkKinds, значит допустимы оба.
- Тесты: guide-тест (строка GATES и `invariants[].detail` содержат описание) и guard-тест (текст отказа
  содержит описание). Найти существующие по `requires artifact:` и `precondition artifact` в `tests/`.
- Данные $system: `rules_get quartet` → у перехода `ideas exploring→review` поставить `description` =
  «spec_plan — план правок дерева спеки: какие листья появятся, изменятся или станут deprecated при принятии
  идеи (ключ листа, нормативная строка, partOf). Не план работ. Подробно: doc/methodology.md, раздел
  про spec_plan». Остальной документ прислать без изменений.
- Альтернатива (не делать без причины): поле `description` у `RequiredArtifactDef`. Тогда меняются
  `McpToolInputs.cs:369`, `McpToolResults.cs:1125`, `MethodologyWire.cs:228`, валидатор и, возможно, SDK.
  Дороже, а выигрыш — только семантическая точность.
**Риски.** Snapshot-тесты guide-вывода, если есть, поменяются. `MethodologyWire`/TS-типы не затрагиваются.
Для других quartet-проектов описание появится только в новых инстансах (из пресета); существующим нужен
свой `rules_upsert`, и это делает владелец проекта, не мы.
**Живая проверка.** (a) `tasks_methodology_guide` (key `quartet`): в строке GATES для `exploring -> review`
и в `invariants` (`rule: precondition_artifact`) есть «план правок дерева спеки». (b) Отказ: в проекте
`smoke` (sandboxOnly-ключ, AGENTS.md правило 7) создать quartet-инстанс или взять существующий, завести идею,
перевести `raw→exploring→review` без комментария `artifact:spec_plan` — текст ошибки содержит описание.
Без ключа smoke проверить только (a) и отметить это в вердикте. Затем убрать за собой в smoke.

## Бриф 2 — Р11: статусы, отличающиеся от FSM только регистром

**Измерено (прод, 9 досок $system, все statusKind):** ровно два узла — `work/tasks-upsert-nonatomic-on-supersedes-throw`
= `pending` и `client-issues/same-class-cross-tenant-field-id-4c0359` = `done`. Остальные доски чисты.
Другие проекты не проверены: ключ только для `$system` (`Not authorized for project:agent-relay`).
**Опровергнуто:** «фильтры по statusKind промахиваются мимо таких узлов» — `statusKind:[open]` находит
`pending`, `[terminalok]` находит `done`. Вред уже — только там, где статус сравнивают точной строкой.
**Корень.** `src/PetBox.Tasks/Data/NodeIdentityBackfillMigrator.cs:142`
`var status = wf.Has(n.Status) ? n.Status : wf.Initial;`, где `Workflow.Has` регистронезависим
(`Workflow.cs:78-82`). Попутно: при точном сравнении `done` ушёл бы в `Initial`, то есть переоткрылся бы.
**Решение: исправить существующий стартовый мигратор, а не писать новую миграцию и не чинить вручную.**
Мигратор идемпотентен, работает на каждом старте и обходит все проекты, включая недоступные ключу агента.
FluentMigrator здесь не подходит: это данные задач в `TasksDb` по проектам, а не схема.
- Строка 142: `var status = wf.Status(n.Status)?.Slug ?? wf.Initial;`
- Проверить, что сравнение `status == n.Status` на строке 143 ordinal (регистрозависимое): тогда `pending`→`Pending` будет изменением.
- Тест в `NodeIdentityBackfillMigratorTests`: `pending`→`Pending`, `done`→`Done` (остаётся терминальным),
  валидный узел не получает новую ревизию.
**Живая проверка.** Скрипт-сканер: `tasks_search` без `q`, `statusKind:[open,terminalok,terminalcancel]`, по всем
доскам, точное сравнение с FSM доски. Результат — ноль расхождений. Оба узла: новая ревизия и аудит-комментарий
мигратора. `log_query`: `events | where Message has "node-identity-backfill"` за время старта — видно, какие
проекты затронуты.

## Бриф 3 — Р2 (дешёвая форма): исследование → work `chore` + `concern:research`

**Тег.** В quartet оси `area`, `concern`, `lane` открытые (значения не перечислены), `concern:research`
проходит без правки правил. Смысловая оговорка: в doc `concern:*` — ось нефункциональных требований спеки,
а на work её уже используют как тему (`concern:process`, `concern:reliability`). Это приемлемо; отдельная ось —
только если владелец попросит.
**Где текст правила (точные места):**
- `doc/methodology.md:30-32` — «A topic → 0..N tasks» переписать: идея даёт правки спеки, работа выводится из спеки.
- `doc/methodology.md:108-117` (Intake is deferred triage…) и `:119-131` (Triage) — добавить адрес
  «investigation / research → work `chore` tagged `concern:research`; result = doc or memory, linked in the
  verdict; ideas hold proposed spec changes only».
- `doc/methodology.md:278-281` — мёртвая ссылка на `roadmap` (см. ограничение 4).
- `src/clients-ts/petbox-wire/src/templates/petbox-methodology/SKILL.md:103-116` — одна обобщённая строка без
  quartet-имён (`skill-files.ts:5-10` запрещает специфику): «An investigation whose outcome is an answer (a doc
  or memory), not a change to a requirement → a spec-less work item, tagged per this project's tag axes; never
  an idea.»
- Данные quartet: `description` видов `ideas` и `work` (сейчас пусто; guide печатает его под видом,
  `MethodologyGuide.cs:93`). Это и есть доставка правила в момент записи, без кода.
- `src/common/default-agents.json` — проверено: правила маршрута там нет, не трогать. `AGENTS.md` —
  маршрута тоже нет, не трогать.
**Вне объёма:** правило резерва «мимоходная хотелка → `raw`+priority, не спрашивали — не заводи»
(`50-reserve-assessment.md` §1 Р2(2)). Оно не входит в пункт 3 зонтика; добавлять только по слову владельца.
**Живая проверка.** `tasks_methodology_guide` показывает описания видов. Doc и skill на `main`.
После выпуска `npm-wire` `npx petbox-wire@latest` кладёт SKILL.md с новой строкой (проверить версию на
npmjs: `npm view petbox-wire version` растёт).

## Бриф 4 — Р7 (форма резерва): ось тегов `stage`

- Только данные: `rules_get quartet` → в `tagAxes` добавить `{namespace:"stage", description:"Этап по объёму,
  не по календарю: stage:<slug>. Задача входит в этап по тегу. Этап закрывается вердикт-комментарием (версия
  и коммит прозой) на chore «закрыть stage:<slug>»; после этого тег не ставится."}` → upsert.
- `doc/methodology.md:278-281` — фраза про ось вместо ссылки на `roadmap`.
- **Шаблон и инстанс.** `rules_upsert` меняет только инстанс `$system/quartet`. Встроенный пресет
  (`MethodologyPresets.BuiltinAxes` = area, concern, `MethodologyPresets.cs:370-380`) и чужие инстансы не
  затрагиваются. Ось расширяет allowlist, поэтому ни один существующий тег не становится недопустимым
  (`TaskUpsertAssociations.cs:42-45`). Ось действует на все виды инстанса (`TagAxes` объявлены на уровне
  документа). Коллизий `stage` в коде нет (`git grep` по `PetBox.Tasks*`).
- Смежное: `ideas/methodology-tag-axis-flexibility` (raw) не решается и не отменяется.
**Живая проверка.** `rules_get` → `stage` в `tagAxes`. Тег `stage:task-process-2026-09` на зонтик принимается
(первое реальное использование). `tasks_search board:work groupBy:"stage"` отдаёт группу. Guide в строке осей
показывает `stage (…)`.

## Бриф 5 — Р9: норма прозы в `petbox-node-authoring`

- Источник: кит, `src/clients-ts/petbox-wire/src/templates/petbox-node-authoring/SKILL.md` (126 строк, язык
  английский, секции (a)–(e)) и `validate-body.mjs` (110 строк; exit 0/1/2; уровня «предупреждение» нет).
  Раскладывается в проекты через `petbox-wire apply` (`skill-files.ts`, `extraFiles`).
- SKILL.md: новая секция перед (e), например «(f) Lead with why; define every coined term». Норма: для тела
  длиннее ~10 строк первая `##`-секция — `Why` / `Зачем` (язык проекта), 2–4 строки о том, что сломается без
  этой работы. Термин, которого нет в doc или спеке проекта, при первом употреблении получает определение
  одной фразой или ссылку на узел; придуманный в сессии термин определяется или не используется.
- Валидатор: warnings отдельно от violations — печать в stdout, exit не меняется. Правило: >N непустых строк
  (N=10) и первый `##` не совпадает с `/^##\s+(Why|Зачем)\b/i` → предупреждение. Константы
  `ALLOWED_TAGS`/`FORBIDDEN_TAGS` не трогать: их парсит `NodeAuthoringSkillSvgDriftTests.cs`. Первая строка
  `// petbox: managed` — маркер происхождения, оставить.
- Тест: bun-тест в `src/clients-ts/petbox-wire/src/` (рядом с `skill-files.test.ts`) запускает валидатор на
  двух временных файлах. Проверить, что `doctor-skill-drift.test.ts`/`status.test.ts` не хранят хэш
  содержимого шаблона; если хранят — обновить.
- Проверка на термины механической не бывает — только норма в тексте. LLM-судья отвергнут (Р9).
**Живая проверка.** `Verify` зелёный, `npm-wire` выпущен, `npx petbox-wire@latest apply` во временной папке
материализует новый SKILL.md, `node validate-body.mjs` на черновике без «Зачем» → предупреждение, exit 0.

## Вопросы владельцу

Блокирующих нет. Решения по умолчанию, которые исполнитель принимает сам и отмечает в вердикте:
1. Р1: `Description` перехода, а не новое поле артефакта (дешевле, без изменения MCP-схемы).
2. Р2: тег `concern:research`, а не новая ось (ноль изменений правил).
3. Р9: заголовок `## Why` или `## Зачем`, на выбор по языку проекта (кит идёт и в англоязычные проекты).
