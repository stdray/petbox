"""PetBox config client — Python SDK.

Sync, zero-dependency client for PetBox config (ETag-aware polling) plus a Data-module
client (raw parameterized SQL pass-through). Mirrors the TypeScript SDK
(``@stdray-npm/petbox-client``).
"""

from importlib.metadata import PackageNotFoundError
from importlib.metadata import version as _pkg_version

from .client import PetBoxConfigClient, fetch_config
from .config import ResolvedConfig
from .data import PetBoxDataClient, PetBoxDataError, PetBoxSqlParam
from .types import (
    HttpResponse,
    PetBoxConfigClientOptions,
    PetBoxConfigError,
    TagVector,
    Template,
    Transport,
)

__all__ = [
    "PetBoxConfigClient",
    "fetch_config",
    "ResolvedConfig",
    "PetBoxConfigClientOptions",
    "PetBoxConfigError",
    "PetBoxDataClient",
    "PetBoxDataError",
    "PetBoxSqlParam",
    "HttpResponse",
    "Transport",
    "TagVector",
    "Template",
]

# Version is stamped from GitVersion at publish (build.cs PyPiPack → `uv version`);
# read it back from installed dist metadata so runtime __version__ is accurate.
try:
    __version__ = _pkg_version("petbox-client")
except PackageNotFoundError:  # running from a source tree, not installed
    __version__ = "0.0.0"
