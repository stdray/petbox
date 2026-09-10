# Контекст и вывод: проверка двух утверждений (droid, codex)

**Вердикт: оба утверждения ПОДТВЕРЖДЕНЫ.** (а) droid не декларирует размер окна;
(б) codex не имеет ключа для максимума вывода.

| харнесс | контекст: ключ и область | вывод: ключ и область | при отсутствии |
|---|---|---|---|
| **droid** | ключа объявления окна НЕТ. Ближайшее — порог компакции: `compactionTokenLimit` (глобально), `compactionTokenLimitPerModel` (на модель) | `customModels[].maxOutputTokens` — на модель | порог компакции: «Model-dependent» default; при неизвестной BYOK-модели значение выводит сам харнесс |
| **codex** | `model_context_window` — глобально/на профиль, для *активной* модели; в каталоге `context_window`, `max_context_window` — на модель | ключа НЕТ (ни в конфиге, ни в каталоге) | `model_context_window`: «default: auto for model»; `model_auto_compact_token_limit` «unset uses model defaults» |
| **qwen** | `generationConfig.contextWindowSize` — на модель, внутри `modelProviders[authType][]` | `generationConfig.samplingParams.max_tokens` — на модель | окно: встроенные дефолты по match имени модели + константный fallback; `samplingParams` атомарен — незаданные поля становятся `undefined` |
| **opencode** | `provider.<p>.models.<m>.limit.context` — на модель | `provider.<p>.models.<m>.limit.output` — на модель | у стандартных провайдеров тянется из models.dev; у кастомных надо задавать руками |

## droid — детали

Просмотрены ЦЕЛИКОМ: `https://docs.factory.ai/cli/byok/overview`,
`https://docs.factory.ai/cli/byok/openai-anthropic`, `https://docs.factory.ai/droid-cli/settings`
(= `https://docs.factory.ai/cli/configuration/mixed-models`, тот же контент).

Полный перечень полей `customModels[]` — 13 ключей: `model`, `displayName`, `baseUrl`, `apiKey`,
`apiKeyHelper`, `apiKeyHelperTtlMs`, `authMode`, `provider`, `maxOutputTokens`, `noImageSupport`,
`extraArgs`, `extraHeaders`, `bedrock` (+ 6 подключей bedrock). Перечень полный — это раздел
«Supported fields».

- `maxOutputTokens`: «Maximum output tokens for model responses», область — `customModels[]`, на модель [дока https://docs.factory.ai/cli/byok/overview]
- `compactionTokenLimit`: «Token threshold that triggers automatic compaction of the current session», глобально, default «Model-dependent» [дока https://docs.factory.ai/droid-cli/settings]
- `compactionTokenLimitPerModel`: «Per-model overrides for `compactionTokenLimit`, as a `{ "<modelId>": number }` map», default `{}` [дока https://docs.factory.ai/droid-cli/settings]
- `compactionModel`: «Which model performs compaction: `same` uses the current session model, or specify a model ID» [дока https://docs.factory.ai/droid-cli/settings]
- Объявление размера окна модели: **[НЕ НАЙДЕНО, просмотрено: три страницы выше целиком — таблица «Supported fields» BYOK и полная таблица «Available settings» (~78 ключей, включая раздел «Context and compaction»); ключей `contextWindow`/`maxTokens`/`maxInputTokens` нет]**

## codex — детали

Просмотрены ЦЕЛИКОМ: `https://learn.chatgpt.com/docs/config-file/config-reference`,
`.../config-sample`, `.../config-advanced` (22 раздела, схемы каталога там нет);
плюс исходники: `codex-rs/models-manager/models.json`, `codex-rs/core/config.schema.json`.

- `model_context_window`: «Context window tokens available to the active model»; sample: `# model_context_window = 128000 # tokens; default: auto for model` [дока https://learn.chatgpt.com/docs/config-file/config-reference, .../config-sample]
- `model_auto_compact_token_limit`: «Token threshold that triggers automatic history compaction (unset uses model defaults)» [дока .../config-reference]
- `model_auto_compact_token_limit_scope`: «Controls whether the auto-compaction threshold counts the full active context (`total`, the default) or only growth after the carried compaction-window prefix (`body_after_prefix`)» [дока .../config-reference]
- `tool_output_token_limit`: «Token budget for storing individual tool/function outputs in history» — это бюджет ХРАНЕНИЯ вывода инструментов в истории, не потолок генерации модели [дока .../config-reference]
- `model_catalog_json`: «Optional path to a JSON model catalog loaded on startup. A selected `$CODEX_HOME/profile-name.config.toml` profile file can override this per profile» [дока .../config-reference]
- Поля записи каталога (источник — исходник, НЕ дока; официальной доки формата нет): `slug`, `display_name`, `description`, `context_window`, `max_context_window`, `auto_compact_token_limit`, `truncation_policy` (`{"mode":"tokens","limit":10000}`), `shell_type`, `visibility`, `supported_in_api`, `priority`, `input_modalities`, `supported_reasoning_levels`, `default_reasoning_level`, `supports_parallel_tool_calls`, `supports_search_tool`, `supports_reasoning_summaries`, `support_verbosity`, `apply_patch_tool_type`, `web_search_tool_type` и др. [источник https://github.com/openai/codex/blob/main/codex-rs/models-manager/models.json] — `effective_context_window_percent` в текущем файле НЕ встречается (упоминается лишь во вторичных статьях; **не проверено** по официальной схеме)
- Максимум вывода: **[НЕ НАЙДЕНО, просмотрено: config-reference целиком (все ключи `model_*`), config-sample целиком, config-advanced целиком, models.json целиком (~50 уникальных полей записи), config.schema.json — единственные «output»-ключи там: `output_token_limit` в `McpServerToolConfig` («Token budget for this tool's output, before the standard 20% serialization allowance») и байтовые лимиты логов OTel; потолка генерации модели нет]**

## Ответ на вопрос 3 (прямо)

- **droid**: (а) контекст — **НЕТ** прямого ключа; косвенно только через `compactionTokenLimitPerModel`. (б) вывод — **ДА**, `customModels[].maxOutputTokens`.
- **codex**: (а) контекст — **ДА**, `model_context_window` (глобально/профиль) либо `context_window` в подменённом каталоге. (б) вывод — **НЕТ** ни одним ключом.

**Прямое vs косвенное.** У droid порог компакции — не то же, что размер окна: он задаёт «когда сжимать»,
а не «сколько модель вмещает». Практически это закрывает главный риск владельца (переполнение окна
можно предотвратить, выставив порог под свою модель), но остаётся без объявленного окна: индикатор
остатка, расчёт бюджета и любые эвристики «сколько влезет» опираются на выведенное харнессом значение,
которое пользователь не переопределяет. У codex `model_context_window` — на активную модель (одно
значение на профиль), а не на каждую модель в списке; per-model объявление есть только через полную
подмену каталога `model_catalog_json`, а она **заменяет** встроенный каталог, а не дополняет его.
