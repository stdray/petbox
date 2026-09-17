# 11 — Роли и маршрутизация. Инвентаризация. 2026-09-10
Автор: petbox-worker (Claude Sonnet 5) · источники: код, доска (work/observations), память, замер

## Что здесь верно сейчас

- Пять ролей в `src/common/default-agents.json`: orchestrator (tier `orchestrator`), worker
  (tier `worker`), worker-highstakes (tier `worker`), reserve (tier `reserve`), explore (tier
  `utility`). [код `src/common/default-agents.json:1-88`]
- requiredCapabilities: orchestrator `mcp_main_session`+`spawn_subagents`; worker/worker-
  highstakes — пусто (лист, `spawn.allowed:false`); reserve — `mcp_subagent`; explore — пусто.
  [код, там же]
- `spawn.allowedRoles`: orchestrator → `[worker, explore, reserve, worker-highstakes]`;
  reserve → `[explore]` (единственная); прочие спавнить не могут. [код]
- `escalation.targets`: orchestrator→`[reserve]`; worker и worker-highstakes→`[orchestrator]`;
  reserve и explore — `available:false`, целей нет. [код]
- Кросс-ссылки в прозе: orchestrator называет все четыре роли по слагу + скил
  `petbox-second-reading`; reserve называет `explore` (спавн) и «the orchestrator» (кому
  рекомендует); worker/worker-highstakes называют только orchestrator (эскалация); explore
  никого не называет. [код]
- **Маршрутизация — прошивка исправлена сегодня.** Коммит `3c70196b` (мердж `077a794d`)
  правит п.2 прозы оркестратора: было — «Second file to touch, or several to read? Spawn a
  `worker`. Fast mechanical work — a search, a summary, a one-line edit — is a `worker` too»;
  стало — «A read-only CONCLUSION — locate, map, summarize? Spawn `explore`. Second file to
  touch, or a CHANGE — even one line? Spawn a `worker`». П.1 также снял коллизию имён:
  «harness Explore — not `explore`». [код `git diff 17cbdb83 077a794d -- src/common/default-agents.json`]
- Карточка-источник правки: `explore-role-never-instructed-in-orchestrator-prose`
  (board `work`, статус **Review** — ещё не принята). Замер ДО правки: explore ~41 ход/14
  сессий (плечо Claude, 30д) + 4 вызова (плечо qwen); worker ~637 ходов (Claude). [доска `work`]
- Плечо opencode, 30д (страница wiki `deepseek-glm-qwen-zamery-2026-09-10`): orchestrator
  ~346 вызовов/$0.37 живых денег; worker ~637/~$2.6 квоты; explore ~41/~$0.09 квоты;
  **reserve ~107/~$5.1 квоты — крупнейший потребитель квоты** из пяти ролей, при трафике
  в 6× меньше worker, потому что едет на дорогом `qwen3.8-max` против дешёвого GLM Flash у
  worker; **worker-highstakes — 0 вызовов**. [память/доска, wiki-страница, «вывод 1/2»]
  База qwen-плеча 10.09 (`m-8a8a56ab...`): highstakes 1/30д, reserve 6, explore 4 — там тоже
  редко, но не ноль. [память workspace]

## Где механизм на самом деле живёт

`src/common/default-agents.json:1-88` — единственный источник прозы ролей, едет в ките,
сервер её больше не хранит (`role-notes-name-the-obligation-axis`, board `work`).
`petbox-wire apply` компилирует его в файлы харнессов (`.claude/agents`, `.opencode/agent`,
`.factory/droids`, `~/.qwen/agents/*.md`, `~/.codex/agents/*.toml`) — правка роли есть правка
ОДНОГО файла + релиз кита + `apply` на каждой машине.

## worker-highstakes: определение прозой vs то, что она есть

П.9 её нот: «You are the same worker... only the roster binding differs. No execution rule
changes.» — НЕ вторая проверка, а тот же воркер на другой модели. [код] Критерий КОГДА звать
живёт целиком в прозе ОРКЕСТРАТОРА (п.1): «a wrong result would be expensive OR would pass
unnoticed — acceptance can't re-derive»; сама роль этот критерий не повторяет. Открытая карточка
`highstakes-notes-say-what-not-when` (board `work`, статус **Pending**, `lane:standard`)
называет это ровно тем дефектом, что уже чинился у оркестратора (`f798832a`, 2026-08-27 —
добавлена ось «пройдёт незамеченным»). Решение владельца 2026-09-06: чинить, но **отдельной
сессией**, не вместе с правкой прозы explore — отсюда и живёт незакрытой сегодня. [доска]
Живой эпизод, объясняющий редкость: `m-879a684c...` — 2026-08-27 оркестратор по умолчанию
отправил синтез-документ (без кода) в обычный `worker`, владелец поймал вопросом «уверен, что
это задача для простого воркера?». Роль срабатывает, когда владелец ловит промах оркестратора,
а не когда оркестратор сам применяет критерий надёжно — это ближе к «редко триггерится, потому
что триггер завязан на суждение и default сбит», чем к «мёртвый код».

## reserve: прошивка вызова vs замеренная

Оркестратор п.4: «Reserve triggers on EVENTS, not on feeling stuck» — гибель гипотезы, повтор
проваленного подхода, дорогой откат. Собственные ноты reserve, п.6: «Spawned per named
tripwire — the spawn prompt names it in one line. Never a default route for ordinary work.»
[код] 107 вызовов/30д (плечо opencode) при явном триггере «по событию» не выглядит очевидно
редким (~3.5/день) — но в этих материалах нет базовой линии, с чем сравнивать «редко», и нет
разбивки, сколько из 107 реально несли named tripwire в промпте. [НЕПРОВЕРЕНО]
Смежный сигнал: `ac-24212516343e` (autocaptured) — «когда резерв получает задачу не своей роли
— прервать до записи»: единичное зафиксированное наблюдение, что reserve иногда получает
work-задачи вопреки своим же нотам п.2 «Recommend, don't build» — намёк на дрейф вызова, не
подтверждённая закономерность (recurrence не проверялся).

## Паттерн «прозы говорит ЧТО, не говорит КОГДА» — по остальным ролям

Держится избирательно, не системно: **orchestrator** — обе оси уже дописаны commit `5986d234`
(Done); **worker/worker-highstakes** — та же правка добавила ось «бриф неверен» (было только
«неясен»), и формулировка сразу несёт WHEN («A brief you believe is WRONG... is a stop»);
**explore** — паттерн неприменим структурно, КОГДА её звать решает оркестратор, и сегодняшней
правкой это названо; **reserve** — уже несёт явный WHEN (п.6, «per named tripwire»). **worker-
highstakes — единственная роль из пяти, где разрыв ещё открыт**; сама карточка называет себя
«зеркалом уже починенной дыры». [доска, код]

## Расширяемость набора ролей

Подтверждено: `AgentRole.slug: string`, ни одного union-типа в TS/C#, добавление роли = правка
JSON, кит не тронут. [код, `02-snapshot.md:25`] Два известных открытых дефекта, оба board
`observations`, status `seen`, recurrenceCount 1:
- `new-role-never-gets-local-model-binding-on-existing-installs` — существующий
  `~/.petbox/roles.json` на apply `left as-is`; новая роль получает только warn (exit 0), файл
  артефакта пишется без поля `model`, роль тихо наследует модель сессии. Замерено: 8
  харнесс-слотов на двух машинах, 5/8 без привязки. [доска]
- `apply-orphans-artifacts-of-a-deleted-role` — `cleanupLegacyArtifact` вызывается только
  внутри цикла по ТЕКУЩЕМУ `plan.files`; у исчезнувшей роли записи там нет, значит файл
  `petbox-<role>.md` не посещается никогда. Воспроизведено прямым прогоном (temp-проект,
  marker-файлы, `apply --offline` с ростером без роли → exit 0, файлы целы, 0 упоминаний в
  логе). [доска]
Сиблинг `artifact-integrity-dangling-and-orphans` (board `work`, Done, `85cf0b06`) закрыл
только половину — висячие ЦЕЛИ spawn/escalation (несуществующий slug) теперь отклоняются при
записи; удаление осиротевших ФАЙЛОВ и привязку модели явно оставил вне охвата. Оба дефекта
живы, не устарели.

## Что НЕ проверено

- Разбивка «явный tripwire vs обычная работа» внутри 107 вызовов reserve. [НЕПРОВЕРЕНО]
- Метрики ПОСЛЕ `3c70196b` — коммит от сегодня, постфактум-замера нет, карточка ещё Review.
- recurrenceCount у `ac-24212516343e` не проверялся — единичный факт мог быть шумом.

## Противоречия

Нет: card `explore-role-never-instructed-in-orchestrator-prose` и wiki по opencode независимо
сходятся на одних числах (41 vs 637).

## Что отсюда следует

Маршрутизация «на бумаге» правится точечно и быстро (сегодняшний коммит), но эффект неизмерен —
решать «редирект сработал» рано. worker-highstakes — не мёртвая роль, а роль без собственного
триггера, зависящая от чужого (оркестраторского) суждения, которое уже один раз подвело
владельца лично. Расширяемость набора ролей технически свободна, но эксплуатационно дырява
в двух точках (bind, orphan) — обе воспроизведены, обе вне текущего скоупа починки.
