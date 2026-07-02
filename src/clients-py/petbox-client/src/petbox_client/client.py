"""Sync PetBox config client with ETag-aware background polling (zero runtime deps)."""

from __future__ import annotations

import json
import os
import tempfile
import threading
import urllib.error
import urllib.request
from collections.abc import Callable, Mapping
from datetime import datetime, timezone
from pathlib import Path
from typing import Any
from urllib.parse import urlencode

from .config import ResolvedConfig
from .types import (
    HttpResponse,
    PetBoxConfigClientOptions,
    PetBoxConfigError,
    Transport,
)

_API_KEY_HEADER = "X-YobaConf-ApiKey"

EventListener = Callable[..., None]


def _default_transport(
    url: str, headers: Mapping[str, str], timeout: float = 10.0
) -> HttpResponse:
    """Perform a GET via urllib. Raises urllib errors only for unreachable hosts;
    HTTP 3xx/4xx/5xx are returned as HttpResponse (4xx/5xx via HTTPError handler).

    ``timeout`` caps each request (seconds) so a hung server can't block the caller
    forever — important for the disk-cache fallback after a data-centre outage."""
    request = urllib.request.Request(url, headers=dict(headers), method="GET")  # noqa: S310 - https URL from configured endpoint
    try:
        with urllib.request.urlopen(request, timeout=timeout) as response:  # noqa: S310 - https URL from configured endpoint
            body = response.read().decode("utf-8")
            return HttpResponse(response.status, dict(response.headers.items()), body)
    except urllib.error.HTTPError as http_error:
        # 304/4xx/5xx — a real HTTP response, not a transport failure.
        body = http_error.read().decode("utf-8") if http_error.fp is not None else ""
        return HttpResponse(http_error.code, dict(http_error.headers.items()), body)


class PetBoxConfigClient:
    """Sync SDK client for PetBox config.

    Fetches resolved config from ``/v1/conf`` with ETag-aware polling. Supports all four
    response templates (flat, dotnet, envvar, envvar-deep).

    Example::

        client = PetBoxConfigClient(PetBoxConfigClientOptions(
            endpoint="https://petbox.3po.su",
            api_key=os.environ["PETBOX_API_KEY"],
            tags={"env": "prod", "project": "kpvotes"},
        ))

        config = client.start()
        print(config.get("db.host"))

        client.on("change", lambda cfg: print("config updated", cfg.data))

    Usable as a context manager — ``__exit__`` disposes the client::

        with PetBoxConfigClient(options) as client:
            config = client.start()
    """

    def __init__(self, options: PetBoxConfigClientOptions) -> None:
        if not options.endpoint:
            raise ValueError("endpoint is required")
        if not options.api_key:
            raise ValueError("api_key is required")
        if not options.tags:
            raise ValueError("at least one tag is required")

        self._options = options
        if options.transport is not None:
            self._transport: Transport = options.transport
        else:
            timeout = options.timeout

            def _transport(url: str, headers: Mapping[str, str]) -> HttpResponse:
                return _default_transport(url, headers, timeout)

            self._transport = _transport
        self._current: ResolvedConfig | None = None
        self._etag: str | None = None
        self._listeners: dict[str, list[EventListener]] = {"change": [], "error": []}
        self._lock = threading.Lock()
        self._stop_event = threading.Event()
        self._thread: threading.Thread | None = None
        self._disposed = False

        # Opt-in disk cache. A cached last-known-good config seeds the current value and
        # its ETag primes the first If-None-Match, so a restart survives a dead server.
        self._cache_path: Path | None = (
            Path(options.cache_path) if options.cache_path is not None else None
        )
        self._cached: ResolvedConfig | None = self._load_cache()
        if self._cached is not None:
            self._current = self._cached
            self._etag = self._cached.etag

    @property
    def current(self) -> ResolvedConfig | None:
        """The most recently fetched config, or None if never fetched."""
        return self._current

    def on(self, event: str, listener: EventListener) -> PetBoxConfigClient:
        """Register a listener for "change" or "error". Returns self for chaining."""
        if event not in self._listeners:
            raise ValueError(f"unknown event: {event!r}")
        self._listeners[event].append(listener)
        return self

    def off(self, event: str, listener: EventListener) -> PetBoxConfigClient:
        """Remove a previously registered listener. Returns self for chaining."""
        if event in self._listeners:
            try:
                self._listeners[event].remove(listener)
            except ValueError:
                pass
        return self

    def fetch(self) -> ResolvedConfig:
        """One-shot fetch. Does NOT start background polling.

        Raises on auth errors, network errors, and 409 conflicts (unless optional=True).
        """
        self._ensure_not_disposed()
        try:
            cfg, fresh = self._fetch_once()
        except Exception:
            # Server unreachable / errored. A cached last-known-good config is a valid
            # start even when optional=False — the whole point of the disk cache.
            if self._cached is not None:
                self._current = self._cached
                self._etag = self._cached.etag
                return self._cached
            if self._options.optional:
                return ResolvedConfig({}, None)
            raise
        self._current = cfg
        self._etag = cfg.etag
        if fresh:
            self._write_cache(cfg)
        return cfg

    def start(self) -> ResolvedConfig:
        """Initial fetch + start background ETag polling. Returns the first resolved config.

        Fires "change" events on subsequent updates, "error" on polling failures.
        """
        cfg = self.fetch()
        self._start_polling()
        return cfg

    def stop(self) -> None:
        """Stop background polling. Does NOT clear the last config."""
        self._stop_event.set()
        thread = self._thread
        if thread is not None and thread is not threading.current_thread():
            thread.join(timeout=5)
        self._thread = None

    def dispose(self) -> None:
        """Stop polling and remove all listeners."""
        self._disposed = True
        self.stop()
        for listeners in self._listeners.values():
            listeners.clear()

    def __enter__(self) -> PetBoxConfigClient:
        return self

    def __exit__(self, *_exc: object) -> None:
        self.dispose()

    # ── internals ──────────────────────────────────────────

    def _ensure_not_disposed(self) -> None:
        if self._disposed:
            raise RuntimeError("PetBoxConfigClient is disposed")

    def _load_cache(self) -> ResolvedConfig | None:
        """Read the last-known-good config from disk. A missing, unreadable, or corrupt
        file is treated as absent (returns None) — never raises."""
        if self._cache_path is None:
            return None
        try:
            raw = self._cache_path.read_text(encoding="utf-8")
            obj = json.loads(raw)
        except (OSError, ValueError):
            return None
        if not isinstance(obj, dict) or "config" not in obj:
            return None
        etag = obj.get("etag")
        if etag is not None and not isinstance(etag, str):
            return None
        return ResolvedConfig(obj["config"], etag)

    def _write_cache(self, cfg: ResolvedConfig) -> None:
        """Persist the config as JSON ({etag, config, savedAt}), written atomically via a
        temp file + os.replace in the same directory. Write failures never break the
        client — the cache is best-effort."""
        if self._cache_path is None:
            return
        try:
            payload = json.dumps(
                {
                    "etag": cfg.etag,
                    "config": cfg.data,
                    "savedAt": datetime.now(timezone.utc).isoformat(),
                }
            )
            directory = self._cache_path.parent
            directory.mkdir(parents=True, exist_ok=True)
            fd, tmp = tempfile.mkstemp(
                dir=str(directory), prefix=f"{self._cache_path.name}.", suffix=".tmp"
            )
            try:
                with os.fdopen(fd, "w", encoding="utf-8") as handle:
                    handle.write(payload)
                os.replace(tmp, self._cache_path)
            except Exception:
                try:
                    os.unlink(tmp)
                except OSError:
                    pass
                raise
        except Exception:  # noqa: BLE001, S110 - cache persistence must never break the client
            pass

    def _build_url(self) -> str:
        base = self._options.endpoint
        if not base.endswith("/"):
            base += "/"
        # Sort for stable URLs (helps caching / log grepping).
        params: list[tuple[str, str]] = [
            (k, self._options.tags[k] or "") for k in sorted(self._options.tags)
        ]
        if self._options.template != "flat":
            params.append(("template", self._options.template))
        return f"{base}v1/conf?{urlencode(params)}"

    def _fetch_once(self) -> tuple[ResolvedConfig, bool]:
        """Fetch once. Returns ``(config, fresh)`` where ``fresh`` is True only for a
        real 200 body (worth writing to the disk cache) and False for a 304."""
        url = self._build_url()
        headers: dict[str, str] = {_API_KEY_HEADER: self._options.api_key}
        if self._etag is not None:
            headers["If-None-Match"] = f'"{self._etag}"'

        try:
            response = self._transport(url, headers)
        except Exception as cause:
            raise PetBoxConfigError(f"Failed to reach PetBox at {url}: {cause}", 0, None) from cause

        # 304 — unchanged. Return last-known-good config (current or, at startup, cache).
        if response.status == 304:
            new_etag = self._strip_etag(response.header("ETag"))
            source = self._current if self._current is not None else self._cached
            data = source.data if source is not None else {}
            return ResolvedConfig(data, new_etag or self._etag), False

        if response.status < 200 or response.status >= 300:
            parsed: Any = None
            try:
                parsed = json.loads(response.body)
            except ValueError:
                pass
            raise PetBoxConfigError(
                self._error_message(response.status, parsed), response.status, parsed
            )

        etag = self._strip_etag(response.header("ETag"))
        data = json.loads(response.body) if response.body else {}
        return ResolvedConfig(data, etag), True

    def _start_polling(self) -> None:
        if self._options.refresh_interval <= 0:
            return
        if self._thread is not None:
            return
        self._stop_event.clear()
        self._thread = threading.Thread(
            target=self._poll_loop, name="petbox-config-poll", daemon=True
        )
        self._thread.start()

    def _poll_loop(self) -> None:
        interval = self._options.refresh_interval
        while not self._stop_event.wait(interval):
            try:
                cfg, fresh = self._fetch_once()
                if fresh:
                    self._write_cache(cfg)
                if cfg.etag != self._etag:
                    self._current = cfg
                    self._etag = cfg.etag
                    self._emit("change", cfg)
            except Exception as err:  # noqa: BLE001 - surfaced via the error event
                self._emit("error", err)

    def _emit(self, event: str, *args: Any) -> None:
        for listener in list(self._listeners.get(event, [])):
            listener(*args)

    @staticmethod
    def _strip_etag(raw: str | None) -> str | None:
        if raw is None:
            return None
        # Server sends `"<hex>"` — strip quotes.
        if len(raw) >= 2 and raw.startswith('"') and raw.endswith('"'):
            return raw[1:-1]
        return raw

    @staticmethod
    def _error_message(status: int, body: Any) -> str:
        if isinstance(body, dict) and "error" in body:
            reason = body.get("reason")
            suffix = f": {reason}" if isinstance(reason, str) else ""
            return f"{body['error']}{suffix}"
        return f"HTTP {status}"


def fetch_config(options: PetBoxConfigClientOptions) -> ResolvedConfig:
    """Convenience: one-shot fetch without keeping a client instance.

    Equivalent to ``PetBoxConfigClient(options).fetch()``.
    """
    return PetBoxConfigClient(options).fetch()
