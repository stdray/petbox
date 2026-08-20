# DuckDB — качество сообщений об ошибках и CREATE MACRO ... AS TABLE — 2026-08-20

Закрывает открытый пункт `20-consolidated.md` («ошибки kuzu/DuckDB PGQ не проверены»). Замерено
самостоятельно, не переписано из чужой переписки: `duckdb.exe v1.5.5 (Variegata)` (WinGet, путь
`DuckDB.cli_*`), CLI, по `research/2026-08-roslyn-code-graph/measurements/callgraph/call-edges.parquet`
(165 681 строка, колонки `caller,callee,kind,reason,via,file,line,tests,gen,ext,constructed`).

## Что проверено

| Что | Команда | Результат |
|---|---|---|
| Неизвестная колонка в `SELECT` по `read_parquet(...)` | `SELECT calee FROM read_parquet('call-edges.parquet');` | `Binder Error: Referenced column "calee" not found in FROM clause!` + `Candidate bindings: "callee", "caller", "file", "via"` + `LINE 1: ...` с кареткой `^` под именем |
| То же, ближний тайпо | `SELECT kinf FROM ...` | та же форма: кандидаты `"kind", "line", "gen", "via", "file"`, каретка на месте |
| Неизвестная таблица, похожих нет | `SELECT * FROM call_eges;` (до `CREATE TABLE`) | `Catalog Error: ... does not exist! Did you mean "duckdb_indexes"?` — предлагает системную таблицу, не по делу |
| Неизвестная таблица, похожая существует | после `CREATE TABLE call_edges AS SELECT * FROM read_parquet(...)`, `SELECT * FROM call_eges;` | `Did you mean "call_edges"?` — точное попадание |
| `CREATE MACRO ... AS TABLE` с параметром | `CREATE MACRO callers_of(target) AS TABLE SELECT caller, kind FROM call_edges WHERE callee = target;` затем `SELECT count(*) FROM callers_of('M:System.Exception.#ctor(System.String)');` | работает, `n = 2` — реальные строки на реальных данных, не пустой каркас |
| Ошибка внутри тела макроса | `CREATE MACRO bad_macro(target) AS TABLE SELECT calee FROM call_edges WHERE callee = target;` → `SELECT * FROM bad_macro('x');` | та же binder-ошибка с кандидатами всплывает из тела макроса без деградации |

Полный протокол команд и вывод — в сессии выше (воспроизводимо: тот же parquet, та же версия).

## Сравнение с зафиксированными числами (не перемерено, только ссылка)

- **CQLinq** (`13-ndepend-baseline.md:63`, `30-decisions.md:145`): на ошибке — `Incomplete query`
  без имени члена и без позиции; по `30-decisions.md` этот класс ошибок стоил исполнителю шести
  попыток.
- **Kusto.Language / KustoLoco.Core** (`12-query-language.md:101`): `KS142`-класс, имя и позиция
  (у KustoLoco — разметкой прямо в тексте запроса), БЕЗ did-you-mean даже на близкий тайпо
  (`Levl`→`Level`).

## Вывод

DuckDB на несуществующей колонке даёт **больше**, чем Kusto.Language: не только имя+позиция+каретка,
но и список кандидатов-подсказок (did-you-mean из коробки, без доклейки слоя). На несуществующей
таблице did-you-mean тоже есть, но не всегда по делу — без похожих кандидатов подсказывает
системную таблицу (`duckdb_indexes`), что может сбить с толку сильнее, чем отсутствие подсказки.
`CREATE MACRO ... AS TABLE` работает на реальных данных и параметрах, включая корректную
пропагацию той же качественной ошибки из тела макроса — основа для замены закрытого DSL
библиотекой макросов подтверждена эмпирически, а не только «по конструкции».

**Пункт `20-consolidated.md` закрыт для DuckDB (не для kuzu/PGQ — Cypher-путь не проверялся и
здесь не проверен, риск для него остаётся гипотезой).**
