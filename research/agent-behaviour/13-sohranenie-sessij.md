# 13 — Сохранение сессий: путь транскрипта в PetBox и где он рвётся. 2026-09-10
Автор: petbox-worker · источники: код (`D:\my\prj\petbox`, `D:\my\prj\_analysis\repos\{qwen-code,opencode,codex}`), `~/.codex/hooks.json`, `~/.claude/settings.json`

## Что здесь верно сейчас
- Все 4 харнесса (кроме droid) сходятся в ОДНОЙ реализации инкрементального пуша —
  `pushTranscript` в `append.ts`; порты харнессов только парсят свой формат транскрипта и
  вызывают её. [код `append.ts:248-400`]
- Claude Code: `Stop` → `push-session.ts`, безусловно (нет гейта на очередь ввода в этом файле).
  [код `~/.claude/settings.json:77-82`, `push-session.ts:106-120`]
- qwen: `Stop` **и** `StopFailure` → `qwen-push-session.ts`. `StopFailure` — «when the turn ends
  due to an API error (instead of Stop)». [код `qwen-push-session.ts:1-17`, qwen-code
  `packages/core/src/hooks/types.ts:60-61`]
- **Крест находки — подтверждено на уровне TS-исходника (не той же сборки, что в памяти, см.
  «Противоречия»):** после ответа модели, если нет pendingToolCalls, вызывается
  `takeSteerInput(steerTurnBudget)`. Если очередь ввода НЕ пуста — эта функция возвращает
  `steerInput`, код рекурсивно зовёт `sendMessageStream(..., type: Steer)` и **делает `return
  steeredTurn` до блока, который стреляет `Stop`**. Блок `Stop` (несколько строк ниже) выполняется
  ТОЛЬКО если `takeSteerInput` вернула `undefined`, т.е. очередь ввода пуста. [код qwen-code
  `packages/core/src/core/client.ts:3657-3688` (takeSteerInput/getSteerInput — сама проверка
  очереди), `:4324-4363` (ветка recursion-vs-Stop)]
- **Следствие подтверждено буквально:** чем активнее рулишь qwen-сессию (steer не даёт очереди
  опустеть), тем меньше ходов долетает до `Stop` → тем меньше пушей. Совпадает с замером владельца
  «15 ходов локально, 0 на сервере». [код + память `02-snapshot.md:32`]
- codex: в `~/.codex/hooks.json` подключены РОВНО `SessionStart`, `SessionEnd`, `PreToolUse`
  (матчер `spawn_agent`). `Stop` в файле нет вообще. [код `~/.codex/hooks.json`]
- codex поддерживает `Stop` как событие на уровне рантайма — он есть в перечислении
  `CodexHookEvent` и в `matcher_pattern_for_event`/`normalize_command_hook`, которые
  `codex-hook-trust.ts` воспроизводит из `codex-rs`. Просто НИКТО не положил запись `Stop` в
  `hooks.json` этого кита. [код `codex-hook-trust.ts:44-56,77-89`]
- Доп. находка (не была в задании, но напрямую по теме): codex клэмпит таймаут `SessionEnd`-хука в
  [1,3] секунды НЕЗАВИСИМО от конфига (`normalize_command_hook`) — push, не уложившийся в 3с,
  обрывается процесс-килом codex, а не таймаутом fetch (12с в `FETCH_TIMEOUT_MS`). [код
  `codex-hook-trust.ts:19-21,80-89`, `codex-push-session.ts:25-29` — свой же комментарий кита]
- opencode: пуш живёт не в хуке процесса, а в плагине (`experimental` event-подписке), триггер —
  `event.type === "session.idle"`. Это РЕАЛЬНЫЙ типизированный ивент SDK
  (`Schema.Literal("idle")` → `type: "session.idle"`), не выдумка кита. [код opencode
  `packages/schema/src/session-status-event.ts:11,45`, `opencode-plugin.ts:198-206`]
- `session.idle` публикуется `SessionStatus.set()`, когда статус переходит в `"idle"`; это
  вызывается из `onIdle`-коллбэка `Runner` в `SessionRunState.runner()` — то есть идёт по
  собственной абстракции очереди работы раннера, НЕ по проверке очереди steer-инпута как у qwen.
  [код opencode `session/status.ts:39-48`, `session/run-state.ts:52-69`]
- Долговечность: append-only + 409-самолечение подтверждено буквально — сервер отдаёт
  `{lastOrdinal}`, клиент досылает с `last+1`; если сервер уже впереди локального view — просто
  берёт его ordinal. Опоздавший пуш доезжает ЦЕЛИКОМ, потому что локальный транскрипт
  пересобирается заново из JSONL при КАЖДОЙ попытке (клиент не хранит durable-состояние). [код
  `append.ts:330-343`, `push-session.ts:106-108`]
- НО: самолечение работает только если пуш вообще СЛУЧИЛСЯ. Оно не создаёт попытку само — если
  `Stop` не стрельнул (qwen под рулёжкой) или процесс убит до `SessionEnd` (codex), самолечению
  нечего лечить: попытки не было вовсе.

## Где механизм на самом деле живёт
`append.ts:248` — `pushTranscript`, общий для всех портов.
`qwen-code/packages/core/src/core/client.ts:4324-4352` — recursion-on-steer ветка, обходящая `Stop`.
`qwen-code/packages/core/src/core/client.ts:3657-3688` — `takeSteerInput`, сама проверка очереди.
`~/.codex/hooks.json` — реестр подключённых хуков codex этого кита (३ события, без `Stop`).
`opencode/packages/opencode/src/session/run-state.ts:52-69` — источник `session.idle`.

## Что НЕ проверено
- Точные номера строк из памяти (`chunk-4F7GQGXB.js:91801-91820`, `acpAgent:7213-7245`) — в клоне
  `D:\my\prj\_analysis\repos\qwen-code` такого чанка НЕТ (другой хэш сборки: `acpAgent-DGGLJEX3.js`,
  `chunk-A445U56X.js` и т.п.). Не удалось привязать цитату памяти буквально к файлу — вероятно,
  другая версия/сборка qwen, не обязательно ошибка. [НЕПРОВЕРЕНО]
- Может ли `session.idle` у opencode «съедать» несколько ходов в один ивент, если `Runner`
  очерёдно догоняет несколько queued-промптов прежде чем стать `idle` (аналог qwen-паттерна, но
  через другой механизм) — не дочитано глубже `run-state.ts`, звонки `ensureRunning`/`Runner.make`
  не разобраны. Комментарий самого кита («fires on... the turn finished»,
  `opencode-plugin.ts:10`) может быть неточным именно в этой точке. [НЕПРОВЕРЕНО]
- Есть ли у Claude Code аналог qwen-гейта (недодоставка `Stop` при висящем steer) — Claude Code
  closed-source, здесь верифицирован только сам хук-скрипт (`push-session.ts`), а не движок
  очереди ввода. Вне явного задания — не копал глубже. [НЕПРОВЕРЕНО]

## Противоречия
- Память (`02-snapshot.md:31`) цитирует конкретные строки компилированного чанка qwen; этот клон
  репозитория с ними не совпадает (см. выше). Механизм подтверждён на уровне TS-исходника того же
  репозитория, но НЕ на уровне той же сборки/версии, что цитирует память. Не разрешено — не
  выбираю победителя, обе стороны могут быть верны для разных версий qwen.

## Что отсюда следует
Экспозиция «работал — не долетело» разная по размеру: у qwen она растягивается на ВЕСЬ активно
рулимый прогон (наблюдаемый худший случай — 15/0); у codex это ВЕСЬ прогон до `SessionEnd`
(+ретёбл-риск обрыва 3с-клэмпом даже на чистом выходе); у Claude Code и opencode — по имеющимся
данным заявление источника, что экспозиция ограничена одним ходом/одним idle-переходом,
подтверждено лишь частично (opencode) и не оспорено, но и не добито до конца (Claude Code). Это
отдельная дыра от droid HTTP 400 (там пуш ДОШЁЛ и был отвергнут веб-слоем) — здесь пуш просто
НЕ СЛУЧАЕТСЯ на уровне триггера, слой другой, конфликта находок нет.
