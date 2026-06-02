"""Tests for PetBoxConfigClient — ported from the TS SDK's client.test.ts.

A fake transport stands in for the network, mirroring the TS ``fetchImpl`` injection.
"""

import json
import threading
from collections.abc import Mapping

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
    """Build a transport returning a fixed response, optionally capturing the request."""

    def transport(url: str, headers: Mapping[str, str]) -> HttpResponse:
        if capture is not None:
            capture["url"] = url
            capture["headers"] = dict(headers)
        return response

    return transport


class TestConstructor:
    def test_requires_endpoint(self) -> None:
        with pytest.raises(ValueError):
            PetBoxConfigClient(
                PetBoxConfigClientOptions(endpoint="", api_key="key", tags={"env": "test"})
            )

    def test_requires_api_key(self) -> None:
        with pytest.raises(ValueError):
            PetBoxConfigClient(
                PetBoxConfigClientOptions(
                    endpoint="https://petbox.test", api_key="", tags={"env": "test"}
                )
            )

    def test_requires_at_least_one_tag(self) -> None:
        with pytest.raises(ValueError):
            PetBoxConfigClient(
                PetBoxConfigClientOptions(endpoint="https://petbox.test", api_key="key", tags={})
            )


class TestFetch:
    def test_returns_resolved_config_on_200(self) -> None:
        transport = make_transport(
            HttpResponse(200, {"ETag": '"abc"'}, json.dumps({"db": {"host": "localhost"}}))
        )
        client = PetBoxConfigClient(
            PetBoxConfigClientOptions(
                endpoint="https://petbox.test",
                api_key="key",
                tags={"env": "test"},
                transport=transport,
                refresh_interval=0,
            )
        )
        cfg = client.fetch()
        assert cfg.get("db.host") == "localhost"
        assert cfg.etag == "abc"

    def test_raises_on_4xx(self) -> None:
        transport = make_transport(
            HttpResponse(401, {}, json.dumps({"error": "unauthorized", "reason": "bad key"}))
        )
        client = PetBoxConfigClient(
            PetBoxConfigClientOptions(
                endpoint="https://petbox.test",
                api_key="key",
                tags={"env": "test"},
                transport=transport,
                refresh_interval=0,
            )
        )
        with pytest.raises(PetBoxConfigError) as exc:
            client.fetch()
        assert exc.value.status == 401
        assert "bad key" in str(exc.value)

    def test_optional_swallows_errors_and_returns_empty(self) -> None:
        transport = make_transport(HttpResponse(500, {}, "nope"))
        client = PetBoxConfigClient(
            PetBoxConfigClientOptions(
                endpoint="https://petbox.test",
                api_key="key",
                tags={"env": "test"},
                transport=transport,
                refresh_interval=0,
                optional=True,
            )
        )
        cfg = client.fetch()
        assert cfg.get("anything") is None

    def test_sends_api_key_header_and_tags_in_query(self) -> None:
        capture: dict[str, object] = {}
        transport = make_transport(HttpResponse(200, {}, "{}"), capture)
        client = PetBoxConfigClient(
            PetBoxConfigClientOptions(
                endpoint="https://petbox.test",
                api_key="test-key",
                tags={"env": "prod", "project": "kpvotes"},
                transport=transport,
                refresh_interval=0,
            )
        )
        client.fetch()
        headers = capture["headers"]
        assert isinstance(headers, dict)
        assert headers["X-YobaConf-ApiKey"] == "test-key"
        url = capture["url"]
        assert isinstance(url, str)
        assert "env=prod" in url
        assert "project=kpvotes" in url
        assert "/v1/conf?" in url

    def test_unreachable_host_raises_petbox_error_with_status_zero(self) -> None:
        def boom(url: str, headers: Mapping[str, str]) -> HttpResponse:
            raise OSError("connection refused")

        client = PetBoxConfigClient(
            PetBoxConfigClientOptions(
                endpoint="https://petbox.test",
                api_key="key",
                tags={"env": "test"},
                transport=boom,
                refresh_interval=0,
            )
        )
        with pytest.raises(PetBoxConfigError) as exc:
            client.fetch()
        assert exc.value.status == 0


class TestTemplates:
    def test_dotnet_template_adds_query_and_direct_lookup(self) -> None:
        capture: dict[str, object] = {}
        transport = make_transport(
            HttpResponse(200, {}, json.dumps({"Db:Host": "localhost"})), capture
        )
        client = PetBoxConfigClient(
            PetBoxConfigClientOptions(
                endpoint="https://petbox.test",
                api_key="key",
                tags={"env": "test"},
                template="dotnet",
                transport=transport,
                refresh_interval=0,
            )
        )
        cfg = client.fetch()
        assert isinstance(capture["url"], str)
        assert "template=dotnet" in capture["url"]
        assert cfg.get("Db:Host") == "localhost"


class TestPolling:
    def test_304_keeps_last_config_and_emits_no_change(self) -> None:
        # First call 200, subsequent calls 304 — emulates an unchanged poll.
        responses: list[HttpResponse] = [
            HttpResponse(200, {"ETag": '"v1"'}, json.dumps({"k": "v"})),
            HttpResponse(304, {"ETag": '"v1"'}, ""),
        ]
        index = {"i": 0}
        requested_etags: list[str | None] = []

        def transport(url: str, headers: Mapping[str, str]) -> HttpResponse:
            requested_etags.append(headers.get("If-None-Match"))
            i = min(index["i"], len(responses) - 1)
            index["i"] += 1
            return responses[i]

        changes: list[object] = []
        done = threading.Event()
        client = PetBoxConfigClient(
            PetBoxConfigClientOptions(
                endpoint="https://petbox.test",
                api_key="key",
                tags={"env": "test"},
                transport=transport,
                refresh_interval=0.05,
            )
        )
        client.on("change", lambda cfg: (changes.append(cfg), done.set()))
        first = client.start()
        assert first.get("k") == "v"
        # Let at least one poll fire (304 → no change event).
        done.wait(timeout=0.5)
        client.stop()

        assert changes == []  # 304 must not emit "change"
        assert requested_etags[1] == '"v1"'  # second request carries If-None-Match

    def test_change_event_fires_on_new_etag(self) -> None:
        responses: list[HttpResponse] = [
            HttpResponse(200, {"ETag": '"v1"'}, json.dumps({"n": 1})),
            HttpResponse(200, {"ETag": '"v2"'}, json.dumps({"n": 2})),
        ]
        index = {"i": 0}

        def transport(url: str, headers: Mapping[str, str]) -> HttpResponse:
            i = min(index["i"], len(responses) - 1)
            index["i"] += 1
            return responses[i]

        seen: list[object] = []
        done = threading.Event()
        client = PetBoxConfigClient(
            PetBoxConfigClientOptions(
                endpoint="https://petbox.test",
                api_key="key",
                tags={"env": "test"},
                transport=transport,
                refresh_interval=0.05,
            )
        )

        def on_change(cfg: object) -> None:
            seen.append(cfg)
            done.set()

        client.on("change", on_change)
        client.start()
        assert done.wait(timeout=1.0)
        client.stop()
        assert client.current is not None
        assert client.current.get("n") == "2"


def test_dispose_blocks_further_fetch() -> None:
    transport = make_transport(HttpResponse(200, {}, "{}"))
    client = PetBoxConfigClient(
        PetBoxConfigClientOptions(
            endpoint="https://petbox.test",
            api_key="key",
            tags={"env": "test"},
            transport=transport,
            refresh_interval=0,
        )
    )
    client.dispose()
    with pytest.raises(RuntimeError):
        client.fetch()
