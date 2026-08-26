"""Service desk domain values and validation helpers."""

from __future__ import annotations

import re
from enum import StrEnum
from typing import Any, Final
from uuid import uuid4

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

DEFAULT_AFFECTED_SERVICES: Final[dict[str, str]] = {
    "email": "+14255550201",
    "network": "+14255550202",
    "payroll": "+14255550203",
    "vpn": "+14255550204",
}
AFFECTED_SERVICES: Final[dict[str, str]] = DEFAULT_AFFECTED_SERVICES
DEFAULT_RETRY_INTERVALS_MINUTES: Final[tuple[int, ...]] = (10,)
MAX_RETRY_INTERVALS: Final = 20
MAX_RETRY_INTERVAL_MINUTES: Final = 1440


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
    """Return a default service label and number for legacy callers."""
    label = normalize_service_label(value)
    try:
        return label, DEFAULT_AFFECTED_SERVICES[label]
    except KeyError as exc:
        raise ValueError(f"Unsupported affected service: {value}") from exc


def default_service_routes() -> list[dict[str, Any]]:
    """Return independent default route documents for initial configuration."""
    return [
        {
            "service_id": label,
            "name": label,
            "phone_number": phone_number,
            "enabled": True,
        }
        for label, phone_number in DEFAULT_AFFECTED_SERVICES.items()
    ]


def validate_retry_intervals(values: list[int] | tuple[int, ...]) -> list[int]:
    """Validate and normalize a retry schedule expressed in whole minutes."""
    if not values:
        raise ValueError("At least one retry interval is required.")
    if len(values) > MAX_RETRY_INTERVALS:
        raise ValueError(f"At most {MAX_RETRY_INTERVALS} retry intervals are allowed.")

    normalized: list[int] = []
    for value in values:
        if isinstance(value, bool) or not isinstance(value, int):
            raise ValueError("Retry intervals must be whole minutes.")
        if value < 1 or value > MAX_RETRY_INTERVAL_MINUTES:
            raise ValueError(
                f"Retry intervals must be between 1 and {MAX_RETRY_INTERVAL_MINUTES} minutes."
            )
        normalized.append(value)
    return normalized


def validate_service_routes(
    routes: list[dict[str, Any]],
    *,
    existing_service_ids: set[str] | None = None,
) -> list[dict[str, Any]]:
    """Validate mutable service fields and assign IDs to newly added services."""
    if not routes:
        raise ValueError("At least one service route is required.")

    existing_ids = existing_service_ids or set()
    seen_ids: set[str] = set()
    seen_names: set[str] = set()
    normalized: list[dict[str, Any]] = []
    for route in routes:
        name = str(route.get("name") or "").strip()
        if not name:
            raise ValueError("Every service route requires a name.")
        name_key = name.casefold()
        if name_key in seen_names:
            raise ValueError(f"Service names must be unique: {name}.")
        seen_names.add(name_key)

        raw_service_id = str(route.get("service_id") or "").strip()
        if raw_service_id:
            if raw_service_id not in existing_ids:
                raise ValueError(f"Unknown service ID: {raw_service_id}.")
            service_id = raw_service_id
        else:
            service_id = f"svc-{uuid4().hex}"
        if service_id in seen_ids:
            raise ValueError(f"Duplicate service ID: {service_id}.")
        seen_ids.add(service_id)

        normalized.append(
            {
                "service_id": service_id,
                "name": name,
                "phone_number": normalize_e164(str(route.get("phone_number") or "")),
                "enabled": True,
            }
        )
    return normalized
