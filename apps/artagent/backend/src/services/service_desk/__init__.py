"""Service desk domain and persistence APIs."""

from apps.artagent.backend.src.services.service_desk.domain import (
    AFFECTED_SERVICES,
    KNOWN_CALLERS,
    Urgency,
    normalize_e164,
    normalize_service_label,
    standby_number_for,
)
from apps.artagent.backend.src.services.service_desk.store import (
    TICKET_DOCUMENT_TYPE,
    WORK_ITEM_DOCUMENT_TYPE,
    WORK_ITEM_EXPIRY_HOURS,
    WORK_ITEM_RETRY_SECONDS,
    ServiceDeskStore,
)

__all__ = [
    "AFFECTED_SERVICES",
    "KNOWN_CALLERS",
    "ServiceDeskStore",
    "TICKET_DOCUMENT_TYPE",
    "Urgency",
    "WORK_ITEM_DOCUMENT_TYPE",
    "WORK_ITEM_EXPIRY_HOURS",
    "WORK_ITEM_RETRY_SECONDS",
    "normalize_e164",
    "normalize_service_label",
    "standby_number_for",
]

