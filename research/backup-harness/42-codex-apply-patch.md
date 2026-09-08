# apply_patch на DeepSeek/BYOK: решается конфигурацией — ДА

2026-09-08, Sonnet 5, petbox-worker. Клон `codex` (codex-cli 0.153.4, npm-релиз, тот же пин,
что в `24-bench-qwen-codex.md`). Всё в Docker (`node:22-bookworm`, контейнеры `--rm`,
удалены). Проверка живьём подтверждает вывод по коду, не заменяет его.

## 1. Цепочка определения `apply_patch_tool_type`

Поле — часть `ModelInfo` (`protocol/src/openai_models.rs:439`), "Model metadata returned
by the Codex backend `/models` endpoint" (комментарий там же, :398). Разрешение слага модели
в `ModelInfo` — `models-manager/src/manager.rs:651` `construct_model_info_from_candidates`:
`find_model_by_longest_prefix` (:616) ищет среди `candidates: &[ModelInfo]` кандидата, чей
`slug` — префикс запрошенного имени модели; при совпадении берётся ВЕСЬ `ModelInfo`
кандидата (значит и его `apply_patch_tool_type`), слаг переписывается на запрошенный
(:664-667). Промах → `model_info::model_info_from_slug` (`model_info.rs:141-186`) — хардкод
`apply_patch_tool_type: None` (:168) для любого нераспознанного имени. Итог у DeepSeek
`deepseek-chat`: слаг не совпадает ни с одним встроенным кандидатом → фолбэк → `None` →
инструмент не включается. Включение инструмента — `core/src/tools/spec_plan.rs:1255`:
`environment_mode.has_environment() && model_info.apply_patch_tool_type.is_some()`.

`candidates` берутся из `ModelsManager` (`manager.rs:187` `get_model_info`), который —
`OpenAiModelsManager` (тянет `/models` с бэкенда, требует ChatGPT/OpenAI-авторизацию) либо
`StaticModelsManager` (`manager.rs:219`, "authoritative in-process catalog"), между ними
выбирает `ModelProvider::models_manager` (`model-provider/src/provider.rs:445-460`) по
наличию `config_model_catalog: Option<ModelsResponse>` — `Some` → Static, `None` → OpenAi.

## 2. Способ повлиять из конфигурации — ЕСТЬ, это `model_catalog_json`

`config_model_catalog` в `models_manager()` — это `config.model_catalog`
(`core/src/thread_manager.rs:377-386` `build_models_manager`), которое строится в
`core/src/config/mod.rs:3972` из ключа конфига `model_catalog_json` (`config/src/
config_toml.rs:378`: "Optional path to a JSON model catalog (applied on startup only)").
`load_catalog_json` (`core/src/config/mod.rs:2059-2079`) читает файл как `ModelsResponse
{ models: Vec<ModelInfo> }` (`protocol/src/openai_models.rs:768-774`), без сетевого похода.
Путь провайдер-агностичен: работает для ЛЮБОГО `model_provider`, не только для встроенных.
Резерв был прав насчёт `config_toml.rs` в узком смысле (там нет спец-ручки под
`apply_patch`), но пропустил, что там ЕСТЬ более общая ручка — целый кастомный каталог
моделей, которым можно объявить что угодно, включая `apply_patch_tool_type`.

Проверенные альтернативы — не сработали / не найдены: `[model_providers.*]`
(`model-provider-info/src/lib.rs`) не содержит поля `apply_patch`/`model_family`; переменных
окружения с этим смыслом нет (`grep APPLY_PATCH` по `codex-rs` — пусто вне тестов); отдельного
`include_apply_patch`-тумблера нет — единственный переключатель это сам `apply_patch_tool_type`
внутри `ModelInfo`. Требование к записи каталога: нужен `base_instructions` ИЛИ
`model_messages.instructions_template` — иначе `openai_models.rs:857-861` кидает ошибку
парсинга при старте.

## 3. Живая проверка (Docker, мок `/v1/responses` на 127.0.0.1:8842, SSE, тело каждого
POST пишется в файл — приём из `35-bench-overhead.md`/`26-qwen-inject-tui.md`)

`model_provider="deepseek"`, `base_url` → мок, `wire_api="responses"`, `model="deepseek-chat"`,
approval/sandbox — `never`/`danger-full-access`.

- **Контроль** (без `model_catalog_json`): первый запрос — 9 tools, все `function` /
  `namespace` / `web_search` (`exec_command`, `write_stdin`, ...). `apply_patch` ОТСУТСТВУЕТ.
- **Опыт** (`model_catalog_json` → JSON с одной записью `slug:"deepseek-chat",
  apply_patch_tool_type:"freeform"`, оставшиеся поля — минимальный валидный `ModelInfo`):
  первый запрос — 10 tools, добавлен ровно один: `{"type":"custom","name":"apply_patch",
  "description":"The \`apply_patch\` tool can be used to edit files. This is a FREEFORM
  tool..."}`. Разница ровно в один инструмент, всё остальное совпадает.

Вывод: конфигурацией — решается, ЖИВЬЁМ подтверждено, а не только по коду.

## 4. Цена и риск

Цена низкая: один JSON-файл + одна строка `model_catalog_json = "..."` в `config.toml`,
без пересборки. Не нужно выдавать DeepSeek за GPT-семейство — `apply_patch_tool_type`
включается точечно per-slug, без подмены остальных полей модели (context_window,
truncation_policy и т.д. задаются в том же каталоге явно, а не наследуются от чужой модели).
Риск по существу — тот, что назвал владелец: `apply_patch` это FREEFORM custom-tool
(текстовый diff-формат, не JSON-schema function-call); включение не гарантирует, что
DeepSeek-chat реально СЛЕДУЕТ этому формату — задача не проверяла, пишет ли модель патчи
корректно после включения (только что тулза появилась в списке первого запроса), это
отдельный замер поведения модели, а не конфигурации.
