"""Immutable resolved config returned by a PetBox fetch."""

from __future__ import annotations

from typing import Any

_MISSING = object()


class ResolvedConfig:
    """Immutable resolved config from a PetBox fetch.

    For the ``flat`` template, ``data`` is a nested JSON tree and ``get("db.host")``
    traverses it. For ``dotnet`` / ``envvar`` / ``envvar-deep`` templates, ``data`` is
    a flat dict and ``get("DB_HOST")`` does a direct key lookup.
    """

    def __init__(self, raw: Any, etag: str | None) -> None:
        self._raw = raw
        self._etag = etag

    @property
    def etag(self) -> str | None:
        return self._etag

    @property
    def data(self) -> Any:
        """The raw parsed JSON response body."""
        return self._raw

    def get(self, path: str) -> str | None:
        """Return the string value at the dotted path (flat) or direct key (other templates).

        Returns None if the path doesn't exist or the value is null.
        """
        v = self._resolve(path)
        if v is _MISSING or v is None:
            return None
        if isinstance(v, str):
            return v
        if isinstance(v, bool):
            return "true" if v else "false"
        return str(v)

    def get_number(self, path: str) -> float | None:
        """Return the numeric value at the path, or None if missing / not a number.

        Accepts JSON numbers and numeric strings.
        """
        v = self._resolve(path)
        if v is _MISSING or v is None:
            return None
        if isinstance(v, bool):
            return None
        if isinstance(v, (int, float)):
            return float(v)
        if isinstance(v, str):
            try:
                return float(v)
            except ValueError:
                return None
        return None

    def get_bool(self, path: str) -> bool | None:
        """Return the boolean value at the path, or None if missing.

        Accepts JSON booleans and the strings "true"/"false" (case-insensitive).
        """
        v = self._resolve(path)
        if v is _MISSING or v is None:
            return None
        if isinstance(v, bool):
            return v
        if isinstance(v, str):
            lower = v.lower()
            if lower == "true":
                return True
            if lower == "false":
                return False
        return None

    def get_json(self, path: str) -> Any:
        """Return the JSON value at the path (object, array, scalar). None if missing."""
        v = self._resolve(path)
        return None if v is _MISSING else v

    def to_env(self) -> dict[str, str]:
        """Flatten the config into a ``Dict[str, str]`` suitable for env export.

        For the flat template, nested objects expand to dotted keys. For other
        templates, the already-flat dict is returned with values stringified.
        """
        if not isinstance(self._raw, dict):
            return {}
        out: dict[str, str] = {}
        self._flatten(self._raw, "", out)
        return out

    def _resolve(self, path: str) -> Any:
        if not isinstance(self._raw, dict):
            return _MISSING

        # Flat dictionary (dotnet/envvar/envvar-deep templates) — keys have no dots,
        # so do a direct lookup.
        if "." not in path:
            return self._raw.get(path, _MISSING)

        # Dotted path — traverse nested object (flat template).
        current: Any = self._raw
        for seg in path.split("."):
            if not isinstance(current, dict):
                return _MISSING
            if seg not in current:
                return _MISSING
            current = current[seg]
        return current

    def _flatten(self, obj: dict[str, Any], prefix: str, out: dict[str, str]) -> None:
        for k, v in obj.items():
            key = f"{prefix}.{k}" if prefix else k
            if isinstance(v, dict):
                self._flatten(v, key, out)
            elif v is None:
                out[key] = ""
            elif isinstance(v, bool):
                out[key] = "true" if v else "false"
            else:
                out[key] = str(v)
