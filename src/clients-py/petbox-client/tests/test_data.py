"""Unit tests for PetBoxDataClient using an injected stub transport (no network)."""

from __future__ import annotations

import json
from collections.abc import Mapping

import pytest

from petbox_client import PetBoxDataClient, PetBoxDataError, PetBoxSqlParam
from petbox_client.types import HttpResponse


class _StubTransport:
    """Captures the request and returns a canned response."""

    def __init__(self, status: int, body: str) -> None:
        self._status = status
        self._body = body
        self.calls: list[tuple[str, str, dict[str, str], bytes | None]] = []

    def __call__(
        self, method: str, url: str, headers: Mapping[str, str], body: bytes | None
    ) -> HttpResponse:
        self.calls.append((method, url, dict(headers), body))
        return HttpResponse(self._status, {"Content-Type": "application/json"}, self._body)


def test_query_posts_with_api_key_and_parses_rows() -> None:
    stub = _StubTransport(200, '[{"id": 1, "film": "Matrix"}]')
    client = PetBoxDataClient(endpoint="https://petbox.test", api_key="k", transport=stub)

    rows = client.query(
        "kpvotes", "cache", "SELECT * FROM votes WHERE id = @id", [PetBoxSqlParam("@id", 1)]
    )

    method, url, headers, body = stub.calls[0]
    assert method == "POST"
    assert url == "https://petbox.test/api/data/kpvotes/cache/query"
    assert headers["X-Api-Key"] == "k"
    assert body is not None
    assert json.loads(body) == {
        "sql": "SELECT * FROM votes WHERE id = @id",
        "params": [{"name": "@id", "value": 1}],
    }
    assert rows == [{"id": 1, "film": "Matrix"}]


def test_exec_returns_affected() -> None:
    stub = _StubTransport(200, '{"affected": 3}')
    client = PetBoxDataClient(endpoint="https://petbox.test", api_key="k", transport=stub)

    affected = client.exec("p", "db", "DELETE FROM votes")

    assert stub.calls[0][1] == "https://petbox.test/api/data/p/db/exec"
    assert affected == 3


def test_create_db_posts_name_only_when_others_omitted() -> None:
    stub = _StubTransport(201, "{}")
    client = PetBoxDataClient(endpoint="https://petbox.test", api_key="k", transport=stub)

    client.create_db("p", "cache", max_page_count=262144)

    _, url, _, body = stub.calls[0]
    assert url == "https://petbox.test/api/data/p/dbs"
    assert body is not None
    assert json.loads(body) == {"name": "cache", "maxPageCount": 262144}


def test_non_2xx_raises_with_status_and_body() -> None:
    stub = _StubTransport(404, '{"error": "DataDb not found"}')
    client = PetBoxDataClient(endpoint="https://petbox.test", api_key="k", transport=stub)

    with pytest.raises(PetBoxDataError) as excinfo:
        client.query("p", "nope", "SELECT 1")

    assert excinfo.value.status == 404
    assert excinfo.value.body == {"error": "DataDb not found"}
