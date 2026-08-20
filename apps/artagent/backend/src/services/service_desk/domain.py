"""Service desk domain values and validation helpers."""

from __future__ import annotations

import re
from enum import StrEnum
from typing import Final

_E164_PATTERN: Final = re.compile(r"^\+[1-9]\d{1,14}$")


class Urgency(StrEnum):
    """Supported service desk urgency levels."""

    LOW = "low"
    MEDIUM = "medium"
    HIGH = "high"
    CRITICAL = "critical"


KNOWN_CALLERS: Final[dict[str, dict[str, str]]] = {
    "+14255550101": {"name": "Ada Lovelace", "callback_number": "+14255550101"},
    "+442079460123": {"name": "Alan Turing", "callback_number": "+442079460123"},
}

AFFECTED_SERVICES: Final[dict[str, str]] = {
    "email": "+14255550201",
    "network": "+14255550202",
    "payroll": "+14255550203",
    "vpn": "+14255550204",
}


def normalize_e164(value: str) -> str:
    """Normalize common E.164 formatting and reject invalid phone numbers."""
    candidate = str(value or "").strip()
    if not candidate.startswith("+"):
        raise ValueError("Phone number must be in E.164 format.")

    normalized = f"+{re.sub(r'[^0-9]', '', candidate)}"
    if not _E164_PATTERN.fullmatch(normalized):
        raise ValueError("Phone number must be in E.164 format.")
    return normalized


def normalize_service_label(value: str) -> str:
    """Normalize a service label to its registry key."""
    return re.sub(r"[\s-]+", "_", str(value or "").strip().lower())


def standby_number_for(value: str) -> tuple[str, str]:
    """Return the canonical service label and its standby number."""
    label = normalize_service_label(value)
    try:
        return label, AFFECTED_SERVICES[label]
    except KeyError as exc:
        raise ValueError(f"Unsupported affected service: {value}") from exc

