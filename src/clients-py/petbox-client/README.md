# petbox-client (Python)

Sync, zero-dependency Python SDK for [PetBox](https://github.com/stdray/petbox) config
**and the Data module**. The config client fetches resolved config from `/v1/conf` with
ETag-aware background polling (all four response templates — `flat`, `dotnet`, `envvar`,
`envvar-deep`); the Data client runs raw parameterized SQL (`query`/`exec`) and provisions
databases (`create_db`/`apply_schema`).

This is the Python analog of the TypeScript SDK `@stdray-npm/petbox-client`. The Data client
has landed; only the Log surface lands later, matching the TS roadmap.

## Install

```sh
pip install petbox-client
```

## Usage

```python
import os
from petbox_client import PetBoxConfigClient, PetBoxConfigClientOptions

client = PetBoxConfigClient(PetBoxConfigClientOptions(
    endpoint="https://petbox.3po.su",
    api_key=os.environ["PETBOX_API_KEY"],
    tags={"env": "prod", "project": "kpvotes"},
))

# Initial fetch + start background polling.
config = client.start()
print(config.get("db.host"))
print(config.get_number("db.port"))
print(config.get_bool("features.cache"))

# React to live config changes.
client.on("change", lambda cfg: print("config updated", cfg.data))
client.on("error", lambda err: print("poll failed", err))

# Stop polling when done (or use the client as a context manager).
client.dispose()
```

One-shot fetch without keeping an instance:

```python
from petbox_client import fetch_config, PetBoxConfigClientOptions

config = fetch_config(PetBoxConfigClientOptions(
    endpoint="https://petbox.3po.su",
    api_key="...",
    tags={"env": "prod"},
    refresh_interval=0,  # disable polling
))
```

## Data module

Raw parameterized SQL over HTTP, plus DataDb provisioning. The server is a pass-through — it
just runs your SQL against the project's SQLite file.

```python
import os
from petbox_client import PetBoxDataClient, PetBoxSqlParam

data = PetBoxDataClient(
    endpoint="https://petbox.3po.su",
    api_key=os.environ["PETBOX_API_KEY"],
)

data.create_db("kpvotes", "cache")
data.exec(
    "kpvotes", "cache",
    "INSERT INTO votes (id, film) VALUES (@id, @film)",
    [PetBoxSqlParam("@id", 1), PetBoxSqlParam("@film", "Matrix")],
)
rows = data.query("kpvotes", "cache", "SELECT * FROM votes")
```

## API

- `PetBoxConfigClient(options)` — `fetch()`, `start()`, `stop()`, `dispose()`,
  `on(event, listener)` / `off(...)` for `"change"` and `"error"`, `current` property.
- `ResolvedConfig` — `get(path)`, `get_number(path)`, `get_bool(path)`, `get_json(path)`,
  `to_env()`, `data`, `etag`. For the `flat` template `get("db.host")` traverses the nested
  tree; for the other templates it does a direct key lookup.
- `fetch_config(options)` — one-shot convenience.
- `PetBoxConfigError` — structured API error with `status` and `body`.
- `PetBoxDataClient(*, endpoint, api_key, transport=None)` — Data-module client. Methods:
  `query(project_key, db_name, sql, params=None, timeout_seconds=None)` → list of row dicts;
  `exec(project_key, db_name, sql, params=None, timeout_seconds=None)` → affected row count;
  `create_db(project_key, name, description=None, max_page_count=None)`;
  `apply_schema(project_key, db_name, migration_name, sql)`.
- `PetBoxSqlParam(name, value, db_type=None)` — one parameter for a parameterized query/exec.
- `PetBoxDataError` — structured Data API error with `status` and `body`.

## License

MIT
