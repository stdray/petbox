# Реальные id моделей по харнессам (снимок 2026-09-10, только чтение)

## 1. qwen — `~/.qwen/settings.json`

`security.auth`: selectedType=openai, baseUrl=`http://127.0.0.1:1234/v1/` (локальный llama.cpp, БЕЗ денег) — это НЕ то же самое, что providers ниже; используется как молчаливый fallback, когда `model.name` не резолвится в `modelProviders` (код-путь подтверждён observation `qwen-printed-fragment-silently-reverts-effort-and-maxtokens`, не проверялся мной живьём). `model.name` (интерактивная сессия) = `ds-deepseek-v4-flash` [файл ~/.qwen/settings.json].

| id (provider record) | reasoning_effort | extra_body.model | baseUrl | кошелёк |
|---|---|---|---|---|
| ds-deepseek-v4-pro | high | deepseek-v4-pro | api.deepseek.com/v1 | деньги (deepseek напрямую) |
| ds-deepseek-v4-pro-max | max | deepseek-v4-pro | api.deepseek.com/v1 | деньги |
| ds-deepseek-v4-flash | high | **deepseek-flash** (не v4-flash!) | api.deepseek.com/v1 | деньги |
| ds-deepseek-flash-max | max | deepseek-flash | api.deepseek.com/v1 | деньги |
| go-glm-5.3-flash | high | glm-5.3-flash | opencode.ai/zen/go/v1 | квота (opencode-go) |
| go-glm-5.3-flash-low | low | glm-5.3-flash | opencode.ai/zen/go/v1 | квота |
| go-qwen3.8-max | xhigh | qwen3.8-max | opencode.ai/zen/go/v1 | квота |
| go-deepseek-flash | high | deepseek-flash | opencode.ai/zen/go/v1 | квота |
| go-deepseek-flash-low | low | deepseek-flash | opencode.ai/zen/go/v1 | квота |

[файл ~/.qwen/settings.json, ключ `modelProviders`]
`agents.modelGrades`: НЕ роль→grade, а identity-map — все 9 id выше отображены сами в себя (`"openai:<id>": "openai:<id>"`) [файл ~/.qwen/settings.json, ключ `agents.modelGrades`].

## 2. opencode — `~/.config/opencode/opencode.json`

Верх: `model`=`deepseek/deepseek-v4-pro`, `small_model`=`deepseek/deepseek-v4-flash`, `default_agent`=`petbox-orchestrator`.

| petbox-роль (agent) | variant | steps |
|---|---|---|
| petbox-orchestrator | high | 600 |
| petbox-worker | high | 350 |
| petbox-worker-highstakes | max | 350 |
| petbox-explore | low | 170 |
| petbox-reserve | xhigh | 120 |

[файл ~/.config/opencode/opencode.json, ключ `agent`] — фактический `provider/model` id по роли берётся не отсюда, а из `roles.json` (табл. 6).

Провайдер `llama.cpp` (локальный, baseURL 127.0.0.1:1234): 3 модели — `qwen3.6-35b-a3b@ud-q4_k_m`, `qwen3.6-27b@iq3_xxs`, `qwen3.5-27b@ud-iq3_xxs` — ни один id не встречается в `roles.json`, похоже на неиспользуемый провайдер [файл ~/.config/opencode/opencode.json]. `D:/my/prj/petbox/opencode.json` — [НЕ НАЙДЕНО] (файла нет).

## 3. codex — `~/.codex/config.toml` + `~/.codex/petbox-model-catalog.json`

Верх (интерактивный дефолт): `model_provider`=deepseek, `model`=deepseek-v4-pro [файл ~/.codex/config.toml].

Провайдеры: `deepseek` baseUrl=api.deepseek.com/v1, wire_api=responses, env=DEEPSEEK_API_KEY → деньги; `opencode-go` baseUrl=opencode.ai/zen/go/v1, wire_api=responses, env=OPENCODE_GO_API_KEY, header `x-opencode-session` → квота [файл ~/.codex/config.toml].

| slug (каталог) | context_window | reasoning_effort levels |
|---|---|---|
| deepseek-v4-flash | 128000 | [НЕ НАЙДЕНО] (supported_reasoning_levels: []) |
| deepseek-v4-pro | 128000 | [НЕ НАЙДЕНО] |
| grok-4.6 | 128000 | [НЕ НАЙДЕНО] |
| deepseek-flash | 1048576 | [НЕ НАЙДЕНО] |

[файл ~/.codex/petbox-model-catalog.json]
## 4. droid — `~/.factory/settings.json` (`customModels`)

| id | model | baseUrl | provider(wire) | extra | кошелёк |
|---|---|---|---|---|---|
| custom:DeepSeek-V4-Pro-0 | deepseek-v4-pro | api.deepseek.com/anthropic | anthropic | — | деньги |
| custom:DeepSeek-V4-Flash-1 | deepseek-v4-flash | api.deepseek.com/anthropic | anthropic | — | деньги |
| custom:GLM-5.2-[...orchestrator]-0 | glm-5.2 | opencode.ai/zen/go/v1 | generic-chat-completion-api | — | квота |
| custom:Kimi-K2.7-Code-[...worker/code]-0 | kimi-k2.7-code | opencode.ai/zen/go/v1 | generic-chat-completion-api | — | квота |
| custom:MiMo-V2.5-[...utility]-0 | mimo-v2.5 | opencode.ai/zen/go/v1 | generic-chat-completion-api | — | квота |
| custom:Qwen3.7-Max-[...orchestrator]-0 | qwen3.7-max | opencode.ai/zen/go/v1 | anthropic | — | квота |
| custom:Qwen3.7-Plus-[...worker]-0 | qwen3.7-plus | opencode.ai/zen/go/v1 | anthropic | — | квота |
| custom:MiniMax-M3-[...worker]-0 (index 7) | opencode-go/minimax-m3 | opencode.ai/zen/go/v1 | anthropic | — | квота |
| custom:MiniMax-2.7-[...orchestrator]-0 (index 7, дубль!) | minimax-2.7 | opencode.ai/zen/go/v1 | anthropic | — | квота |
| custom:DeepSeek-Flash-0 | deepseek-flash | api.deepseek.com/anthropic | anthropic | — | деньги |
| custom:Qwen3.8-Max-[...reserve]-0 | qwen3.8-max | opencode.ai/zen/go (без /v1!) | anthropic | extraHeaders x-opencode-session | квота |
| custom:GLM-5.3-Flash-[...worker]-0 | glm-5.3-flash | opencode.ai/zen/go/v1 | generic-chat-completion-api | extraHeaders x-opencode-session | квота |
| custom:GLM-5.3-Flash-[...explore]-0 | glm-5.3-flash | opencode.ai/zen/go/v1 | generic-chat-completion-api | extraHeaders x-opencode-session | квота |
| custom:DeepSeek-Flash-HighStakes-0 | deepseek-flash | api.deepseek.com/anthropic | anthropic | extraArgs.thinking budget=16000 | деньги |

[файл ~/.factory/settings.json, ключ `customModels`]
Наблюдение (уже задокументировано, НЕ подаю повторно — дедуп по recurrenceCount): 8 gateway-маршрутизированных записей (Qwen3.7-*, GLM-5.2, Kimi, MiMo, MiniMax-*) нерабочие живьём (3 независимые причины: baseUrl с лишним `/v1`, отсутствие `extraHeaders`, протухший apiKey) — board `observations`, ключ `droid-gateway-custommodels-nonfunctional-url-key-header`, recurrenceCount=2, lastSeenAt 2026-09-10T07:41.

## 5/6. `~/.petbox/roles.json` — activeProfile: **opencode-main**

### Профиль opencode-main (АКТИВНЫЙ)

| харнесс | orchestrator | worker | worker-highstakes | explore | reserve |
|---|---|---|---|---|---|
| opencode | deepseek/deepseek-v4-flash | opencode-go/glm-5.3-flash | deepseek/deepseek-v4-flash | opencode-go/glm-5.3-flash | opencode-go/qwen3.8-max |
| droid | custom:DeepSeek-Flash-0 | custom:GLM-5.3-Flash-[...worker]-0 | custom:DeepSeek-Flash-HighStakes-0 | custom:GLM-5.3-Flash-[...explore]-0 | custom:Qwen3.8-Max-[...reserve]-0 |
| claude-code | opus | sonnet | opus | haiku | fable |
| codex | deepseek-flash | deepseek-flash | deepseek-flash | deepseek-flash | deepseek-flash |
| qwen | openai:ds-deepseek-v4-flash | openai:go-deepseek-flash | openai:ds-deepseek-flash-max | openai:go-deepseek-flash-low | openai:go-qwen3.8-max |

### Профиль opencode-go-max

| харнесс | orchestrator | worker | worker-highstakes | explore | reserve |
|---|---|---|---|---|---|
| opencode | deepseek/deepseek-v4-pro | opencode-go/glm-5.3-flash | opencode-go/deepseek-v4-pro | opencode-go/glm-5.3-flash | opencode-go/qwen3.8-max |
| droid | custom:DeepSeek-V4-Pro-0 | custom:DeepSeek-V4-Pro-0 | custom:DeepSeek-V4-Pro-0 | custom:DeepSeek-V4-Flash-1 | custom:Qwen3.7-Max-[...orchestrator]-0 |
| claude-code | opus | sonnet | opus | haiku | fable |
| codex | deepseek-v4-pro | deepseek-v4-flash | deepseek-v4-pro | deepseek-v4-flash | deepseek-v4-pro |
| qwen | openai:ds-deepseek-v4-pro | openai:ds-deepseek-v4-flash | openai:ds-deepseek-v4-pro | openai:ds-deepseek-v4-flash | openai:ds-deepseek-v4-pro |

### Профиль opencode-direct

| харнесс | orchestrator | worker | worker-highstakes | explore | reserve |
|---|---|---|---|---|---|
| opencode | deepseek/deepseek-v4-pro | deepseek/deepseek-v4-flash | deepseek/deepseek-v4-pro | deepseek/deepseek-v4-flash | opencode-go/qwen3.8-max |
| droid | custom:DeepSeek-V4-Pro-0 | custom:DeepSeek-V4-Pro-0 | custom:DeepSeek-V4-Pro-0 | custom:DeepSeek-V4-Flash-1 | custom:Qwen3.7-Max-[...orchestrator]-0 |
| claude-code | opus | sonnet | opus | haiku | fable |
| codex | deepseek-v4-pro | deepseek-v4-flash | deepseek-v4-pro | deepseek-v4-flash | deepseek-v4-pro |
| qwen | openai:ds-deepseek-v4-pro | openai:ds-deepseek-v4-flash | openai:ds-deepseek-v4-pro | openai:ds-deepseek-v4-flash | openai:ds-deepseek-v4-pro |

[файл ~/.petbox/roles.json] — все `origin` в opencode-main = `owner`; в go-max/direct droid/opencode = `owner`, claude-code/codex/qwen = `kit`.

## 7. Кошелёк — сводка по origin id

- **деньги (deepseek напрямую)**: baseUrl `api.deepseek.com/*` — все `deepseek-v4-pro`, `deepseek-v4-flash`, `deepseek-flash` записи вне opencode-go.
- **квота подписки (opencode-go)**: baseUrl `opencode.ai/zen/go*` — `glm-5.3-flash`, `glm-5.2`, `qwen3.8-max`, `qwen3.7-*`, `kimi-k2.7-code`, `mimo-v2.5`, `minimax-*`, а также `deepseek-flash`/`deepseek-v4-pro` записи, когда они идут через opencode-go (`opencode-go/deepseek-v4-pro`, qwen go-deepseek-flash*) — тот же модельный slug, другой кошелёк.
- **иной кошелёк (не deepseek/opencode-go)**: claude-code роли (opus/sonnet/haiku/fable) — Anthropic/Claude-подписка, вне запрошенных двух категорий.
- **локально, без кошелька**: qwen `security.auth` (127.0.0.1:1234), opencode `llama.cpp` провайдер.

## Известные расхождения (не подаю новых observation — уже задокументировано)

1. qwen id `ds-deepseek-v4-flash` называется "v4-flash", но реальный `extra_body.model` = `deepseek-flash` — легаси-имя id пережило вчерашний переход роутинга на deepseek-flash.
2. Умирающая модель `deepseek-v4-pro` жива только в профилях opencode-go-max/opencode-direct (origin=kit) и в droid `custom:DeepSeek-V4-Pro-0`; в активном opencode-main везде вытеснена `deepseek-flash`.
3. droid `customModels`: две записи (`MiniMax-M3`, `MiniMax-2.7`) имеют одинаковый `"index": 7` — не проверял, ломает ли это droid; область уже покрыта board `observations` → `droid-gateway-custommodels-nonfunctional-url-key-header` (recurrenceCount=2).
4. qwen: несовпадение id → молчаливый fallback на `security.auth` (127.0.0.1:1234) при коде возврата 0 — уже задокументировано в `qwen-printed-fragment-silently-reverts-effort-and-maxtokens` (board `observations`, status=promoted); в ТЕКУЩЕМ `roles.json`/`settings.json` все 5 id профиля opencode-main резолвятся, проблема не активна прямо сейчас.
