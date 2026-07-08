# @stdray-npm/petbox-client (TypeScript)

Zero-dependency TypeScript SDK for [PetBox](https://github.com/stdray/petbox) config **and the
Data module**. The config client fetches resolved config from `/v1/conf` with ETag-aware
background polling (all four response templates — `flat`, `dotnet`, `envvar`, `envvar-deep`);
the Data client runs raw parameterized SQL (`query`/`exec`) and provisions databases
(`createDb`/`applySchema`).

This is the TypeScript analog of the Python SDK `petbox-client`. The Data client has landed;
only the Log surface lands later.

## Install

```sh
npm i @stdray-npm/petbox-client
```

## Usage

```ts
import { PetBoxConfigClient } from "@stdray-npm/petbox-client";

const client = new PetBoxConfigClient({
  endpoint: "https://petbox.3po.su",
  apiKey: process.env.PETBOX_API_KEY!,
  tags: { env: "prod", project: "kpvotes" },
});

// Initial fetch + start background polling.
const config = await client.start();
console.log(config.get("db.host"));
console.log(config.getNumber("db.port"));
console.log(config.getBoolean("features.cache"));

// React to live config changes.
client.on("change", (cfg) => console.log("config updated", cfg.data));
client.on("error", (err) => console.log("poll failed", err));

// Stop polling when done.
client.dispose();
```

One-shot fetch without keeping an instance:

```ts
import { fetchConfig } from "@stdray-npm/petbox-client";

const config = await fetchConfig({
  endpoint: "https://petbox.3po.su",
  apiKey: "...",
  tags: { env: "prod" },
  refreshIntervalMs: 0, // disable polling
});
```

## Data module

Raw parameterized SQL over HTTP, plus DataDb provisioning. The server is a pass-through — it
just runs your SQL against the project's SQLite file.

```ts
import { PetBoxDataClient } from "@stdray-npm/petbox-client";

const data = new PetBoxDataClient({
  endpoint: "https://petbox.3po.su",
  apiKey: process.env.PETBOX_API_KEY!,
});

await data.createDb("kpvotes", "cache");
await data.exec(
  "kpvotes",
  "cache",
  "INSERT INTO votes (id, film) VALUES (@id, @film)",
  [{ name: "@id", value: 1 }, { name: "@film", value: "Matrix" }],
);
const rows = await data.query<{ id: number; film: string }>(
  "kpvotes",
  "cache",
  "SELECT * FROM votes",
);
```

## API

- `PetBoxConfigClient(options)` — `fetch()`, `start()`, `stop()`, `dispose()`, `current`
  getter, and `on(event, listener)` / `off(...)` for `"change"` and `"error"`. Options:
  `endpoint`, `apiKey`, `tags` (at least one required), and optional `template`,
  `refreshIntervalMs` (0 disables polling), `optional`, `cacheDir` (last-known-good disk
  cache, Node only), `timeoutMs`, `fetchImpl`.
- `ResolvedConfig` — `get(path)`, `getNumber(path)`, `getBoolean(path)`, `getJson<T>(path)`,
  `toEnv()`, `data`, `etag`. For the `flat` template `get("db.host")` traverses the nested
  tree; for the other templates it does a direct key lookup.
- `fetchConfig(options)` — one-shot convenience (no polling).
- `PetBoxConfigError` — structured config API error with `status` and `body`.
- `PetBoxDataClient({ endpoint, apiKey, fetchImpl? })` — Data-module client. Methods:
  `query<T>(projectKey, dbName, sql, params?, opts?)` → `T[]`;
  `exec(projectKey, dbName, sql, params?, opts?)` → affected row count;
  `createDb(projectKey, name, { description?, maxPageCount? }?)`;
  `applySchema(projectKey, dbName, migrationName, sql)`. `params` is a `PetBoxSqlParam[]`;
  `opts.timeoutSeconds` sets the server-side command timeout (capped at 300).
- `PetBoxSqlParam` — `{ name, value, dbType? }`; `name` matches the SQL placeholder (e.g. `@id`).
- `PetBoxError` — structured Data API error with `status` and `body`.

## License

MIT
