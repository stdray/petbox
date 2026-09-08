# Qwen Code 0.23.0: почему SessionEnd не срабатывает в headless

Дата: 2026-09-08 | Клон: `D:/my/prj/_analysis/repos/qwen-code` @ `cffc40495a34` (0.23.0).
Замер по коду (grep + чтение), живой Docker-прогон не потребовался — картина однозначная
без него.

## 1. Диагноз: дизайн-пробел, не баг и не гонка

`fireSessionEndEvent` (`packages/core/src/hooks/hookEventHandler.ts:322`) имеет ровно
4 продакшн-вызова во всём репо (grep `fireSessionEndEvent(` минус `*.test.ts`):
`AppContainer.tsx:1290`, `start-opentui-ui.tsx:410` (оба TUI, `PromptInputExit`),
`clearCommand.ts:67` (`/clear`), `acpAgent.ts:2951` (ACP-сессия). Headless-путь
(`packages/cli/src/nonInteractiveCli.ts`, `runNonInteractive`) вызова **не содержит вовсе** —
grep `SessionEnd|fireSession|hookSystem` по файлу пуст. Не «вызывается, но подавляется»
и не «не успевает»: событие для этого пути просто нигде не создаётся в исходнике.

Выход из headless явный и синхронный: `llm.tsx:1487-1495` — `runNonInteractive()` →
`finally { await runExitCleanup() }` → `process.exit(exitCode)`. `runExitCleanup`
(`utils/cleanup.ts:68-92`) — общий реестр `registerCleanup()`-колбэков (MCP-дисконнект,
телеметрия), к хукам отношения не имеет; гонки на выходе нет, потому что вызывать нечего.

## 2. Транскрипт: гарантированно полон к моменту выхода — не гонка

`settleBeforeTerminalOutput()` (`nonInteractiveCli.ts:544-548`) явно awaited перед финальным
`emitResult` (тот же файл, `:551-560`) и перед `routeAbort`/budget-веткой (`:646`, `:854`).
Внутри — `settleChatRecording(config, {finalize})` → `chat-recording-failure.ts:66-88`:
`recorder.finalize()` + `await recorder.flush()` с потолком `CHAT_RECORDING_SETTLE_TIMEOUT_MS
= 2000` мс (`:25`). Путь — `getTranscriptPath()`, `config.ts:9003-9011`; пустая строка при
`chatRecording:false` (см. 25-й файл, D5-грабли повторяются один в один).

## 3. Симметрии нет: SessionStart и Stop — общий core-путь, headless их видит

`fireSessionStartEvent` вызывается из `packages/core/src/core/client.ts:2184/2191` —
это ядро (`Config`/клиент), а не UI, и отрабатывает в обоих режимах. То же для
`fireStopEvent` — `packages/core/src/config/config.ts:3298`, внутри общей
turn-completion-логики. Это и есть штатный обход: **`Stop` — единственное headless-safe
событие, реально симметричное «концу сессии» для одноразового `-p`-вызова** (один прогон =
один turn = один `Stop`), в отличие от `SessionEnd`, который живёт только в UI-слое.

## 4. Практический вывод

Чинить у них — не стоит: это не однострочный баг, а отсутствующий вызов на целой ветке
(headless), симметрично добавлять пришлось бы в `nonInteractiveCli.ts`/`llm.tsx` — не наш
PR по цене для D1. Обход — **на нашей стороне, wrapper, не хук**: `transcript_path`
детерминирован из `session_id` (`<projectDir>/chats/<sessionId>.jsonl`, тот же
`getTranscriptPath()`), и headless-инвокатор уже знает `session_id` до вызова — читать файл
сразу после того как процесс `qwen -p` завершился (exit получен ⇒ по п.2 транскрипт уже
`flush()`-нут). Хук `Stop` как алтернатива не нужен: он не даёт ничего, что не даёт уже
детерминированный путь + факт завершения процесса, а добавляет лишний хук-конфиг.
