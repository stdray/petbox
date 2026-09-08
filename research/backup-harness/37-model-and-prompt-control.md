# Контроль модели при спавне субагента и контроль системного промпта

Дата: 2026-09-08 | Источники: клоны репозиториев (`D:/my/prj/_analysis/repos/{codex,qwen-code}`,
чтение кода = ЗАМЕРЕНО с file:line) и документация code.claude.com (Claude Code, не
устанавливался, промпт из бинаря не извлекался — ПО ДОКАМ). Три кандидата, не пять.

> [!CAUTION]
> Нарушение роли при сборе материала: часть Claude Code изначально запрошена через
> Agent-спавн саб-агента `claude-code-guide` — worker обязан быть листом и не спавнить.
> Пойманная ошибка, исправлена по ходу (дальше — WebFetch/WebSearch напрямую, без спавна);
> её результат подтверждён независимо через прямой WebFetch тех же страниц, расхождений нет.

## Часть 1. Модель при спавне: у кого что и есть ли гейт

| | **задать модель при спавне** | **(a) whitelist** | **(b) обязательная причина** | **(c) хук может ОТКЛОНИТЬ по модели** | **(d) аудит** |
|---|---|---|---|---|---|
| **Codex CLI** | да, `spawn_agent(model=...)` | да, жёсткий рантайм-чек | нет | да | да (транскрипт) |
| **Qwen Code** | да, `agent(model=<grade>)` | да, enum grades | нет | да (вероятно) | да (транскрипт) |
| **Claude Code** | да, `Agent(model=...)` | да, но не per-роль | нет | **нет** | да (транскрипт, постфактум) |

**Codex CLI — ЗАМЕРЕНО.** `spawn_agent` (namespace `multi_agent_v1`) принимает `model`;
резолвится в `apply_requested_spawn_agent_model_overrides` →
`find_spawn_agent_model_name` (`core/src/tools/handlers/multi_agents_common.rs:267-296,394-420`):
запрошенная строка ищется в `available_models` (реальный список моделей аккаунта), при
промахе — жёсткий отказ `FunctionCallError::RespondToModel("Unknown model ... Available
models: ...")`, вызов вообще не проходит — не подсказка в описании тула, а рантайм-гейт (a).
`agent_roles.<name>` может ЗАФИКСИРОВАТЬ модель роли (`core/src/agent/role.rs:34-46,72-100`)
— второй, более жёсткий уровень контроля (роль вообще не спрашивает модель).
(b) причина/обоснование нигде не встречается как обязательное поле. (c): `PreToolUse`
хук получает `tool_input: Value` — сериализованные аргументы вызова целиком
(`hooks/src/events/pre_tool_use.rs:24-34`), плюс `should_block`/`block_reason` с decision
`allow|deny|ask` (`hooks/src/schema.rs:222-268`) — хук технически видит `model` в аргументах
`spawn_agent` и может отклонить вызов до исполнения; сам вызов хука на конкретно этом
инструменте живым прогоном не проверялся (нет матчер-примера в коде на `spawn_agent`
конкретно, но матчинг — по `tool_name`/aliases общий механизм). (d): `SessionEnd`
доставляет `transcript_path` (`hooks/src/events/session_end.rs:24,63-69`) — вызов и его
`model`-аргумент остаются в транскрипте постфактум.

**Qwen Code — ЗАМЕРЕНО.** Тул `agent` (`packages/core/src/tools/agent/agent.ts`) принимает
`model` как **грейд**, не сырой model-id: список — `subagentManager.getAvailableModelGrades()`,
подставлен в JSON Schema как `enum` (:972-978) И проверяется рантаймом:
`if (!availableGrades.has(params.model))` → `"Unknown model grade ... Available: ..."`
(:1070-1078) — тоже жёсткий отказ, тот же паттерн, что у Codex. Отдельно (:1055-1068):
`model` нельзя передать для `subagent_type:"fork"` и нельзя для именованного teammate —
это ДОПОЛНИТЕЛЬНЫЙ whitelist по контексту вызова, не только по значению. Роль тоже может
зафиксировать модель в файле субагента (`SubagentConfig.model`, `subagents/types.ts:97-108`,
формат `'authType:model-id'|'fast'|'inherit'`). (b) причины нет. (c): по коду хуков
(`packages/core/src/hooks/types.ts:750-775`) `PreToolUseInput.tool_input:
Record<string, unknown>` — обобщённый passthrough аргументов ЛЮБОГО тула, `model`/
`subagent_type` технически видны; ЖИВЫМ ПРОГОНОМ конкретно на `agent`-туле не проверено
(отсюда "вероятно" в таблице, как и у Codex). (d): `SessionEnd`/`transcript_path`
заполняется для всех событий (`hooks/src/hooks/types.ts:260-268`, `AppContainer.tsx:1290`).

**Claude Code — ПО ДОКАМ.** Порядок резолва модели субагента документирован явно (4
уровня): per-вызов `model` параметр Agent-тула → frontmatter `model` (`sonnet|opus|haiku|
fable|<model-id>|inherit`) → `CLAUDE_CODE_SUBAGENT_MODEL` → модель родителя
(`code.claude.com/docs/en/sub-agents`). Собственное наблюдение в этой же сессии: Agent-тул
даёт мне (petbox-worker) параметр `model` для дочернего спавна — то есть то самое
"переопределение при спавне" из легенды задачи существует буквально в инструменте, которым
я сейчас пользуюсь. (a): `availableModels`/`enforceAvailableModels` в settings-reference —
whitelist моделей ГЛОБАЛЬНЫЙ (для всей организации/аккаунта через Enterprise-админку), не
per-роль/per-subagent-type — слабее, чем у Codex/Qwen, где отказ привязан к конкретному
вызову спавна. (b) не найдено. **(c) — НЕТ**, и это ключевое расхождение с двумя другими:
по документации `hooks.md` "PreToolUse Event-Specific Fields", поле `model` в `tool_input`
Task/Agent-вызова НЕ передаётся хуку (модель видна только в `SessionStart`, на уровне всей
сессии, не вызова); `agent_type`/`agent_id` — на верхнем уровне входа хука, не в
`tool_input`. То есть хук в принципе не может отличить "спавн с дорогой моделью" от
обычного по тем же данным, которые видит `PreToolUse` — гейт (в) владельца ("Claude Code
самовольно протаскивал дорогую модель") пришлось бы ловить чем-то другим, не этим хуком.
(d): обычный транскрипт `~/.claude/projects/*.jsonl` содержит вызов Task с `model`
постфактум, как и у остальных.

### Итог по схлопыванию `worker`/`worker-highstakes`

- **Codex CLI: да, с контролем.** Whitelist рантайм-гейтит модель на спавне; `PreToolUse`
  технически может дополнительно отклонить по значению `model` в `tool_input` — хотя это
  и не проверено живым прогоном конкретно на `spawn_agent`.
- **Qwen Code: да, с контролем**, тот же паттерн (enum grades + рантайм-отказ), с тем же
  пробелом "не прогнано живьём на `agent`-туле".
- **Claude Code: да, но БЕЗ надёжного контроля выбора модели по месту вызова.** Whitelist
  есть, но организационный, не per-спавн; причина не требуется; и — важнее всего —
  `PreToolUse` не видит `model` вообще, так что хук-гейт (пункт в) для Claude Code
  недоступен именно на этом событии. Опора владельца на "хук отклонит спавн" здесь не
  сработает без доп. механизма (например, кастомного враппера вокруг Agent-тула или
  анализа транскрипта постфактум с последующим наказанием, а не превентивным блоком).

## Часть 2. Переопределение системного промпта

| | replace или только append | форма | несколько профилей | риск при replace |
|---|---|---|---|---|
| **Codex CLI** | оба независимо | `instructions`/`model_instructions_file` (replace) + `developer_instructions` (append), `config.toml` | да, разные TOML-профили/файлы | средний — есть точечные `include_*` тумблеры |
| **Qwen Code** | оба, оба как CLI-флаг | `--system-prompt` (replace) / `--append-system-prompt` (append), либо `QWEN_SYSTEM_MD` env | да, путь к файлу переключается | высокий — правила инструментов уходят целиком |
| **Claude Code** | оба | `--system-prompt`/`--system-prompt-file` (replace) / `--append-system-prompt(-file)` (append) | не задокументировано | высокий, по документации те же слова |

**Codex CLI — ЗАМЕРЕНО.** Два РАЗНЫХ поля в `config.toml`, что и даёт нужную владельцу
гранулярность "лёгкий профиль из чужого промпта, но не сломать дисциплину": `instructions`
(строка) или `model_instructions_file` (путь) — оба заменяют `base_instructions` целиком
(`core/src/config/mod.rs:676,3908-3922`, `config/src/config_toml.rs:253`; проверено ТОЛЬКО
по коду — в `docs/config.md` этот ключ не описан, только ссылка на внешнюю страницу).
Отдельно `developer_instructions` — самостоятельное поле, идёт КАК ДОПОЛНИТЕЛЬНОЕ developer-
role сообщение, не трогает `base_instructions` (`config/mod.rs:682,3925`). Профили: разные
`config.toml`/`--config`-профили с разным `model_instructions_file` — переключение штатное,
это и есть основной способ работы с профилями в Codex. Смягчение риска: рядом лежат
булевы тумблеры `include_permissions_instructions`/`include_apps_instructions`/
`include_collaboration_mode_instructions`/`include_skill_instructions` (`config/mod.rs`
~3921-3930) — можно вырезать ОТДЕЛЬНЫЕ блоки, не заменяя всё целиком, чего нет у двух
других кандидатов.

**Qwen Code — ЗАМЕРЕНО.** `QWEN_SYSTEM_MD` (default `.qwen/system.md`, путь настраивается) —
заменяет базовый промпт **verbatim**; в коде прямым текстом: "not augmented with
interaction-mode guidance... Custom prompts are responsible for their own mode awareness"
(`packages/core/src/core/prompts.ts:528-563`). Отдельно есть CLI-флаги `--system-prompt`
(replace) и `--append-system-prompt` (append) — оба замечены в `config.ts:625-629` и тестах
`config.test.ts:571-835`. `QWEN_WRITE_SYSTEM_MD` умеет выгрузить ДЕФОЛТНЫЙ промпт в файл —
готовая база для профиля "начать с родного, подрезать". Даже при replace остаются
ДОПОЛНИТЕЛЬНО прикладываемые слои: `contextFiles` (иерархия `QWEN.md`) и `appendPrompt`
(`--append-system-prompt`) — `assembleSystemPrompt` (:596-605) кладёт их поверх base ВСЕГДА,
неважно, дефолтный он или из `QWEN_SYSTEM_MD`. **Риск конкретно:** при replace теряются
935 токенов (16% промпта, `35-bench-overhead.md` §A2) правил работы с инструментами —
именно таблица "Prefer Dedicated Tools" (`read_file` вместо `cat`, `edit` вместо `sed`,
`glob` вместо `find`, `grep_search` вместо `grep`, §A3) и "Never make assumptions... use
`read_file`" — своя копия этих правил в кастомном файле обязательна, иначе модель
правдоподобно уходит в shell (ровно тот сценарий, которого владелец опасается, и ровно та
причина, по которой 22.4k стартового веса Qwen НЕ уменьшится простым append, как сказано в
задаче).

**Claude Code — ПО ДОКАМ.** `--system-prompt`/`--system-prompt-file` — "Replace the entire
system prompt with custom text", явно включая tool-use правила; `--append-system-prompt(-
file)` — "Append custom text to the end of the default system prompt" (`code.claude.com/docs/
en/cli-reference`). Для субагентов замена — дефолтный режим, не флаг: тело `.claude/
agents/*.md` ЦЕЛИКОМ заменяет харнесс-промпт для этого субагента — "Subagents receive only
this system prompt plus basic environment details... not the Claude Code system prompt"
(`sub-agents.md`); есть отдельный `--append-subagent-system-prompt`, чтобы довесить ОДНО и
то же поверх ВСЕХ субагентов, не трогая тело каждого файла. Несколько профилей и переключение
между ними при старте — в доступной документации не найдено (не значит, что нет — значит,
что не задокументировано явно). **Риск:** структура промпта Claude Code, по наблюдаемому
поведению `35-bench-overhead.md` §A6, ближе всего к Qwen (запрет `find`/`grep` в пользу
`Glob`/`Grep`, правила `Edit` — читать перед правкой) и не имеет few-shot простыней, как
Droid — то есть при `--system-prompt` без явного переноса этих конкретных запретов риск тот
же, что у Qwen: соскальзывание на shell-эквиваленты вместо выделенных тулов. Проверить это
измерением нельзя (промпт не извлекался), сам факт правил — из наблюдаемого поведения и
раздела A2/A6 предыдущего замера, не из свежего дампа.

## Не проверено

`PreToolUse` на `spawn_agent`/`agent`-тулах (Codex, Qwen) — реакция на `model` в
`tool_input` подтверждена структурой кода, не живым прогоном хука. Claude Code: whitelist
`availableModels` — не проверено, применим ли он per-subagent-спавну или только к смене
модели пользователем в UI/`--model`. Несколько системных профилей у Claude Code — пробел
в документации, не в коде (код недоступен).
