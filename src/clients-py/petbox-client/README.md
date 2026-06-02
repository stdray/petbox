# petbox-client (Python)

Sync, zero-dependency Python SDK for [PetBox](https://github.com/stdray/petbox) config.
Fetches resolved config from `/v1/conf` with ETag-aware background polling. Supports all
four response templates (`flat`, `dotnet`, `envvar`, `envvar-deep`).

This is the Python analog of the TypeScript SDK `@stdray-npm/petbox-client`. Data + Log
surfaces land later, matching the TS roadmap.

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

## API

- `PetBoxConfigClient(options)` — `fetch()`, `start()`, `stop()`, `dispose()`,
  `on(event, listener)` / `off(...)` for `"change"` and `"error"`, `current` property.
- `ResolvedConfig` — `get(path)`, `get_number(path)`, `get_bool(path)`, `get_json(path)`,
  `to_env()`, `data`, `etag`. For the `flat` template `get("db.host")` traverses the nested
  tree; for the other templates it does a direct key lookup.
- `fetch_config(options)` — one-shot convenience.
- `PetBoxConfigError` — structured API error with `status` and `body`.

## License

MIT
