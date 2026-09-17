# `agents.modelGrades` в qwen-code

Источники: установленный пакет `@qwen-code/qwen-code@0.23.0`
(`C:/nvm4w/nodejs/node_modules/@qwen-code/qwen-code`, минифицированный бандл)
+ исходники TS `QwenLM/qwen-code` на GitHub (`gh api repos/.../contents/...`,
default branch на 2026-09-10).

## 1. Что это по коду
`agents.modelGrades` — `{grade: modelSelector}`. **Ключ поиска — grade**
(левая часть, имя-псевдоним), **значение — селектор модели** (правая часть:
`fast`, `authType:model-id`...): отображение имя→селектор, не список. [код
`packages/core/src/subagents/subagent-manager.ts:179-201`, то же в бандле
`chunks/chunk-4F7GQGXB.js@4977987`: `getAvailableModelGrades()` строит `Map`
из `Object.entries(modelGrades).flatMap(([grade, model])=>...)`, фильтруя по
`allowedGrades`]. Дока: "Maps semantic grade names exposed to the Agent tool
to model selectors." [`docs/users/configuration/settings.md` /
`bundled/qc-helper/docs/configuration/settings.md:411`]

Потребитель — тул `Agent`. Доступные грейды идут в схему параметра `model`
как `enum` [код `agent.ts:983-989`]. При спавне `resolveModelGrade(grade,
agentConfig)` даёт селектор по имени грейда, **только если** модель
субагента не задана явно (не-`inherit`) — явная модель агента приоритетнее
грейда [код `subagent-manager.ts:158-176`; вызов `agent.ts:2654-2658`].

Identity-map из `~/.qwen/settings.json` (`"openai:<id>": "openai:<id>"`) —
вырожденный, но валидный случай: имя грейда = сам id модели, грейд
используется как allow-листинг id для `model` без переименования; `subagent_type` (роль) в структуре не участвует нигде в коде.

## 2. Поведение при отсутствии записи
Незнакомый/запрещённый `params.model` → спавн **отклоняется** явной ошибкой
(валидация параметров, не фолбэк и не подмена): `Unknown model grade
"${params.model}". Available: ...` [код `agent.ts:1081-1085`, дословно в
бандле `chunk-4F7GQGXB.js@2756013`].

Оговорка: ветка выполняется только если `model` вообще передан
(`if (params.model !== undefined)`, `agent.ts:1065`). Без `model` валидация
не запускается — спавн идёт по умолчанию, без обращения к `modelGrades`.
Тезис «без modelGrades спавн отвергается» — частично верен: если
`agents.modelGrades` не задан, `getAvailableModelGrades()` возвращает пустую
`Map` [`subagent-manager.ts:180-187`], и тогда любой явный `model:"<грейд>"`
отклоняется (Available: пусто) — но спавн БЕЗ параметра `model` никогда не
отвергается по этой причине. Отдельно (до проверки грейда) отклоняются:
`model` с `subagent_type:"fork"` и `model` у именованного teammate
[`agent.ts:1069-1077`].

## 3. Зачем — замысел авторов
Цитата дизайн-документа: "Let the model choose a user-defined model grade when spawning a regular subagent without exposing provider-specific model IDs in the Agent tool schema." ... "Concrete model selectors remain private to user settings." [`docs/design/subagent-model-grade.md`, `QwenLM/qwen-code`]

Приоритет резолюции (тот же документ, совпадает с кодом): 1) явная
не-`inherit` модель кастомного агента; 2) разрешённый грейд из
`modelGrades`; 3) built-in Explore-модель; 4) модель родителя. "Unknown or
disallowed grades are rejected." — подтверждение п.2 из первоисточника.
Смысл косвенности: скрыть от вызывающей модели provider-specific id, оставив
абстрактные ярлыки; `allowedGrades` — белый список годных грейдов.

## 4. Что НЕ установлено
- История появления (issue/PR/коммит, автор): не проверялась через `git
  log`/`gh pr list`; design-doc найден, PR-номер не извлекался. Точное
  соответствие коммита default-branch и бандла `0.23.0` тоже не сверялось.
  [НЕ УСТАНОВЛЕНО]
- Связь с YAML-frontmatter (`~/.qwen/agents/*.md`): `model` там
  документирован только как `inherit`/`fast`/`modelId`/`authType:modelId`
  [`docs/users/features/sub-agents.md:211`]; имя грейда как `model` во
  frontmatter не встречается и не читается в `resolveModelGrade`/
  `getAvailableModelGrades` — похоже, грейд это рантайм-параметр вызова тула
  (`params.model`), не то, на что агент ссылается в определении.
  Исчерпывающий grep репозитория на чтение `modelGrades` из
  frontmatter-парсера не проводился. [частично НЕ УСТАНОВЛЕНО]
