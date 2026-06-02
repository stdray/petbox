"""PetBox config client — Python SDK.

Sync, zero-dependency client for PetBox config with ETag-aware polling. Mirrors the
TypeScript SDK (``@stdray-npm/petbox-client``). Data + Log surfaces land later.
"""

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

__version__ = "0.1.0"
