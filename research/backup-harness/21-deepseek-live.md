# DeepSeek Harness (dsh) v0.1.3-alpha.2 — живой прогон

Дата: 2026-09-08 | Исполнитель: Claude Sonnet 5, petbox-worker | Среда: Docker `node:22`
(контейнер `dsh-live`, удалён после прогона), `npm i -g @deepseek-ai/dsh@0.1.3-alpha.2`
(538 пакетов, чисто). Проверяет и ПЕРЕСМАТРИВАЕТ `15-cand-deepseek.md` (тот файл был
целиком ПО ДОКАМ). Ключи провайдера НЕ использовались: либо не заданы, либо фиктивный
`DEEPSEEK_API_KEY=dummy-fake-key-123`, либо локальный mock-сервер на 8842 внутри контейнера
(без сети наружу).

## Вопрос 1 — интерфейс (решающий вопрос владельца)

ЗАМЕРЕНО (`dsh --help`): команды — `dsh --profile <name>`, `dsh web`, `dsh plugin`.
Профили грузятся из `$DSH_HOME/profiles/<name>`, штатно установлены: `web`, `headless`,
`acp`, `sdk`, `sdk-minimal`. Профиль `tui` из примера в `--help` **не существует из
коробки**: `dsh --profile tui --help` → `Error: dsh: profile "tui" does not exist;
create it with 'dsh plugin --profile tui add <package>'`. Полный обзор пакетов
(`ls node_modules/@deepseek-ai`, 200+ пакетов) не содержит ни одного TUI-фронтенда
(ink/blessed/readline-REPL) — `dsh-terminal`/`dsh-terminal-bash`/`dsh-tmux-context` это
ИНСТРУМЕНТЫ ДЛЯ МОДЕЛИ (persistent shell-сессии как tool), а не интерфейс пользователя.
`dsh-acp-app` — стдио-протокол Agent Client Protocol для редакторов (Zed и т.п.),
`--help` "writes help and exits without claiming stdin or stdout" — тоже не терминальный
чат для человека.

`headless` РЕАЛЬНО существует и работает без сервера и без браузера:
`dsh --profile headless "say hi"` печатает финальный ответ в stdout, стримит reasoning
в stderr, код возврата 0/1. ЗАМЕРЕНО: полный цикл (SessionStart → LLM-запрос → финальный
ответ → выход) — раздел "Хуки" ниже. НО: `dsh-headless`, по докам ("Known Limitations"),
даёт ровно один таск за запуск процесса, **без интерактивного продолжения** — это
скриптовый режим, не REPL.

Вывод: заявление владельца "слышал, что только web" **неточно, но близко к сути** —
из коробки есть третий, скриптовый headless-режим (годится для CI/автоматизации), но
**интерактивного терминального агента (TUI/REPL) в комплекте нет**; чтобы получить его,
нужно самому написать/подключить `tui`-профиль — готового пакета для этого в дереве нет.

## Вопрос 2 — хуки

ЗАМЕРЕНО живьём, полный цикл: собран `hooks.json` (SessionStart пишет входной payload
в файл и возвращает `{"hookSpecificOutput":{"additionalContext":"PETBOX-CANARY-4471"}}`;
Stop пишет payload в файл), подключён через patch `insert: [{id, name:
'@deepseek-ai/dsh-hooks-claude-code', config:{configPath, projectDir}}]`
(команда `dsh --profile headless --patch <file> --dump-config` подтвердила композицию).
Локальный mock LLM-сервер (Node http, SSE-формат `data: {...}\n\n` + `data: [DONE]`)
поднят на 8842 внутри контейнера, `DEEPSEEK_BASE_URL=http://localhost:8842`.

- SessionStart сработал: payload на диске —
  `{"session_id":"...","transcript_path":"","cwd":"/tmp/dshtest","hook_event_name":"SessionStart","source":"startup"}`.
- `additionalContext` ДОШЁЛ до реального LLM-запроса: в захваченном теле запроса
  (`last-request-*.json`) строка `"content":"PETBOX-CANARY-4471"` присутствует как
  отдельное `user`-сообщение.
- **Находка (гонка, не баг по докам, но реальный эффект):** SessionStart-хук в докаx
  явно назван "detached — no extension point awaits them". ЗАМЕРЕНО: канарейка
  НЕ попала в ПЕРВЫЙ ход (request #2, без канарейки, agent уже ответил
  "MOCK-REPLY-OK title"), а появилась только в ВТОРОМ ходе (request #4) — как отдельное
  user-сообщение ПОСЛЕ ассистентского ответа, потому что мой командный хук (`cat` +
  `echo`) не успел отработать до отправки первого запроса. Для инжекта канона это риск:
  порядок "канон виден с первого сообщения" НЕ гарантирован, если хук хоть немного
  медленный — эмулируется как control test, не единичная случайность.
- Stop сработал (payload есть), но **`transcript_path` пуст всегда** — ЗАМЕРЕНО дословно
  как в докаx: `"transcript_path":""`. Значит "выгрузка транскрипта на Stop" через этот
  бридж **не реализована**: путь к артефакту хук не получает никогда, только сам факт
  события.
- Patch-формат хуков — не в докаx кандидата: голый список `- name: ...` даёт
  `patch: id is required for non-insert patches`; рабочий синтаксис — `- insert: [{id,
  name, config}]` (нашёл через пример `dsh-headless/cordis.patch.yml`).

## Вопрос 3 — MCP

Опровергает `15-cand-deepseek.md` (там: "MCP-пакета в дереве НЕТ"). ЗАМЕРЕНО:
пакет `@deepseek-ai/dsh-mcp-client` присутствует по факту установки (`node_modules/
@deepseek-ai/dsh-mcp-client/package.json`, description: "MCP client bridge: connects to
MCP servers and registers their tools on ctx.tools"), просто лежит НЕ под `packages/mcp/`
именем, которое искал прежний файл через `packages/README.md`. Живой прогон: поднял
собственный минимальный stdio MCP-сервер (`mini-mcp-server.mjs`, ~25 строк, JSON-RPC
`initialize`/`tools/list`/`tools/call`, один инструмент `echo`), подключил patch'ем
(`transport: stdio, command: node, args:[...]`). `--dump-config` подтвердил композицию
без ошибок; живой запуск headless-таска с этим patch'ем — в захваченном теле реального
LLM-запроса присутствует `mcp__petboxcanary__echo` в списке `tools`. Именование
`mcp__<serverName>__<tool>` — то же, что у Claude Code (по докам пакета, и совпадает
с ЗАМЕРЕННЫМ именем инструмента). Транспорт stdio и streamable-http оба заявлены;
живьём проверен только stdio.

## Вопрос 4 — messaging/interrupt к субагенту

ЗАМЕРЕНО частично: подключать `dsh-subagent`/`-spawn-in-process`/`-fork-in-process`/
`dsh-tool-subagent`/`dsh-tool-subagent-control` НЕ требуется руками — они уже в
СТАНДАРТНОЙ композиции headless-профиля (`--dump-config` без всякого патча уже
показывает `tool-subagent-control`, `tool-subagent`, `tool-subagent-fork`). В реальном
теле LLM-запроса (headless, дефолтный профиль, без патчей) присутствуют инструменты
`send_message`, `interrupt_agent`, `list_agents`, `subagent`, `subagent_fork` — то есть
модель реально ВИДИТ эти tool-схемы по умолчанию. Механику "родитель шлёт сообщение уже
работающему живому ребёнку и это доходит ДО завершения хода" — то есть саму сцену
"воркер встрял, досылка доходит" — живьём НЕ прогнал: это требует реального
многоходового вызова инструментов (spawn → send_message → interrupt) со скриптованными
tool_calls в mock LLM, что не уложилось в отведённый бюджет задачи. Механика по докам
(`packages/subagent`, `dsh-tool-subagent-control/README.md`, читан живьём из
установленного пакета) описана детально: `send_message` доставляется через `Steer` на
ближайшей границе шага, `interrupt_agent` останавливает только текущий ход ребёнка,
очередь и потомки не трогает. Это ПО ДОКАМ, не ЗАМЕРЕНО для самой доставки/прерывания.

## Вопрос 5 — стабильность

За ~15 запусков `dsh` в контейнере (help, dump-config, headless×8, patch-ошибки) —
**ни одного зависания, ни одного сегфолта**. Каждая ошибка — один читаемый ярлык и
ненулевой exit:
- `dsh: MISSING_CREDENTIAL: llm-deepseek: no API key for provider route
  "deepseek-official"; ...` (нет ключа вообще — ошибка ХАРНЕССА).
- `dsh: AUTH: Authentication Fails, Your api key: ****-123 is invalid` (фиктивный ключ
  против настоящего `api.deepseek.com` — ошибка ПРОВАЙДЕРА, дошла реальная сеть).
- `dsh: STREAM_CLOSED: SSE stream ended without [DONE]` (мой mock ответил plain JSON
  вместо SSE — сразу понятная причина, не путаница).
- `dsh: plugin tree failed to load: ... duplicate loader entry id: tool-subagent` (я
  повторно объявил уже встроенный id) — явная причина, не тихий сбой.
- `dsh: [patch.yml] patch: id is required for non-insert patches` — синтаксис патча
  не задокументирован в файле-кандидате, но сообщение об ошибке само подсказывает путь.

Вывод: в этом узком прогоне (headless CLI, хуки, MCP, patch-композиция, без реального
провайдера, без web/TUI, без реального многоходового tool-calling) харнесс вёл себя
ПРЕДСКАЗУЕМО, а не "крайне нестабильно". Это НЕ опровергает слухи о нестабильности web UI
или длинных агентных сессий с реальной моделью — те не проверялись.

## Идеи, достойные переноса (сверх уже отмеченных в 15-cand-deepseek.md)

- **Patch = `insert` + `id`, replace = голый `id`+`config`.** Простой, декларативный
  формат наложения конфигурации поверх базового профиля — годная модель для того, как
  MCP-сервер PetBox мог бы поставлять "патч-оверлей" вместо целого харнесса.
- **Ошибки харнесса ВСЕГДА одна строка `dsh: <CODE>: <message>` на stderr, exit 1.**
  Разделение "код ошибки виден программно" + "человекочитаемый текст" — упрощает наши же
  скрипты-обвязки, которые должны отличать сбой харнесса от сбоя провайдера.
- **MCP-клиент — не "функция ядра", а такой же patch-плагин, как и всё остальное**,
  с `reconnect` (экспоненциальный бэкофф, потолок попыток) и явным различением "сервер
  не поднялся при старте" (`failOnStartupError`) от "сервер отвалился в процессе"
  (старые инструменты остаются видны, но падают до реконнекта) — хорошая модель
  отказоустойчивости для нашего собственного MCP-подключения к другим харнессам.

## Чего не проверил и почему

- Web UI и TUI-гигиену (home/end) — нет живого браузера/desktop-обвязки в Docker без
  дисплея, и `tui`-профиля не существует из коробки (см. Вопрос 1).
- Реальную сцену "интерпрет работающего ребёнка" (spawn → занят → send_message/interrupt
  доходит ДО завершения хода) — нужен скриптованный многоходовой tool-calling mock;
  не уложилось в бюджет строк/времени этой задачи. Инструменты подтверждены
  присутствующими в реальном запросе к модели (Вопрос 4), сама доставка — ПО ДОКАМ.
- BYOK к Anthropic/OpenAI через `llm-pi-ai` — пакет есть в дереве, конкретный список
  поддерживаемых провайдеров не читал (экономил бюджет на приоритетные 5 вопросов).
