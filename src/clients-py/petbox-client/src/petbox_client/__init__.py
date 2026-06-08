"""PetBox config client — Python SDK.

Sync, zero-dependency client for PetBox config with ETag-aware polling. Mirrors the
TypeScript SDK (``@stdray-npm/petbox-client``). Config surface only.
"""

from importlib.metadata import PackageNotFoundError
from importlib.metadata import version as _pkg_version

from .client import PetBoxConfigClient, fetch_config
from .config import ResolvedConfig
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
