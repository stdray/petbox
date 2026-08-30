# Фактический контур разрешения ОПРЕДЕЛЕНИЯ сегодня

Дата: 2026-08-30. Автор: petbox-worker (Sonnet 5). Область: путь от `src/common/default-agents.json`
до артефакта, порядок резолва в `agent-def-fetch.ts`, два потребителя, что делает `apply` с
машинными полями, серверное хранение, наличие слоёв.

## Ответ одной строкой
Резолв ОДНОУРОВНЕВЫЙ: server → LKG-РЕПЛИКА → встроенный DEFAULT — цепочка деградации ОДНОГО
документа, не каскад слоёв; баннер сессии и компиляция артефактов — ДВА НЕЗАВИСИМЫХ живых резолва
в разных процессах, поэтому claim резерва (баннер v18 / артефакты LKG v17) СТРУКТУРНО ВОЗМОЖЕН.

## Факты

### 1. Путь источника до артефакта
1. Канон: `src/common/default-agents.json` (гит, единственный редактируемый файл).
2. Бэкенд грузит его embedded resource в `PetBox.Core.csproj`; читает и лениво валидирует
   `src/PetBox.Core/Contract/DefaultAgentDefinition.cs:35,37,44-52,57-64`.
3. Сидирование в проект: `src/PetBox.Web/AgentDefs/ProjectAgentDefSeeder.cs:1-30` — ОДИН РАЗ на
   `ProjectDirectory.CreateAsync`, не перезаписывает существующий `default` (пробник + version 0 =
   create). После сидирования проект ВЛАДЕЕТ копией и расходится свободно (`DefaultAgentDefinition.cs:24-26`).
4. npm-кит: `sync-default-agents.mjs:20-42` копирует тот же канонический файл в
   `packageRoot/src/default-agents.json` (гитигнор, генерится в pretest/pretypecheck/prepack) —
   офлайн-fallback кита, читается как `DEFAULT_AGENT_DEFINITION` (`agent-def-fetch.ts:25`).
5. `petbox-wire` читает СЕРВЕРНУЮ копию проекта: `GET /api/{projectKey}/agent-defs/{key}`
   (`agent-def-fetch.ts:200,3-6`).

### 2. Порядок резолва — `resolveAgentDefinitionWithLkg` (`agent-def-fetch.ts:340-443`)
1. Живой fetch (если не `--offline` и есть projectKey/baseUrl/apiKey, `:362-385`). Успех →
   `writeAgentDefCache` (LKG перезаписывается) + `source:"server"`, возврат сразу.
2. Провал классифицируется, никогда не бросает: 404→`notFoundOnServer`(`:386`),
   401/403→`forbidden`(`:387-394`), другой не-2xx→`httpError`(`:395-402`), 2xx-битое-тело→
   `parseError`(`:403-405`).
3. LKG-кеш: `readAgentDefCache` (`:281-322`). Ключ кеша — путь `agentDefCachePath` =
   `~/.petbox/cache/<projectKey>.agent-def.json` (`:124-133`); отсутствие файла — тихо (обычный
   случай), битый JSON/форма/policy — логируется и трактуется как «нет кеша» (`:274-321`).
4. Кеш есть и ключ совпадает (или запрошен `"default"`) → `source:"lkg"`, `stale:true`,
   один из двух маркеров offline/unreachable (`:31-40,420-423`).
5. Иначе → `DEFAULT_AGENT_DEFINITION`, `source:"default"`, с флагами точной причины (`:433-442`).
`--offline` пропускает шаг 1, но всё ещё предпочитает LKG над DEFAULT (`:338,362`).

### 3. Два потребителя — ОДНА функция, ДВА независимых вызова
Баннер: `pull-memory.ts:99`, `droid-pull-memory.ts:94`, `opencode-plugin.ts:51` зовут
`resolveAgentDefinitionForSession` (`agent-def-fetch.ts:512-526`, фиксирует `definitionKey:
DEFAULT_DEFINITION_KEY`) из SessionStart-хука — отдельный процесс. Артефакты: `wire.ts:972-986`
(`resolveApplyDefinition`) зовёт ТОТ ЖЕ `resolveAgentDefinitionWithLkg` (`wire.ts:980`) из команды
`apply`/`doctor` — другой процесс, другое время. `apply-artifacts.ts` сам НЕ резолвит: принимает
готовый `AgentDefinition` параметром (`planApply`, `:270-273`), ни одного вызова agent-def-fetch
в файле. ПОДТВЕРЖДЕНО: `agentDefinitionBannerNote` — форматтер поверх уже полученного результата,
не второй резолвер (`:445-450`). Но резолв каждый вызывающий делает НЕЗАВИСИМО — LKG обновляется
только при успехе КОНКРЕТНОГО fetch (`:377`), общего in-memory кеша между процессами нет. Если
сервер меняет версию между SessionStart-хуком и последующим `apply`, или один живой fetch поймал
сетевой сбой, а другой нет — баннер и артефакты МОГУТ разойтись по версии. Обычно совпадают (LKG
подтягивается к последнему успеху), но гарантии атомарности/снапшота на сессию НЕТ.

### 4. Что `apply` делает с машинными полями
`requiredCapabilities` → MCP-обвязка ТОЛЬКО у droid: `needsMcp` (`mcp_main_session`/`mcp_subagent`/
`spawn.allowed`) добавляет `mcpServers:["petbox"]` (`apply-artifacts.ts:236-244`). Claude Code/
opencode рендер (`:190-200`) НЕ пишет `tools:`/`mcpServers:` — весь тулсет наследуется харнессом
(`:185-186`); requiredCapabilities для них — только данные truthfulness-гейта и текста тела
(`:126-133`), не переключатель MCP. `spawn.allowedRoles` → блок `## Spawn` (`buildRoleBody`,
`:141-153`) — цели рендерятся через `emittedRoleName(r)`, НЕ голый slug, иначе `subagent_type` в
тексте не совпадёт с реально записанным файлом. Гейт правдивости `truthfulness.ts`:
`checkRoleTruthfulness` (`:148-162`) сравнивает `effectiveRequiredCapabilities` (spawn.allowed⇒
неявно требует `spawn_subagents`, `:82-88`) с `harnessCapabilities(harness)` + модельный гейт
(foreign-shape id блокируется, `:99-115`). `planApply` (`apply-artifacts.ts:297-336`) зовёт гейт
ДО рендера — грязная роль пропускается целиком (`skippedRoles`), никогда не пишется частично; для
closed-model-space харнесса (claude-code) unbound-роль — тоже жёсткий отказ (`:322-328`).

### 5. Серверное хранение и другие потребители
`AgentDefsApi.cs:20-43` — REST GET list/get, PUT, DELETE, скоупы `agents:read`/`agents:write`
(`:54,63,81,116`; `ApiKeyScopes.cs:74-75,112-113`). ОПРОВЕРГНУТО «вне petbox-wire потребителей
нет»: есть MCP-инструменты `Mcp/AgentDefTools.cs` (`agent_def_list/get/upsert/delete` — их же
зовёт этот worker через `mcp__petbox__agent_def_*`) и admin UI
`Pages/Admin/ProjectAgentDefs.cshtml.cs` — оба читают/пишут тот же `IAgentDefinitionService`,
минуя petbox-wire целиком. Это потребители хранилища, не потребители «резолва для компиляции
артефактов» — резерву стоит уточнить формулировку.

### 6. Слой/каскад — НЕТ, одноуровневый
Ничего похожего на «локальный слой проекта» не найдено. Единственная «многоуровневость» — цепочка
ДЕГРАДАЦИИ (server → LKG-реплика → DEFAULT-реплика) = РЕПЛИКА (кэш) по легенде, не СЛОЙ (дельта).
Проектный документ на сервере после сидирования — тоже не слой поверх канона, а РАЗВИЛКА:
`ProjectAgentDefSeeder` копирует канон один раз, дальше проект и канон независимы (`:24-26`).

### Ветка `agent-def-typed-merge` (`feat/agent-def-typed-and-merge-by-role`, HEAD `9f585f32`)
Мерж-по-ролям УЖЕ В main (коммит `e92589e5`, предок текущего HEAD `5d2a713c` — проверено
`git merge-base --is-ancestor`). Ветка — ОТДЕЛЬНАЯ, разошедшаяся РАНЬШЕ (`git merge-base main
<branch>` = `ad845278`, не текущий main) независимая попытка ТОЙ ЖЕ задачи. Ветка использует
ПЛОСКУЮ форму роли (`spawnAllowed`/…) ради охвата `McpUnknownParameterFilter` (один хоп в глубину,
`McpUnknownParameterFilter.cs:44-49`). Main решил проблему ИНАЧЕ и строже: `Program.cs:513`
включает `JsonUnmappedMemberHandling.Disallow` глобально для MCP-сериализатора — непойманный член
отвергается на любой глубине, поэтому main остался на ВЛОЖЕННОЙ форме (`McpToolInputs.cs:572-579,
594-601`), что и документирует `McpToolInputs.cs:539-562` («Do not re-flatten citing the filter»).
Ветка технически устарела относительно main; факт двух параллельных реализаций одной карточки на
разных базах — сигнал координации, не дефект резолва.

## Что из этого следует для гипотезы «источник истины — файлы»
Источник истины сегодня — гитовый `src/common/default-agents.json` («ONE FILE, TWO READERS»,
`DefaultAgentDefinition.cs:10`). Но живое поведение агента подчиняется НЕ ему, а серверной
РЕПЛИКЕ конкретного проекта (после сидирования — независимой), и при недоступности сервера — ещё
и локальной LKG-реплике. Три копии физически существуют (гит, сервер-на-проект, LKG на диске),
приоритет между ними — «старшая по свежести живого ответа», а не «канон всегда выигрывает».

## Спорное
Резерв: «баннер может строиться из v18, а артефакты — из LKG v17». Нахожу: структурно возможно
(два независимых живых резолва в разных процессах, LKG обновляется только на успехе КОНКРЕТНОГО
fetch), но не потому, что резолв «читает два разных кэша» — механизм один и тот же
(`resolveAgentDefinitionWithLkg`), просто вызывается дважды в разное время без снапшота на сессию.
Резерв верен по следствию, неточен по механизму.

## Чего я не проверил
Не запускал живой SessionStart→apply на реальном сервере, чтобы замерить окно рассинхронизации.
Не смотрел `harness-capabilities.ts`/`harness-models.ts` целиком (только точки вызова). Не читал
`AgentDefinitionService.cs`/`AgentDefinitionMergeTests.cs` целиком — только заголовок и grep.
