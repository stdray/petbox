# Кандидаты: Codex CLI (OpenAI) и Qwen Code (QwenLM)

Дата: 2026-09-08 | Исполнитель: Sonnet 5, petbox-worker | Источники: клоны репозиториев,
чтение кода. Клоны: `D:/my/prj/_analysis/repos/codex` (origin openai/codex, shallow depth 200,
HEAD d648947, 2026-09-08), `D:/my/prj/_analysis/repos/qwen-code` (origin QwenLM/qwen-code,
shallow depth 200, HEAD cffc404, 2026-09-08). Ничего не устанавливалось, не запускалось —
только чтение исходников (= ЗАМЕРЕНО в терминах легенды, с указанием file:line).

## Codex CLI

Вердикт: **годится** — BYOK доказан в коде как приоритетный путь, hook-инжект и транскрипт
на SessionEnd реальны, MCP и роли есть; делегирование неблокирующее.

| ось | оценка | обоснование | статус |
|---|---|---|---|
| Раздаваемость | да | `load_auth()`: "API key via env var takes precedence over any other auth method" — читает `CODEX_API_KEY`, ветка ChatGPT даже не проверяется первой | ЗАМЕРЕНО `login/src/auth/manager.rs:1472-1477` |
| Раздаваемость (custom provider) | да | `ModelProviderInfo.requires_openai_auth: bool`, default `false`; `should_show_login_screen`: `if !requires_openai_auth { return false }` — комментарий в коде: "For OSS/other providers, skip login entirely" | ЗАМЕРЕНО `model-provider-info/src/lib.rs:140-145`, `tui/src/lib.rs:2070-2078` |
| Автономность от подписки | да, для ApiKey-режима | `AuthMode::ApiKey` — независимая ветка от ChatGPT-токенов, отдельное хранение | ЗАМЕРЕНО `login/src/auth/manager.rs:80-90,300-316` |
| BYOK-охват | широкий | произвольный provider через `base_url`+`env_key` (своя env-переменная на провайдера), не только OPENAI_API_KEY; `codex login --api-key` пишет ApiKey-режим в auth.json | ЗАМЕРЕНО `model-provider-info/src/lib.rs:97-152`, `cli/src/login.rs:204-230` |
| Обвязка PetBox — SessionStart(inject) | да | вход: session_id, transcript_path, cwd, model, permission_mode, source(startup/resume/clear/compact); stdout (plain или `hookSpecificOutput.additionalContext`) идёт в `additional_contexts_for_model` → в контекст модели; тест `plain_stdout_becomes_model_context` подтверждает | ЗАМЕРЕНО `hooks/src/events/session_start.rs:42-50,262-309,371-400` |
| Обвязка PetBox — SessionEnd(транскрипт) | да, но `reason` всегда `"other"` | вход: session_id, transcript_path, cwd, reason; `reason` — константа `SESSION_END_REASON = "other"`, не различает типы завершения | ЗАМЕРЕНО `hooks/src/events/session_end.rs:24,63-69` |
| MCP | stdio + StreamableHttp, конфиг `[mcp_servers.<name>]` в `config.toml`, per-server timeouts/allow-deny-list тулов, OAuth | ЗАМЕРЕНО `config/src/mcp_types.rs:196-268,533-565`, `config/src/config_toml.rs:276-277` |
| Роли/модели | частично | `agent_roles.<name>` может переопределить `model`, `model_reasoning_effort`, и ВЫКЛЮЧИТЬ фичи (ShellTool/Apps/Plugins/MemoryTool/RequestPermissionsTool) — только вычитание из родительского набора, не произвольный набор тулов | ЗАМЕРЕНО `core/src/agent/role.rs:34-46,72-100`, `core/src/config/mod.rs:900` |
| Делегирование | неблокирующее | `spawn_agent` возвращает id сразу; ожидание — отдельный `wait_agent`; `default_subagent_model`/per-spawn `model` override | ЗАМЕРЕНО `core/src/tools/handlers/multi_agents_spec.rs:63-140,264-290`, `config/src/config_toml.rs:693-696` |
| Досылка | да | `send_input(target, message, interrupt)`: `interrupt=false` ставит в очередь без прерывания текущей задачи, `interrupt=true` — немедленно; `followup_task`: "deliver ... promptly at message boundaries ... or after the pending tool call completes"; `wait_agent` v2 досрочно завершается при "new user input... steered into the active turn" | ЗАМЕРЕНО `core/src/tools/handlers/multi_agents_spec.rs:168-232,283` |
| Лицензия | Apache-2.0 | файл `LICENSE` в корне | ЗАМЕРЕНО (открыт файл) |
| Живость | HEAD день в день с "сегодня" (2026-09-08), 200 коммитов покрывают только ~5.5 календарных дня (`git log --oneline`, `git log --format=%ci`) | ЗАМЕРЕНО |
| Windows | отдельный workspace-крейт `windows-sandbox-service` (sandboxing на Windows как первоклассная фича, не заглушка) | ЗАМЕРЕНО `Cargo.toml:108`, `ls windows-sandbox-service` |

Боли владельца: home/end — не проверено (TUI ratatui-код не читал, вне бюджета). Блокировка
субагентом — снята архитектурно (spawn асинхронный). Поле ввода у субагента — есть
эквивалент (`send_input`/`send_message`/`followup_task`), причём есть режим немедленного
прерывания, чего нет даже в легенде-требовании владельца буквально. Застрявший воркер —
`wait_agent` не блокирует навечно (таймаут + прерывание по новому вводу), но именно
"воркер завис — почему бы не отреагировать на новый ввод" не проверялось на живом прогоне.

Стоимость перехода: хуки почти 1:1 с Claude Code по контракту (stdin JSON, stdout
additionalContext, exit-code семантика) — адаптер, не переписывание. MCP-конфиг — другой
файл (`config.toml` вместо `.mcp.json`), формат transport-полей отличается, нужен конвертер.

## Qwen Code

Вердикт: **годится** — код прямо документирует смерть OAuth-тира вендора и BYOK как
единственный практический путь; hook- и multi-agent система заявлена как порт Claude Code
"verbatim" и это подтверждается в коде, а не только в комментарии.

| ось | оценка | обоснование | статус |
|---|---|---|---|
| Раздаваемость | да, и это единственный путь | `AuthType.QWEN_OAUTH` ветка `validateAuthMethod`: "Qwen OAuth free tier was discontinued on 2026-04-15. Block new OAuth setups" — вендорский логин НЕ работает для новых пользователей; `AuthType.USE_OPENAI` проверяет только наличие ключа (env var или `settings.security.auth.apiKey`), без всякой связи с Alibaba-аккаунтом | ЗАМЕРЕНО `packages/cli/src/config/auth.ts:269-301` |
| BYOK-охват | да, включая свой base_url | `modelProviders[]` задаёт произвольный `baseUrl` + произвольное имя env-переменной (`envKey`) на провайдера; готовые presets: `custom-provider`, `deepseek`, `openrouter`, `moonshot`, `grok`, `minimax`, `modelscope` и т.д. — локальный llama-server и любой OpenAI-совместимый эндпоинт подключается тем же механизмом | ЗАМЕРЕНО `packages/core/src/utils/auth-type.ts:8-14`, `packages/core/src/providers/presets/*.ts`, `packages/cli/src/config/auth.ts:97-195` |
| Обвязка PetBox — SessionStart(inject) | да, реально применяется к модели, не только к логу | `fireSessionStartHook()` возвращает `additionalContext`, дальше `chat.applySessionStartContext(text, source)` вызывается перед первым сообщением | ЗАМЕРЕНО `packages/core/src/core/client.ts:2169-2203,2327-2343` |
| Обвязка PetBox — SessionEnd(транскрипт) | да | базовый `HookInput.transcript_path` заполняется из `config.getTranscriptPath()` для ВСЕХ событий, включая `SessionEnd`; реально вызывается на `/clear`, на выходе из промпта (`PromptInputExit`), в ACP-интеграции | ЗАМЕРЕНО `packages/core/src/hooks/types.ts:260-268`, `packages/core/src/hooks/hookEventHandler.ts:934-942`, вызовы в `AppContainer.tsx:1290`, `clearCommand.ts:67`, `acpAgent.ts:2951` |
| MCP | stdio / sse / streamable-http (+ websocket-поле `tcp`, отдельный кейс), конфиг в `.qwen/settings.json` и/или проектном `.mcp.json`; project/workspace-scope МСР-серверы гейтятся approval'ом (untrusted), user/system — нет | ЗАМЕРЕНО `packages/core/src/config/mcp-server-config.ts:24-49,106-161` |
| Роли/модели | да, шире чем у Codex | `SubagentConfig.model?: string` принимает `'authType:model-id'` — субагент может ходить через ДРУГОЙ authType/провайдера, не только другую модель того же провайдера; `tools?: string[]` allowlist + `disallowedTools` — независимый набор тулов на агента | ЗАМЕРЕНО `packages/core/src/subagents/types.ts:51-108` |
| Совместимость агентов с Claude Code | буквальная | заголовок файла: "Mirrors Claude Code 2.1.168's `.claude/agents/<name>.md` schema verbatim so a user can drop a Claude Code agent file into `.qwen/agents/`" + маппинг `permissionMode`→`approvalMode` | ЗАМЕРЕНО `packages/core/src/subagents/agent-frontmatter-schema.ts:7-66` |
| Делегирование | неблокирующее по умолчанию | "Top-level regular subagents run in the background by default and report their results through a completion notification"; `run_in_background: false` — явный запрос на инлайн-блокировку | ЗАМЕРЕНО `packages/core/src/tools/agent/agent.ts:856-873` |
| Досылка | да, с семантикой по состоянию агента | `send_message`→`task_id`: "Running agents receive the message at the next tool-round boundary; paused agents resume with it as their first continuation instruction; completed agents continue on retained transcript" | ЗАМЕРЕНО `packages/core/src/tools/agent/agent.ts:873` |
| Лицензия | Apache-2.0 | файл `LICENSE` в корне | ЗАМЕРЕНО (открыт файл) |
| Живость | релиз v0.23.0 от 2026-09-03 (5 дней до "сегодня"), HEAD день в день; 200 коммитов покрывают ~6 календарных дней | ЗАМЕРЕНО `package.json:5`, `CHANGELOG.md:14`, `git log` |
| Windows | 189 файлов с явной веткой `process.platform === 'win32'` в `packages/cli`+`packages/core`; в `package.json` нет `os`-ограничения | ЗАМЕРЕНО (`grep -rl` подсчёт) |

Боли владельца: home/end — не проверено (не читал TUI input-компонент, вне бюджета).
Блокировка субагентом — снята архитектурно, аналогично Codex. Поле ввода у субагента —
есть (`send_message`+`task_id`), причём явно расписана семантика для running/paused/
completed агента — детальнее, чем у Codex. Застрявший воркер — механизм тот же class
notification+resume, отдельного "если агент завис — уведомление всё равно придёт"
гарантии в коде не нашёл; не проверено на живом прогоне.

Стоимость перехода: хуки — тот же контракт полей (`session_id`, `transcript_path`, `cwd`,
`hook_event_name`), тот же `additionalContext`-канал — адаптер форматов, не с нуля.
Multi-agent/roles-система ближе к нашей схеме "оркестратор-воркеры", чем у Codex
(отдельный authType на роль, а не только модель).

## Чего не проверил и почему

Ни один агент не устанавливался и не запускался (граница задачи — только Docker при
крайней нужде, не понадобилось). Не проверено: TUI-гигиена (home/end) — не читал
компонент ввода ни у одного кандидата, отдельная задача под свой бюджет строк. Полный
формат `.mcp.json`/`config.toml` на предмет скрытых ограничений по числу серверов — не
искал явных лимитов, но и не нашёл упоминаний лимита ни в одном из кандидатов. Точный
процент "воркер завис — сообщение всё равно доставится" — только по коду (комментарии
и сигнатуры инструментов), не по живому прогону с зависшим тестом, как в легенде owner'а.
Реальный exit-код и поведение при отсутствии интерактивного логина на чистой машине —
не прогонялось (только Docker дал бы это, не потребовалось — код однозначен по обоим
кандидатам). Codex: не проверял, действительно ли `codex exec` (headless) идёт по тому же
`load_auth()`, что и TUI — код общий (`login` crate используется и `cli`, и `tui`), поэтому
уверенность высокая, но отдельного грепа per-binary не делал.
