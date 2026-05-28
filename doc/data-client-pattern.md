# Data module — pet-side client pattern

Reference doc for pets migrating from local storage to petbox's Data module
(Phase 16 Wave 3). PetBox itself doesn't implement the client — this is what
each pet needs to add to its own repo.

**Strategic note**: once Wave 4 (MCP server) is up, an agentic session can do
this setup for a pet using petbox MCP tools — `data.create_db`,
`data.schema_apply`, plus the file edits to the pet's source. This doc is the
reference for *what* the agent needs to produce; if you're a human doing
manual integration, follow it directly.

Current pet target: **kpvotes-ts** (TypeScript). The .NET version is being
phased out. Lead with TS; C# section kept for completeness.

## Model

Pet writes its own POCO classes locally, uses linq2db normally to build
queries. Right before execution, the pet extracts compiled parameterized SQL +
parameters from the IQueryable, ships them over HTTP to petbox, and petbox
runs them against a SQLite file on its side.

```
[pet] linq2db local              [petbox]
   q.GetSqlQueries()                ADO.NET parameterized
   ↓                                ↑
   { sql, params } ─── HTTP ───→ DataConnection.Execute
   ↓                                ↓
   materialize rows ←── JSON ──── result rows
```

Server knows nothing about pet's POCOs — pure pass-through.

## Setup (one-time)

```bash
# Create the DataDb (one DataDb per logical store; pet can have several).
curl -X POST "$PETBOX_URL/api/data/$PROJECT/dbs" \
  -H "X-Api-Key: $PETBOX_APIKEY" \
  -H "Content-Type: application/json" \
  -d '{"name":"cache","description":"vote cache","maxPageCount":262144}'

# Apply each existing migration (one POST per script).
curl -X POST "$PETBOX_URL/api/data/$PROJECT/cache/schema" \
  -H "X-Api-Key: $PETBOX_APIKEY" \
  -H "Content-Type: application/json" \
  -d '{"name":"M001_create_votes","sql":"CREATE TABLE votes (id INTEGER PRIMARY KEY, film TEXT NOT NULL)"}'
```

Idempotent: re-running with the same name+sql is a 200 no-op; same name with
different sql is 409.

## Pet-side thin client (C# / linq2db, ~30 LOC)

```csharp
using System.Net.Http.Json;
using LinqToDB;
using LinqToDB.Data;

public sealed class YobaDataClient(HttpClient http, string projectKey, string dbName)
{
    public async Task<List<T>> QueryAsync<T>(IQueryable<T> q, CancellationToken ct = default)
        where T : class, new()
    {
        var qs = q.ToSqlQuery();  // LinqToDB.LinqExtensions, multi-statement use GetSqlQueries
        var dto = new
        {
            sql = qs.Sql,
            @params = qs.Parameters.Select(p => new { name = p.Name, value = p.Value }).ToArray(),
        };
        var resp = await http.PostAsJsonAsync($"/api/data/{projectKey}/{dbName}/query", dto, ct);
        resp.EnsureSuccessStatusCode();
        var rows = await resp.Content.ReadFromJsonAsync<List<Dictionary<string, object?>>>(cancellationToken: ct);
        return rows!.Select(MapRow<T>).ToList();
    }

    public async Task<int> ExecAsync(string sql, object? @params, CancellationToken ct = default)
    {
        var dto = new
        {
            sql,
            @params = @params is null ? Array.Empty<object>() : ToParams(@params),
        };
        var resp = await http.PostAsJsonAsync($"/api/data/{projectKey}/{dbName}/exec", dto, ct);
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<ExecResp>(cancellationToken: ct);
        return body!.Affected;
    }

    static T MapRow<T>(Dictionary<string, object?> row) where T : new()
    {
        // For pet using linq2db on both ends, DataConnection.QueryToList<T>(sql, params)
        // is the natural materializer — see linq2db docs. For a fully manual map:
        var inst = new T();
        foreach (var prop in typeof(T).GetProperties())
            if (row.TryGetValue(prop.Name, out var v) && v is not null)
                prop.SetValue(inst, Convert.ChangeType(v, prop.PropertyType));
        return inst;
    }

    static object[] ToParams(object obj) => obj.GetType().GetProperties()
        .Select(p => (object)new { name = "@" + p.Name, value = p.GetValue(obj) })
        .ToArray();

    sealed record ExecResp(int Affected);
}
```

## Usage in pet code

```csharp
// Setup
var http = new HttpClient { BaseAddress = new Uri(config["PetBox:Url"]) };
http.DefaultRequestHeaders.Add("X-Api-Key", config["PetBox:ApiKey"]);
var data = new YobaDataClient(http, "kpvotes", "cache");

// linq2db query — define POCO + a fake DataConnection just for query building
using var dc = new DataConnection(new DataOptions().UseSQLite(":memory:"));
var q = dc.GetTable<Vote>().Where(v => v.Film == "Matrix").OrderBy(v => v.Id);
var rows = await data.QueryAsync(q);

// Write
await data.ExecAsync(
    "INSERT INTO votes (id, film, score) VALUES (@id, @film, @score)",
    new { Id = 1, Film = "Matrix", Score = 8.7 });
```

## Gotchas

1. **Captured locals, not literals.** linq2db inlines literal constants
   into the SQL — `Where(v => v.Id == 1)` produces `WHERE Id = 1` (no
   parameter). Always use captured variables: `var id = 1; Where(v => v.Id == id)`
   → `WHERE Id = @p`. Otherwise the SQL grows unbounded and skew gets cached
   poorly.

2. **No multi-statement transactions.** PetBox doesn't expose `BEGIN`/`COMMIT`
   across HTTP calls. If you need atomicity, restructure the pet logic into
   idempotent UPSERTs (`INSERT OR REPLACE`, `ON CONFLICT DO UPDATE`) or move
   the multi-step logic into a single SQL statement.

3. **PRAGMA via /exec is denylisted for a few specific cases**
   (`writable_schema`, `temp_store_directory`, `data_store_directory`,
   `trusted_schema`). Everything else passes through.

4. **CommandTimeout default 30s.** Long-running queries: send
   `X-PetBox-Timeout-Seconds: 120` header (capped at 300).

5. **Body limits:** `/query` 1 MB, `/exec` 10 MB. For larger blob writes,
   chunk client-side.

6. **`ToSqlQuery` truncates to single statement.** If you need multi-statement
   support (e.g., `CreateTable`, `InsertOrReplace` patterns that emit multiple
   commands), use `((IExpressionQuery)q).GetSqlQueries(null)` which returns
   `IReadOnlyList<QuerySql>` and POST each statement (or extend the wrapper to
   ship `{ statements: [...] }` and call `/exec` per item server-side).

## TypeScript pets (Knex / Drizzle / Kysely)

Equivalent pattern in TS — extract via `query.toSQL()` which returns
`{ sql, bindings }`, POST to the same endpoint:

```typescript
const { sql, bindings } = knex('votes').where('film', 'Matrix').toSQL();
const res = await fetch(`${PETBOX_URL}/api/data/kpvotes/cache/query`, {
  method: 'POST',
  headers: { 'X-Api-Key': PETBOX_APIKEY, 'Content-Type': 'application/json' },
  body: JSON.stringify({ sql, params: bindings.map((v, i) => ({ name: `@p${i}`, value: v })) }),
});
const rows = await res.json();
```

(Adjust binding-name convention to match your ORM's parameter style — Knex
uses positional `?`, Drizzle named, Kysely named. PetBox accepts whatever
name the SQL uses.)
