"""Option, error, and transport types for the PetBox config client."""

from __future__ import annotations

from collections.abc import Callable, Mapping
from dataclasses import dataclass, field
from pathlib import Path
from typing import Literal

# Response-shape templates (spec §9.1).
Template = Literal["flat", "dotnet", "envvar", "envvar-deep"]

# Tag-vector — flat key=value pairs sent as query params to /v1/conf.
TagVector = Mapping[str, str]


@dataclass(frozen=True)
class HttpResponse:
    """Minimal HTTP response returned by a transport.

    Header lookups are case-insensitive (the client only reads ``ETag``), so a
    custom transport may supply headers with any casing.
    """

    status: int
    headers: Mapping[str, str]
    body: str

    def header(self, name: str) -> str | None:
        """Case-insensitive header lookup. Returns None if absent."""
        lower = name.lower()
        for key, value in self.headers.items():
            if key.lower() == lower:
                return value
        return None


# A transport is any callable taking (url, headers) and returning an HttpResponse.
# The default transport uses urllib; tests inject a fake to avoid the network.
Transport = Callable[[str, Mapping[str, str]], HttpResponse]


@dataclass(frozen=True)
class PetBoxConfigClientOptions:
    """Options for :class:`PetBoxConfigClient`."""

    # Base URL of the PetBox server, e.g. "https://petbox.3po.su". Trailing slash optional.
    endpoint: str
    # Plaintext API key. Sent as X-YobaConf-ApiKey header on every request.
    api_key: str
    # Tag-vector — every tag the request carries. Resolve finds bindings whose tag-set is a subset.
    tags: TagVector
    # Response template (default: "flat"). Controls the shape of the JSON response.
    template: Template = "flat"
    # Polling interval in SECONDS. Each poll uses If-None-Match for cheap 304s.
    # Set to 0 (or negative) to disable polling (one-shot fetch only). Default: 5 minutes.
    refresh_interval: float = 5 * 60
    # When True, initial fetch failures (network, auth, 409) don't raise — the client
    # starts with empty config and retries on the next poll. Default: False.
    optional: bool = False
    # Custom transport (for testing / proxy injection). Default: urllib over HTTPS.
    transport: Transport | None = field(default=None)
    # Opt-in disk cache for last-known-good config. When set, every successful 200 is
    # written atomically to this JSON file ({etag, config, savedAt}); on startup the file
    # is read so the client survives a process restart even if the server is unreachable
    # (the cached config becomes a valid initial value, and its ETag seeds If-None-Match).
    # A missing or corrupt file is treated as absent. Default: None (no disk cache).
    cache_path: str | Path | None = None
    # Per-request timeout in SECONDS for the default urllib transport. Ignored when a
    # custom transport is supplied. Default: 10 seconds.
    timeout: float = 10.0


class PetBoxConfigError(Exception):
    """Structured error from the PetBox config API."""

    def __init__(self, message: str, status: int, body: object) -> None:
        super().__init__(message)
        self.status = status
        self.body = body
