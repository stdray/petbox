# Выводы

## 1. pi ставит заголовок

`@earendil-works/pi-ai/dist/providers/opencode-headers.js`:

```js
const OPENCODE_SESSION_HEADER = "x-opencode-session";
// "Adds OpenCode's required per-conversation routing header before API dispatch."
```

`providers/opencode-go.js` оборачивает этой обёрткой **все четыре** адаптера провайдера
(`openai-completions`, `anthropic-messages`, `openai-responses`, `google-generative-ai`),
а `pi-agent-core/dist/agent.js:311` кладёт `sessionId: this.sessionId` в конфиг цикла,
то есть в опции каждого запроса. `pi-coding-agent` создаёт агента с
`sessionId: sessionManager.getSessionId()`.

Замерено:

- юнит на реальном `opencodeGoProvider()` — при наличии `sessionId` заголовок есть, без
  него нет; `x-session-affinity` не появляется (это отдельная ветка compat, см. п.4);
- e2e через фейковый шлюз (два запроса в одной сессии, второй форсирован tool-call'ом) —
  `x-opencode-session` одинаков на обоих, плюс `x-opencode-client: pi`.

## 2. Имя заголовка — не проблема

Шлюз требует заголовок сессии: без него `400 MissingSessionID`. Принимает **оба** имени —
нативный `session-id` и `x-opencode-session` (замер 09.09.2026, память `m-f86fbff4…`).
`x-opencode-session-id` из исходной гипотезы — третий заголовок, внутренний для opencode.

## 3. Разрыв с opencode: один заголовок против шести

Из бандла `@opencode/cli/bin/opencode.exe` (`nK = e => e.parentID ?? e.fork?.sessionID ?? e.id`,
где `F = nK(T)`):

```js
{"x-opencode-session-id": T.id,
 "x-opencode-parent-session-id": T.parentID,
 "x-session-affinity": F, "X-Session-Id": F,
 "x-opencode-session": F,
 "x-opencode-project": T.projectID, ...}
```

pi отправляет только `x-opencode-session` (+ `x-opencode-client`, UA). `x-opencode-parent-session-id`
не отправляется никогда: каждая суб-сессия pi (субагент) — отдельная сессия со своим id.

## 4. Но по деньгам pi не хуже

Разбор JSONL-сессии `01a19c7f-c534-7262-94c2-2a465dbca25f`
(`~/.pi/agent/sessions/--D--my-prj-petpi--/`) против `opencode.db` (GD провайдер
`opencode-go`, модель `deepseek-v4.1-flash`):

| | pi | opencode |
|---|---|---|
| ответов | 303 | 196 (9 сессий) |
| с кеш-хитом | **291 = 96 %** | 185 = 94.4 % |
| промахи посреди работы | 4 = 1.3 % | 3 = 1.5 % |
| промахи после простоя ≥6 мин | **5** | 0 |

Пять промахов pi — простой 55 мин / 82 мин / 92 мин / 35 ч: префикс вытеснен на апстриме.
Заголовок тут ни при чём — держать префикс живым может только запрос, а его не было.

Четыре мид-конверсейшн промаха pi сопровождались сменой семейства `responseId`
(`gw-…` → `chatcmpl-fe2aea…` → `0217937786…`): запрос обслужил другой апстрим. У opencode
такие же промахи есть (3, все с коротким простоем) — значит это failover шлюза, а не
дефект клиента.

Цена промахов в разобранной сессии: **$0.375 из $0.83** (45 %).

## 5. Флаг, который закрывает часть разрыва

`compat.sendSessionAffinityHeaders: true` на модели (по умолчанию включён только для
OpenRouter) добавляет к `openai-completions` три заголовка с тем же id сессии:
`x-session-affinity`, `session_id`, `x-client-request-id`. Проверено в e2e. Заголовок
`x-opencode-session-id` выставить нечем — отдельного флага нет.

Ставить его как «исправление» нельзя: разница в цифрах отсутствует, а изменение конфигурации
без замера до/после — расход денег ради гипотезы.
