"""Shared Azure Communication Services authentication configuration."""

from typing import Literal

ACSAuthMode = Literal["auto", "connection_string", "entra"]

_CONNECTION_STRING_AUTH_ALIASES = {"connection_string", "connection-string", "key", "access_key"}
_ENTRA_AUTH_ALIASES = {"entra", "entra_id", "aad", "managed_identity", "default_credential"}


def normalize_acs_auth_mode(auth_mode: str | None) -> ACSAuthMode:
    """Normalize ACS authentication mode configuration."""
    normalized = (auth_mode or "auto").strip().lower()
    if not normalized or normalized == "auto":
        return "auto"
    if normalized in _CONNECTION_STRING_AUTH_ALIASES:
        return "connection_string"
    if normalized in _ENTRA_AUTH_ALIASES:
        return "entra"
    raise ValueError(
        "ACS_AUTH_MODE must be one of: auto, connection_string, entra " f"(got {auth_mode!r})"
    )
