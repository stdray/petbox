# Critique: Tasks + Memory модули

Жёсткая критика дизайна от стороннего ревьюера (claude-code general-purpose agent, 2026-05-27). Без validation, без обтекаемых формулировок.

## 1. Сама идея

Pain настоящий, но решение несоразмерно. Реальный pain: **claude-code теряет hot session plans при rewrite** + **разные агенты пишут в разные места**. Это два бага, не повод строить два модуля с историей, junction-таблицами, MCP-серверами и dogfooding-фазой.

Бенч в `doc/tasks-mcp/` (8 записей за один день, все от claude-code, все вариации одного типа операции — "edited plan.md") уже показывает что:
- 99% операций — `plan.md update`, остальное — заметки к разговору, не state.
- Записи делает один агент. Никакой "сходимости" между агентами нет — потому что **других агентов на проекте нет**. Это не bench, это твой собственный journal.
- `doc/tasks-mcp/README.md` сам прямо говорит "claude code часто игнорирует требование" — а это в том самом репо где разработчик сидит над агентом. В дикой природе compliance будет ниже.

Yak-shaving: да. Ты строишь инструмент для координации agent-ов, которые игнорируют куда более лёгкое требование (записать markdown файл по шаблону). MCP-tool с auth+upsert+revision history требует **больше** disciplined behavior от агента, не меньше.

## 2. Велосипед

Что уже делает то же самое:
- **mcp-server-memory** (официальный Anthropic example) — knowledge graph с entities/relations/observations через MCP. Multi-agent, file-based, нулевой setup. Покрывает 80% Memory модуля.
- **mcp-server-filesystem** + git — если plan.md в репе и каждый агент его правит, git и есть append-only history с junction-table к commit-hash'ам. `PlanNodeRef.commit_hash` буквально дублирует git blame.
- **Linear / Plane.so / GitHub Issues + sub-issues** — древовидные таски с историей, API, MCP-серверы есть у всех трёх. Если нужен structured plan — это они.
- **Obsidian + Templater + Sync** — markdown с backlinks (`[[name]]`), dangling links, history через git. Это буквально дизайн MemoryLink.
- **Logseq / Reflect / mem.ai** — то же самое в плоскости notes.

Чем yobabox-Tasks будет лучше: **ничем для single-user-pet-project**. Аргумент "у меня уже есть auth и workspace" слабый — экономия на интеграции не оправдывает написание ещё двух модулей с миграциями, UI, MCP-эндпоинтами и hooks.

## 3. Главные проблемы дизайна

- **Markdown как render, а не storage.** Bidirectional откладывается — это значит что **plan.md становится write-only mirror'ом**. Любой агент или человек, который правит plan.md руками (а это удобнее MCP-tool вызова), теряет правки. Это хуже текущего состояния, где plan.md хотя бы source of truth.
- **PlanNode дерево неограниченной глубины.** `doc/plan.md` — 902 строки, 29 фаз, sub-секции типа `24.5`. На дереве это будет UI-ад с collapse state, drag-drop reordering, и непонятно куда лепить prose между nodes (а в plan.md prose между bullet'ами есть — `## Phase 22` имеет 4 параграфа объяснений).
- **PlanNodeRef с commit-hash'ами.** Зачем? Git уже знает где упомянут любой identifier. Secondary index с устаревающей информацией.
- **Per-project storage memory.** `data/memory/{projectKey}.db` для user-level "ты senior dev в Go" — дублирование или per-project copy которые расходятся. User-level memory должен быть workspace-level или global. Текущая схема не оставляет места.
- **4-типовая schema (user/feedback/project/reference).** Это buckets из claude-code memory. Other agents этого деления не имеют. Если строить общий store — типы должны быть либо tags (свободные), либо вообще выкинуть и оставить markdown+frontmatter.
- **SessionPlan natural key `(workspaceKey, projectKey, agent, slug)`.** Slug автогенерируется claude-code из user prompt. Два claude-code instance на одной задаче генерят один slug → upsert race. Добавится "новая ревизия", и пользователь увидит **смешанные edits с двух машин**. Это не collision, это data corruption.
- **Дублирование везде:** yobabox.db ← MCP ← agent ← локальный mirror ← git. Четыре места где живёт plan. SoT неопределён. Bug: какой выигрывает при конфликте.

## 4. Границы применения

- **>100 session-plan'ов:** UI tree с фильтрами надо. Сейчас не планируется. Через 6 месяцев — dump.
- **Concurrent agent edits на одном plan'е:** read-modify-write race. Optimistic concurrency не упомянут.
- **1 проект (yobabox):** текущий случай. Per-project БД — overkill, нужна одна таблица.
- **Offline / плохая сеть:** агент висит на MCP call, или fail silently, или агент пишет в локальный файл и забывает синхронизировать. Все три исхода плохие, и не дизайнились.

## 5. Multi-machine

Каждый из этих сценариев — реальная дыра в текущем дизайне:
- Skill при старте сессии перезаписывает plan.md с серверной версии → laptop'ом отредактировал plan.md локально, не успел зафиксировать через MCP → утром на desktop'е стартуешь сессию → laptop'ный edit ушёл.
- SessionPlan slug collision: реален как описано выше.
- Memory upsert: last-write-wins без даже vector clock. Два agent'а одновременно правят одну `feedback-explicit-over-implicit` memory → один перезаписан.
- Локальный mirror plan.md в git: оба коммита на двух машинах с разным rendered content → merge conflict который **markdown не парсится назад** → resolve невозможен, `node.status` будет неконсистентен.

## 6. Альтернативы (по убыванию ценности)

1. **Ничего не делать.** plan.md в git. Stop-hook коммитит. Conflicts решает git. Memory — Claude-code per-repo, opencode per-config, потому что они и так разные.
2. **Stop-hook + git auto-commit** на правки `doc/plan.md`, `~/.claude/plans/*.md`, memory dir. Commit message с slug. Всё. **Это 50 строк bash, не два C# модуля.**
3. **mcp-server-memory (готовый) + git** для plan'ов. 0 строк собственного кода.
4. **Если очень хочется свой store** — один модуль `Notes`: один тип сущности (markdown+frontmatter+tags), один эндпоинт upsert/get/list, версионирование через git, не через таблицу. Без enum'ов статусов, без дерева, без типов memory.
5. **GitHub Issues** через gh CLI с MCP-обёрткой. Уже tree через sub-issues, уже history, уже UI, уже бесплатно.

## 7. Что сделать ПЕРЕД кодом

- **Эксперимент**: настроить Stop-hook на claude-code, который вызывает hypothetical `tasks.session_save` (mock — просто `echo` в файл). Поработать 2 недели. Посчитать: сколько раз агент **сам** позвал tool vs сколько правок plan'а прошло мимо. Если compliance <70%, дизайн не работает — агент не будет вызывать MCP-tool.
- **Bench собрать честно**: попросить claude-code, droid, opencode, pi выполнить одну и ту же задачу. Сравнить artifacts. Тогда видно — нужна ли унификация, или у всех уже git'абельный markdown.
- **Список реальных queries** к Tasks: какие вопросы задаются plan'у сегодня? "Что не done?", "что изменилось за неделю?", "какой commit закрыл задачу X?" Все три отвечает git+grep. Если ничего сверх — модуль не нужен.
- **Workflow с двух машин** руками раз: на laptop править, на desktop pull, посмотреть что ломается. До модуля, не после.

## 8. Прочее

- `Phase 29: dogfooding` через 5 фаз вперёд — red flag. Если ты не используешь tool сам с самого начала, ты дизайнишь вслепую. Должно быть `Phase 24.1`.
- Auth scope `tasks:write` на свою же plan-историю при том что единственный user — overengineering. Текущий cookie-auth + admin scope покрывает.
- `data/tasks/{projectKey}.db` per-project — сейчас 1-2 проекта. SQLite-файл на проект для plan'а который занимает 20KB — карго-культ изоляции из `Log.Core`.
- В `doc/tasks-mcp/README.md` написано "anonymized fixtures for tests" — fixtures для модуля который ещё не существует, и при этом сам же compliance к записи требований не выдерживается. Бенчей будет ~10, потом ты бросишь, и тесты модуля будут на 10 записей собственного journal'а одного агента за один день.

## Verdict

**Не строить как сейчас.** В лучшем случае — серьёзно переосмыслить, в честном — **не делать вообще**.

Pain решается hook'ом на 50 строк + git. Tasks/Memory модули — это два месяца работы на инфраструктуру, которая дублирует git, дублирует существующие MCP-серверы, и которую агенты будут игнорировать ровно так же как игнорируют `doc/tasks-mcp/` запись (собственный бенч это доказывает).

Если хочется построить — построить **минимальный Notes-модуль** (один тип, markdown+tags+upsert, git как history), использовать сам 2 недели, и только потом решать нужны ли PlanNode-tree, типы memory, junction-таблицы. Сейчас пишется schema для use-case'ов которые не валидированы даже на собственном workflow.

Ключевые файлы как опорные точки: `doc/plan.md` (902 строки — пример что в дереве станет адом), `doc/tasks-mcp/README.md` (бенч который сам говорит о низком compliance), `AGENTS.md:28-36` (правило про tasks-mcp records — то самое которое игнорируется).
