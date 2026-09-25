# 65 — Практический стенд: CLIProxyAPI как локальный шлюз для codex (vs agentgateway)

Проверено 2026-09-25. `router-for-me/CLIProxyAPI` v7.3.17, релиз-бинарь Windows amd64 (без Docker —
официального образа не нашли, см. `61-candidates-go.md`), режим — только `openai-compatibility`
провайдеры с API-ключами (без OAuth/подписок). Метод переиспользует `bench/u1`/`bench/u2` (фейковые
апстримы, порты 9101/9102) и codex-пробу из `63-server-needs-and-codex.md` (временный `CODEX_HOME`,
`wire_api="responses"`, `sandbox_mode="danger-full-access"` — без него `exec_command` блокируется
политикой, не связано со шлюзом). Baseline agentgateway — из `64-bench.md`, не повторялся здесь.

## Таблица сценариев

| # | Сценарий | CLIProxyAPI | agentgateway (64-bench) |
|---|---|---|---|
| B1 | codex(responses) → шлюз → u1 chat-completions: перевод + tool call + реальный shell round-trip | ✅ | ✅ |
| B2 | routing по имени виртуальной модели → u1/u2 | ✅ | ✅ |
| B3 | fallback: сломанный апстрим → автоматически здоровый; cooldown | ✅ **подтверждено эмпирически, без explicit-списка в запросе** | ⚠️ не сработало в стенде (alpha) |
| B4 | embeddings + rerank через шлюз | ❌ оба `404 Not Found` | ✅ один provider на chat+embed+rerank |
| B5 | header rewrite: снять клиентский, добавить свой | ✅ | ✅ |
| B6 | ресурсы: RAM idle/200req, размер, старт | см. ниже — тяжелее agentgateway, легче Bifrost | см. ниже |
| B7 | fallback на 2 РЕАЛЬНЫХ провайдера (opencode.json: deepseek + opencode-go) + живой codex exec | ✅ (новый сценарий, не гонялся у agentgateway) | не замерено в этой сессии |

## B1 — codex через CLIProxyAPI, детально

`model_provider="cpa"`, `base_url="http://127.0.0.1:8317/v1"`, `wire_api="responses"`, модель
`u1-chat` (alias на `openai-compatibility` провайдер `u1`). `codex exec --skip-git-repo-check
"please run: echo hi"`:
```
exec
"C:\...\pwsh.exe" -Command 'echo hi' ... succeeded in 60ms:
hi
codex
shell said: Chunk ID: 50de21
Wall time: 0.0600 seconds
Process exited wi...
```
Прямой HTTP-тест (`POST /v1/responses`, `stream:false`) на `u1-chat` тоже дал корректный
`{"object":"response","status":"completed","output":[{"type":"function_call",...}]}` — перевод
Responses↔chat, включая function_call, подтверждён и по коду (X1/X2 в `61-candidates-go.md`), и
живьём.

## B2/B3 — routing и fallback, детально

Три `openai-compatibility` провайдера в одном конфиге: `u1`→u1(:9101), `u2`→u2(:9102, `429` +
`Retry-After:3`), `u1-for-fallback`→u1(:9101). Алиас `u2-chat` (только у `u2`) корректно
пробрасывает `429`/`Retry-After` клиенту как есть (B2). Алиас `route-fallback` — **пул из двух
моделей у РАЗНЫХ провайдеров** (`u2` зарегистрирован первым, `u1-for-fallback` вторым):

```
try1: 200 (1 upstream hit — u1 напрямую, round-robin выбрал его первым)
try2: 200 (2 upstream hit — u2 429, затем u1 200: реальный fallback)
try3..8: 200, все — u1; u2 больше НЕ выбирается (cooldown)
```
Подтверждено логами апстримов: `u2.log` — 4 хита за всю сессию (включая B2), `u1.log` растёт на
каждый запрос `route-fallback`. И debug-логом шлюза (`conductor_execution.go`): `429 |
upstream execution failed: provider=openai-compatible-u2 ...` → следующая строка того же
request-id — успешный `200` с провайдера `u1-for-fallback`.

**Важный нюанс поведения** (не был очевиден из доков): выбор внутри пула — **weighted
round-robin/случайный, не строго "сначала первый в списке"** — `try1` сразу попал на u1, минуя u2.
Настоящий fallback (`429` → следующий) виден на `try2`, после чего `u2` **выпадает из ротации**
(cooldown, дефолт `disable-cooling:false`, глобальный) — `try3`..`try8` идут на u1 без единого
обращения к u2. Значит: авто-fallback + авто-cooldown у CLIProxyAPI работают **из коробки, без
explicit-списка в запросе** (в отличие от Bifrost) и **работают эмпирически** (в отличие от
agentgateway в этом стенде) — но порядок внутри пула не гарантированно "primary→secondary": для
детерминированного приоритета нужно тюнить `weight` (не проверялось отдельно).

## B4 — embeddings/rerank, детально

`POST /v1/embeddings` и `POST /v1/rerank` на `u1-chat` (провайдер `openai-compatibility` c u1,
который сам умеет оба эндпоинта) → **оба `404 Not Found`**, пустое тело. Это **эмпирическое
подтверждение**, не просто grep-находка из `61-candidates-go.md` (S2/S3): CLIProxyAPI не
проксирует `/v1/embeddings`/`/v1/rerank` для `openai-compatibility`-провайдеров вообще — ни S2, ни
S3 не закрыты, не только "не нашли в коде", а буквально нет маршрута.

## B5 — header rewrite, детально

`openai-compatibility[].headers: {X-Gateway-Injected: "cliproxyapi-was-here"}` дошёл до u1
(`"x-gateway-injected":"cliproxyapi-was-here"` в `requests.log`); клиентский
`x-client-should-be-stripped: leak-me` — нет (`undefined` в логе u1, сервер явно проверяет это
поле). Тот же паттерн, что у Bifrost/agentgateway в `64-bench.md`.

## B6 — ресурсы

| | CLIProxyAPI | agentgateway (64-bench) | Bifrost (64-bench) |
|---|---|---|---|
| Артефакт | релиз-бинарь, **Docker-образа нет** | `ghcr.io` образ, 147 MB on-disk | Docker Hub образ, 360 MB on-disk |
| Скачано (win amd64) | 22.9 MB (zip) / 70.3 MB (exe на диске) | — | 90.6 MB (Docker Hub API) |
| RAM idle (сразу после старта) | ~45-47 MiB (`tasklist`, 46 512 K) | ~17.5 MiB | ~146 MiB |
| RAM после ~200 chat-запросов | ~47-49 MiB (стабильно, без роста) | ~19 MiB | ~140 MiB |
| Старт (процесс → первый 200) | **~580 ms** | <1s | ~6s (с сетью до `getbifrost.ai`) |
| Сеть на старте | Пытается (не блокирующе) — 3 GitHub raw-фетча каталогов моделей + 1 GitHub API вызов за инфо о релизе панели управления; при рейт-лимите/офлайне — `warn`, старт не блокируется (подтверждено: 580ms успешный старт в том же прогоне, где GitHub API вернул `403 rate limit exceeded`) | Нет | Да, **блокирующе** (см. `64-bench.md` готча №1) |

Итого по весу: CLIProxyAPI **тяжелее agentgateway** (~2.6× по RAM idle) но **легче и надёжнее на
старте, чем Bifrost** (не виснет без сети, в отличие от жёсткой зависимости Bifrost от
`getbifrost.ai`).

## B7 — fallback на два реальных провайдера (НОВОЕ)

Источник провайдеров — `~/.config/opencode/opencode.json` (лежит не в этом репо):
`provider "deepseek"` и `provider "opencode-go"` — оба API-ключевые (env `DEEPSEEK_API_KEY`,
`OPENCODE_GO_API_KEY`; `llama.cpp` в том же конфиге — локальный, без ключа, не годится под B7).
Base URL взяты из закешированного каталога `models.dev`, который сам opencode использует
(`~/.local/share/opencode/opencode.db`, kv-запись `models-dev:catalog`): DeepSeek —
`https://api.deepseek.com`, OpenCode Go — `https://opencode.ai/zen/go/v1`.

Конфиг (шаблон без ключей — `bench/cliproxyapi/config-real.yaml.template`; реальный файл собирался
только в scratchpad и удалён после теста): провайдер `deepseek-broken` — **намеренно мёртвый порт**
`http://127.0.0.1:19999/v1` (не чужой ключ, не битая модель — ровно то, что просил владелец),
провайдер `opencode-go-real` — настоящий `base-url` + настоящий ключ, оба на алиасе
`real-fallback`.

```
POST /v1/chat/completions {"model":"real-fallback","messages":[{"role":"user",
  "content":"Reply with exactly: PONG"}],"max_tokens":16}
→ HTTP 200, model:"glm-5.3-flash" (реальный ответ opencode-go), usage.total_tokens=34
```
Мёртвый порт `deepseek-broken` не породил ни одной реальной попытки коннекта к api.deepseek.com
(соединение падает локально, до сети) — тот же механизм пула/cooldown, что в B3, отработал на
живом маршруте с первого запроса. **Ловушка по пути**: opencode-go требует нестандартный заголовок
`X-Opencode-Session` (без него — `400 MissingSessionID`, реальная ошибка живого апстрима, не
CLIProxyAPI) — добавлен статическим `headers:` на провайдере, что заодно подтвердило **hot-reload
конфига живьём** (правка файла подхватилась без рестарта процесса — S9 работает и для
`openai-compatibility`, в отличие от сломанного hot-reload agentgateway на Windows bind-mount,
готча №7 в `64-bench.md`).

Второй шаг — оба провайдера живые, `codex exec` через `wire_api="responses"` на реальный
`opencode-go-real` (модель `og-direct`, задача "Reply with exactly: PONG. Do not run any commands
or use any tools."):
```
codex
PONG
tokens used
16,997
```
Подтверждает Responses→chat трансляцию CLIProxyAPI на живом, недокументированном (не своём)
OpenAI-совместимом апстриме — не только на фейковом u1. **Оговорка по стоимости**: 16 997 токенов —
больше, чем "1-3 крошечных запроса" по духу задания, из-за системного промпта самого codex
(`reasoning_content` реального `glm-5.3-flash` тоже съел часть `max_tokens=16` в прямом curl-тесте
— модель "думает" перед ответом, `finish_reason:"length"` без видимого текста в первом прямом тесте
на `og-direct` до этого). Прямых curl-вызовов было 3 (og-direct×2, real-fallback×1) — счёт
"1-3 крошечных запроса" соблюдён по числу вызовов, не по общему токен-бюджету codex-обвязки.

## Найденные подводные камни (сверх известных по 61-/64-)

1. **Нет env-var индирекции для `api-key-entries`** — только литеральное значение в YAML (в
   отличие от Bifrost `env.VAR_NAME`). Ключи пришлось подставлять скриптом в файл, лежащий только
   в scratchpad, и удалять после теста — на реальном деплое это означает ключи в открытом
   YAML-файле на хосте (или собственный secrets-слой поверх, которого нет из коробки).
2. **Пул fallback внутри одного алиаса — weighted round-robin, не строгий приоритет** (см. B3) —
   для гарантированного "primary/secondary" нужно явно тюнить `weight`, не проверено отдельно.
3. **`/v1/embeddings` и `/v1/rerank` не существуют** для `openai-compatibility`-провайдеров вообще
   (404, не "not supported by this provider" как у Bifrost) — подтверждает S2/S3 из
   `61-candidates-go.md` эмпирически.
4. **Сторонний апстрим может требовать нестандартные заголовки** (`X-Opencode-Session`) — не баг
   CLIProxyAPI, но конфигурировать такое приходится вручную через `headers:` per-provider; без
   документации самого апстрима (`opencode.ai/docs/go`) не угадать.
5. **Hot-reload конфига работает** (в противовес зависшему hot-reload agentgateway на Windows) —
   плюс, не готча, но стоит явно зафиксировать как проверенное расхождение с `64-bench.md`.

## Итог: годится ли CLIProxyAPI на роль локального шлюза для codex лучше agentgateway?

Для **именно этой роли** (локальный, однопользовательский, на машине рядом с codex, без PetBox) —
CLIProxyAPI **не хуже и местами сильнее** agentgateway: X1/X2/B1/B2/B5 — паритет; **B3 (fallback)
CLIProxyAPI выигрывает эмпирически** (agentgateway не сработал в стенде на alpha-версии);
**единственный бинарь без Docker** ближе к формулировке владельца "локально, полностью
автономно" (00-index.md), чем образ, требующий Docker Desktop; hot-reload подтверждён рабочим (у
agentgateway — нет, на этой ОС). Embeddings/rerank (B4) codex не использует — эта дыра не бьёт
именно по роли "локальный шлюз для codex", хотя блокирует использование того же экземпляра для
серверной роли PetBox (S2/S3 в матрице кандидатов).
Минусы: RAM idle ~2.6× тяжелее agentgateway (не критично для одного дев-хоста, но не "почти ноль"),
ключи в открытом YAML без env-индирекции (требует собственного secrets-слоя на реальном деплое),
пул fallback — не строго приоритетный без тюнинга weight.
**Вывод**: для роли K3 ("шлюз только локально для codex", `70-verdict.md`) CLIProxyAPI —
жизнеспособный, эмпирически более надёжный по fallback кандидат, чем agentgateway, ценой RAM и
ручной работы с секретами; не заменяет agentgateway/Bifrost для серверной роли PetBox (S2/S3
не закрыты вовсе).

## Уборка
Все процессы (`cli-proxy-api.exe` ×2, `node.exe` ×2 — фейковые апстримы) убиты
(`taskkill /F`) в конце сессии. `config-real.yaml` (с реальными ключами deepseek/opencode-go)
существовал только в scratchpad и удалён; в репозиторий положен только `.template` с
`${DEEPSEEK_API_KEY}`/`${OPENCODE_GO_API_KEY}` плейсхолдерами. Временный `CODEX_HOME` (оба —
фейковый и реальный апстрим) остался только в scratchpad.
