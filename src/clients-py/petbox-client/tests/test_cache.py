"""Tests for the opt-in disk last-known-good (LKG) cache.

A fake transport stands in for the network (as in test_client.py); ``tmp_path`` gives
each test an isolated cache file. Covers: cache write after 200, cache fallback when the
server is unreachable, no-cache behaviour, If-None-Match seeded from the cache + 304, and
corrupt-file tolerance.
"""

import json
from collections.abc import Mapping
from pathlib import Path

import pytest

from petbox_client import (
    HttpResponse,
    PetBoxConfigClient,
    PetBoxConfigClientOptions,
    PetBoxConfigError,
)


def make_transport(
    response: HttpResponse,
    capture: dict[str, object] | None = None,
):
    """Transport returning a fixed response, optionally capturing the request."""

    def transport(url: str, headers: Mapping[str, str]) -> HttpResponse:
        if capture is not None:
            capture["url"] = url
            capture["headers"] = dict(headers)
        return response

    return transport


def boom_transport(url: str, headers: Mapping[str, str]) -> HttpResponse:
    """Simulate an unreachable host."""
    raise OSError("connection refused")


def _options(cache_path: Path | None, **kwargs: object) -> PetBoxConfigClientOptions:
    return PetBoxConfigClientOptions(
        endpoint="https://petbox.test",
        api_key="key",
        tags={"env": "test"},
        refresh_interval=0,
        cache_path=cache_path,
        **kwargs,  # type: ignore[arg-type]
    )


class TestCacheWrite:
    def test_writes_cache_file_after_200(self, tmp_path: Path) -> None:
        cache = tmp_path / "conf.json"
        transport = make_transport(
            HttpResponse(200, {"ETag": '"abc"'}, json.dumps({"db": {"host": "localhost"}}))
        )
        client = PetBoxConfigClient(_options(cache, transport=transport))
        cfg = client.fetch()
        assert cfg.get("db.host") == "localhost"

        assert cache.exists()
        stored = json.loads(cache.read_text(encoding="utf-8"))
        assert stored["etag"] == "abc"
        assert stored["config"] == {"db": {"host": "localhost"}}
        assert isinstance(stored["savedAt"], str) and stored["savedAt"]

    def test_creates_missing_parent_directory(self, tmp_path: Path) -> None:
        cache = tmp_path / "nested" / "dir" / "conf.json"
        transport = make_transport(HttpResponse(200, {"ETag": '"x"'}, json.dumps({"k": "v"})))
        PetBoxConfigClient(_options(cache, transport=transport)).fetch()
        assert cache.exists()


class TestCacheFallback:
    def test_unreachable_server_with_cache_returns_cached_config(self, tmp_path: Path) -> None:
        cache = tmp_path / "conf.json"
        cache.write_text(
            json.dumps(
                {"etag": "e1", "config": {"db": {"host": "cached-host"}}, "savedAt": "2026-07-02"}
            ),
            encoding="utf-8",
        )
        # optional defaults to False — the cache is still a valid start.
        client = PetBoxConfigClient(_options(cache, transport=boom_transport))
        cfg = client.fetch()
        assert cfg.get("db.host") == "cached-host"
        assert cfg.etag == "e1"
        assert client.current is not None
        assert client.current.get("db.host") == "cached-host"

    def test_unreachable_server_without_cache_raises(self, tmp_path: Path) -> None:
        cache = tmp_path / "missing.json"  # never written
        client = PetBoxConfigClient(_options(cache, transport=boom_transport))
        with pytest.raises(PetBoxConfigError) as exc:
            client.fetch()
        assert exc.value.status == 0

    def test_no_cache_path_configured_still_raises(self) -> None:
        client = PetBoxConfigClient(_options(None, transport=boom_transport))
        with pytest.raises(PetBoxConfigError):
            client.fetch()


class TestCacheEtagAndNotModified:
    def test_cached_etag_seeds_if_none_match_and_304_returns_cache(self, tmp_path: Path) -> None:
        cache = tmp_path / "conf.json"
        cache.write_text(
            json.dumps({"etag": "v1", "config": {"k": "cached"}, "savedAt": "2026-07-02"}),
            encoding="utf-8",
        )
        capture: dict[str, object] = {}
        transport = make_transport(HttpResponse(304, {"ETag": '"v1"'}, ""), capture)

        client = PetBoxConfigClient(_options(cache, transport=transport))
        cfg = client.fetch()

        headers = capture["headers"]
        assert isinstance(headers, dict)
        assert headers["If-None-Match"] == '"v1"'  # cached ETag seeded the first request
        assert cfg.get("k") == "cached"  # 304 falls back to the cached config
        assert cfg.etag == "v1"


class TestCorruptCache:
    def test_corrupt_cache_file_is_treated_as_absent(self, tmp_path: Path) -> None:
        cache = tmp_path / "conf.json"
        cache.write_text("not json {{{", encoding="utf-8")
        client = PetBoxConfigClient(_options(cache, transport=boom_transport))
        # Corrupt cache == no cache, so an unreachable server (optional=False) raises.
        with pytest.raises(PetBoxConfigError):
            client.fetch()
        assert client.current is None

    def test_non_object_cache_file_is_ignored(self, tmp_path: Path) -> None:
        cache = tmp_path / "conf.json"
        cache.write_text(json.dumps(["not", "a", "dict"]), encoding="utf-8")
        client = PetBoxConfigClient(_options(cache, transport=boom_transport))
        with pytest.raises(PetBoxConfigError):
            client.fetch()
