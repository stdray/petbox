# 14 — tools/session-usage: измерительный инструмент. Инвентаризация. 2026-09-10
Автор: petbox-worker · источники: код (`origin/main`, merge `262d191a`), доска `work`/`ideas`

## Что здесь верно сейчас

### Три плеча — что каждое читает, что отдаёт
- **Claude Code**: `~/.claude/projects/**/*.jsonl` (root) + `<session>/subagents/
  agent-*.jsonl`+`.meta.json` (`agentType`). Подкоманды `summary`/`roles`/`money`.
  [код `archive.py:1-159`, `session_usage.py:24-56`]
- **opencode**: SQLite `~/.local/share/opencode/opencode.db` read-only
  (`file:...?mode=ro`), атрибуция из строк `message` (не `session` — 65/195 сессий
  там NULL). Подкоманды `oc-roles`/`oc-money`/`oc-tree`/`reconcile`.
  [код `opencode_store.py:14-25`]
- **qwen**: `~/.qwen/projects/<slug>/chats/*.jsonl` (root) + `subagents/<parent>/
  agent-<role>-call_*.jsonl`+`.meta.json`. Подкоманды `q-roles`/`q-money`.
  [код `qwen_store.py:1-16`]
- Три формата НЕ смешиваются в одном флаге — явное решение [код `:14-22`].

### Три арифметики корзин — где именно в коде
- **Claude**: 4 бакета (`input`/`output`/`cache_read`/`cache_creation`) ВСЕ
  дизъюнктны, стоимость = сумма всех по своим ставкам. `_bucket_cost()`
  [код `session_usage.py:315-321`, бакеты `archive.py:19-24`].
- **qwen**: `input = max(prompt-cached,0)` (cached — подмножество prompt),
  `output = candidates` (thoughts — подмножество candidates, НЕ добавляется).
  Инвариант `total == prompt+candidates` держится на 404 ходах, 0 нарушений.
  [код `_extract_usage()` `qwen_store.py:153-166`, докстринг п.1-2 `:20-40`]
- **opencode**: `reasoning` — самостоятельный бакет, ДОБАВЛЯЕТСЯ к output только
  в формуле цены: `(output+reasoning)*price.output`. Без reasoning формула даёт
  96.7% (ratio 1.0327) recorded cost на 164 turns glm-5.3-flash; с ним — 1.000.
  [код `bucket_cost()` `opencode_store.py:286-299`, эмпирика `:52-61`]
- **Наивный кросс-порт сломается на**: суммировании qwen `cache_read` поверх
  `input` как в Claude (задвоит cache); НЕ-добавлении opencode `reasoning`
  к output (недосчитает 3.3%); прибавлении qwen `thoughts` к output (задвоит).
  Все три предупреждены докстрингами, не выведены агентом заново.

### Счётные ловушки README — 18 штук, все в коде
README enumерует **18 gotcha** (1-6 общие/Claude, 7-12 opencode, 13-18 qwen)
[`README.md:91-316`], каждая с код-якорем: дедуп по `message.id` keep-last
(`archive.parse_transcript`); исключение `subagents/` из root-walk (фильтр
`os.sep+"subagents"+os.sep`); запрет `subagent_tokens` из spawn-notification
(= `cache_creation`, наименьший бакет); атрибуция по `message.model`/`.data`,
не по ростеру/`session`-колонкам; local-vs-UTC граница дня (`--tz`); root-only
TUI "spent"; `quota_multiplier` — **транскрибируется вручную** из текста
`opencode models --verbose` [код `opencode_store.py:63-76`]; wallet-префиксы
`ds-`/`go-` с fallback-стриппингом суффикса ТОЛЬКО после прямого промаха [код
`qwen_store.py:41-51`]; `ui_telemetry` near-duplicate (5.4x turn count), не
суммируется с токенами. Все 18 закодированы, не остаётся на совести читающего.

## Где механизм на самом деле живёт
`archive.py:141-149` iter_root_sessions() фильтр subagents/ (двойной счёт 2.3-3x) ·
`qwen_store.py:153-166` _extract_usage(), вся qwen-арифметика в одной функции ·
`opencode_store.py:286-299` bucket_cost(), reasoning-в-output только для цены ·
`session_usage.py:315-321` _bucket_cost(), Claude, простое суммирование 4.

## Тесты/линт/CI
**Нет ни одного.** [код: `git ls-tree tools/session-usage/` — только `.py/.md/.json`,
нет `test_*`/`conftest.py`/`pytest.ini`; `ci.yml`/`inspect.yml` не упоминают
`session-usage`/`pytest`]. Подтверждено на доске: «no tests, no lint, no CI
whatsoever… verification rested on `py_compile`, live-archive runs, a hand-check
of bucket invariants» [доска `session-usage-tool-language-and-packaging`, raw].

**Минимальный доверяемый тест-сет** (арифметика бакетов — первая цель), ничего
не существует сейчас [код: отсутствие файлов]: fixture-транскрипт на плечо с
известной суммой (assert точных чисел, не диапазонов); qwen-инвариант
`total==prompt+candidates` как assert, не разовая ручная проверка на 404 ходах;
дедуп по `message.id` (keep-last) на дублированной строке; фильтр `subagents/`
на дереве с вложенным call-файлом; `resolve_price()` на `go-qwen3.8-max` (целый
id) vs `go-glm-5.3-flash-low` (id+суффикс); `quota_multiplier` ×2/×8 на КВОТНОЙ
цифре, не на recorded/recomputed base cost; local vs UTC граница дня в
`reconcile` на полуночь.

## Что инструмент может ответить про ПОВЕДЕНИЕ АГЕНТОВ (не только деньги)
- **Роли/звонки на роль**: `roles`/`q-roles` — per-role n_calls + sum/median/p90
  [код `session_usage.py:264-297`, `~753-776`].
- **Роли с нулевым трафиком**: ТОЛЬКО Claude, ТОЛЬКО роли из хардкода
  `KNOWN_ROLES` (6 слагов, `:66-73`) — печатает `n=0` явно [код `:289-292`]; роль
  вне списка без вызовов не появится нигде. qwen/opencode такого не делают.
- **Delegation shape**: НЕ отвечается архив-вайд ни одним плечом. `oc-tree`
  строит parent_id-дерево только для ОДНОЙ сессии по id/заголовку [код
  `:559-577`]. Claude/qwen графа звонков вообще не строят.
- **Calls per session**: opencode `oc-roles` считает по ассистент-ходу, не по
  сессии (12/195 сессий меняют agent, 4/195 — model внутри одной) [код+докстринг
  `opencode_store.py:26-35`, gotcha #9]. Claude/qwen — по call-файлу.
- **Latency**: ТОЛЬКО qwen (`duration_ms`/`ttft_ms`/`status_code`, root-сессии
  only, 0 строк в 23 subagent-call файлах, без разбивки по роли) [код
  `qwen_store.py:88-98`, `:695-790`]. Claude/opencode — НЕТ латентности вообще.

## Общая квота (shared-quota problem)
`opencode-go` — ОДНА подписка, тратят и opencode, и qwen (`go-*`). `q-money`
считает `go-*` против тех же `prices.json`, но НЕ сливает расход с `oc-money`'s
`opencode-go`-кошельком в одно окно — явно названный дефицит [код+докстринг
`qwen_store.py:83-92`, gotcha #17]. Любая цифра "окно квоты съедено" по одному
плечу — занижение. Открытая карточка `session-usage-quota-window-and-price-dating`
(`work`, Pending): окно лимитов квоты, датирование прайса, прогнозная таблица.

## Открытый вопрос владельца: язык/упаковка
Карточка-идея `session-usage-tool-language-and-packaging` (`ideas`, raw,
дочерняя `umbrella-agent-behaviour`) фиксирует вопрос и НЕ решает его: Python в
репо C#/TS — «логично жить в одном из языков репо»; альтернатива — модуль кита,
`petbox-wire analyze qwen`. Факты без выбора: появился как «temporary
measurement script»; три арифметики — «exactly the shape where an untested port
double-counts silently»; ноль зависимостей вне stdlib сейчас [код
`session_usage.py:56`], порт на C#/TS этот ноль теряет; карточка сама требует
учесть слияние окна квоты, не решать язык отдельно от него. [раздел выше, raw]

## Что НЕ проверено
- Живьём: ломается ли `q-roles` latency-секция без `ui_telemetry` вовсе (код
  читает `.get()`, похоже безопасно, но не прогнано). [НЕПРОВЕРЕНО]
- Есть ли способ собрать delegation-граф архив-вайд вне `oc-tree` (обход всех
  `parent_id`) — код так НЕ делает нигде сейчас. [НЕПРОВЕРЕНО]

## Противоречия
Нет — `02-snapshot.md` и код на `origin/main` сходятся по всем проверенным пунктам.

## Что отсюда следует
Инструмент честен о ловушках (18/18 закодированы), но непроверен: любая правка
любого из трёх модулей рискует молча сломать ровно ту арифметику, ради которой
существует докстринг. "Роли с нулевым трафиком" и "delegation shape" —
половинчато отвечаемые вопросы: первое ограничено хардкод-списком, второе не
отвечается архив-вайд вообще ни одним плечом.
