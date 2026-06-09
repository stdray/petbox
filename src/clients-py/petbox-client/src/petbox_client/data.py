"""Sync PetBox Data-module client — raw parameterized SQL over HTTP (zero runtime deps).

Mirrors the .NET PetBoxDataClient and the TypeScript one. The server is a dumb pass-through:
it knows nothing about your types, it just runs the SQL against the project's SQLite file.

Example::

    data = PetBoxDataClient(endpoint="https://petbox.3po.su", api_key=os.environ["PETBOX_API_KEY"])
    data.create_db("kpvotes", "cache")
    data.exec(
        "kpvotes", "cache",
        "INSERT INTO votes (id, film) VALUES (@id, @film)",
        [PetBoxSqlParam("@id", 1), PetBoxSqlParam("@film", "Matrix")],
    )
    rows = data.query("kpvotes", "cache", "SELECT * FROM votes")
"""

from __future__ import annotations

import json
import urllib.error
import urllib.request
from collections.abc import Callable, Mapping, Sequence
from dataclasses import dataclass
from typing import Any
from urllib.parse import quote

from .types import HttpResponse

_API_KEY_HEADER = "X-Api-Key"

# A POST-capable transport: (method, url, headers, body) -> HttpResponse.
# The default uses urllib; tests inject a fake to avoid the network.
DataTransport = Callable[[str, str, Mapping[str, str], bytes | None], HttpResponse]


@dataclass(frozen=True)
class PetBoxSqlParam:
    """A single parameter for a parameterized query/exec.

    ``name`` matches the SQL placeholder (e.g. "@id"); ``value`` is any JSON-serializable
    value (None → SQL NULL); ``db_type`` is an optional explicit type hint.
    """

    name: str
    value: Any
    db_type: str | None = None


class PetBoxDataError(Exception):
    """Structured error from a PetBox Data API call (non-2xx, or transport failure)."""

    def __init__(self, message: str, status: int, body: object) -> None:
        super().__init__(message)
        self.status = status
        self.body = body


def _default_transport(
    method: str, url: str, headers: Mapping[str, str], body: bytes | None
) -> HttpResponse:
    request = urllib.request.Request(url, data=body, headers=dict(headers), method=method)  # noqa: S310 - https URL from configured endpoint
    try:
        with urllib.request.urlopen(request) as response:  # noqa: S310 - https URL from configured endpoint
            return HttpResponse(
                response.status, dict(response.headers.items()), response.read().decode("utf-8")
            )
    except urllib.error.HTTPError as http_error:
        text = http_error.read().decode("utf-8") if http_error.fp is not None else ""
        return HttpResponse(http_error.code, dict(http_error.headers.items()), text)


class PetBoxDataClient:
    """Sync client for the PetBox Data module — query/exec plus DataDb provisioning."""

    def __init__(
        self, *, endpoint: str, api_key: str, transport: DataTransport | None = None
    ) -> None:
        if not endpoint:
            raise ValueError("endpoint is required")
        if not api_key:
            raise ValueError("api_key is required")
        self._base = endpoint if endpoint.endswith("/") else endpoint + "/"
        self._api_key = api_key
        self._transport: DataTransport = transport or _default_transport

    def query(
        self,
        project_key: str,
        db_name: str,
        sql: str,
        params: Sequence[PetBoxSqlParam] | None = None,
        timeout_seconds: int | None = None,
    ) -> list[dict[str, Any]]:
        """Run a SELECT and return the rows as dicts keyed by column name."""
        resp = self._post(
            f"api/data/{quote(project_key)}/{quote(db_name)}/query",
            {"sql": sql, "params": _params(params)},
            timeout_seconds,
        )
        body = self._read_json(resp)
        return body if isinstance(body, list) else []

    def exec(
        self,
        project_key: str,
        db_name: str,
        sql: str,
        params: Sequence[PetBoxSqlParam] | None = None,
        timeout_seconds: int | None = None,
    ) -> int:
        """Run an INSERT/UPDATE/DELETE/DDL/PRAGMA and return the affected row count."""
        resp = self._post(
            f"api/data/{quote(project_key)}/{quote(db_name)}/exec",
            {"sql": sql, "params": _params(params)},
            timeout_seconds,
        )
        body = self._read_json(resp)
        return int(body["affected"]) if isinstance(body, dict) and "affected" in body else 0

    def create_db(
        self,
        project_key: str,
        name: str,
        description: str | None = None,
        max_page_count: int | None = None,
    ) -> None:
        """Create a DataDb. ``max_page_count`` caps file size (pages × 4KB);
        None uses the server default."""
        payload: dict[str, Any] = {"name": name}
        if description is not None:
            payload["description"] = description
        if max_page_count is not None:
            payload["maxPageCount"] = max_page_count
        self._ensure_ok(self._post(f"api/data/{quote(project_key)}/dbs", payload, None))

    def apply_schema(self, project_key: str, db_name: str, migration_name: str, sql: str) -> None:
        """Apply a named migration. Idempotent: same name+sql is a no-op;
        same name with different sql is a 409."""
        self._ensure_ok(
            self._post(
                f"api/data/{quote(project_key)}/{quote(db_name)}/schema",
                {"name": migration_name, "sql": sql},
                None,
            )
        )

    # ── internals ──────────────────────────────────────────

    def _post(
        self, path: str, payload: Mapping[str, Any], timeout_seconds: int | None
    ) -> HttpResponse:
        headers: dict[str, str] = {
            _API_KEY_HEADER: self._api_key,
            "Content-Type": "application/json",
        }
        if timeout_seconds is not None:
            headers["X-PetBox-Timeout-Seconds"] = str(timeout_seconds)
        data = json.dumps(payload).encode("utf-8")
        url = self._base + path
        try:
            return self._transport("POST", url, headers, data)
        except Exception as cause:
            raise PetBoxDataError(f"Failed to reach PetBox at {url}: {cause}", 0, None) from cause

    def _read_json(self, resp: HttpResponse) -> Any:
        self._ensure_ok(resp)
        return json.loads(resp.body) if resp.body else None

    @staticmethod
    def _ensure_ok(resp: HttpResponse) -> None:
        if 200 <= resp.status < 300:
            return
        parsed: Any = resp.body
        try:
            parsed = json.loads(resp.body)
        except ValueError:
            pass
        reason = f": {parsed['error']}" if isinstance(parsed, dict) and "error" in parsed else ""
        raise PetBoxDataError(
            f"PetBox request failed: HTTP {resp.status}{reason}", resp.status, parsed
        )


def _params(params: Sequence[PetBoxSqlParam] | None) -> list[dict[str, Any]]:
    if not params:
        return []
    result: list[dict[str, Any]] = []
    for p in params:
        item: dict[str, Any] = {"name": p.name, "value": p.value}
        if p.db_type is not None:
            item["dbType"] = p.db_type
        result.append(item)
    return result
