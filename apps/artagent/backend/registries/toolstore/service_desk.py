"""Agent tools for service desk caller lookup, ticket creation, and confirmation."""

from __future__ import annotations

from typing import Any

from apps.artagent.backend.registries.toolstore.registry import register_tool
from apps.artagent.backend.src.services.service_desk import (
    KNOWN_CALLERS,
    ServiceDeskStore,
    Urgency,
    normalize_e164,
)
from apps.artagent.backend.voice.shared.terminal_action import build_terminal_action
from utils.ml_logging import get_logger

_service_desk_store: ServiceDeskStore | None = None
logger = get_logger("agents.tools.service_desk")


def configure_service_desk_store(store: ServiceDeskStore | None) -> None:
    """Configure the app-scoped store used by service desk tool executors."""
    global _service_desk_store
    _service_desk_store = store


LOOKUP_KNOWN_CALLER_SCHEMA: dict[str, Any] = {
    "name": "lookup_known_caller",
    "description": "Look up a caller by phone number and prefill their name and callback number.",
    "parameters": {
        "type": "object",
        "properties": {
            "caller_number": {
                "type": "string",
                "description": "The incoming caller number in E.164 format.",
            }
        },
        "required": ["caller_number"],
        "additionalProperties": False,
    },
}

LIST_SERVICE_DESK_SERVICES_SCHEMA: dict[str, Any] = {
    "name": "list_service_desk_services",
    "description": "List the service names currently accepted for service desk tickets.",
    "parameters": {
        "type": "object",
        "properties": {},
        "additionalProperties": False,
    },
}

CREATE_SERVICE_DESK_TICKET_SCHEMA: dict[str, Any] = {
    "name": "create_service_desk_ticket",
    "description": (
        "Create a service desk ticket and callback work item after collecting and confirming "
        "all required incident details."
    ),
    "parameters": {
        "type": "object",
        "properties": {
            "name": {"type": "string", "description": "Caller name."},
            "callback_number": {
                "type": "string",
                "description": "Callback number in E.164 format.",
            },
            "urgency": {
                "type": "string",
                "enum": [urgency.value for urgency in Urgency],
                "description": "Business urgency of the incident.",
            },
            "affected_service": {
                "type": "string",
                "description": (
                    "Current configured service name returned by list_service_desk_services."
                ),
            },
            "description": {
                "type": "string",
                "description": "Caller's detailed description of the issue.",
            },
            "short_description": {
                "type": "string",
                "description": "A concise, agent-generated summary of the issue.",
            },
        },
        "required": [
            "name",
            "callback_number",
            "urgency",
            "affected_service",
            "description",
            "short_description",
        ],
        "additionalProperties": False,
    },
}

RECORD_SERVICE_DESK_CONFIRMATION_SCHEMA: dict[str, Any] = {
    "name": "record_service_desk_confirmation",
    "description": (
        "Record whether the caller explicitly confirmed a service desk ticket. "
        "If details are incorrect, add a correction note instead of changing the original."
    ),
    "parameters": {
        "type": "object",
        "properties": {
            "ticket_id": {"type": "string", "description": "Service desk ticket identifier."},
            "work_item_id": {
                "type": "string",
                "description": "Callback work item identifier from the current call context.",
            },
            "call_id": {
                "type": "string",
                "description": "ACS call identifier from the current call context.",
            },
            "confirmed": {
                "type": "boolean",
                "description": "Whether the caller explicitly confirmed the ticket details.",
            },
            "correction_note": {
                "type": "string",
                "description": "Timestamped note describing any correction requested by the caller.",
            },
        },
        "required": ["ticket_id", "work_item_id", "call_id", "confirmed"],
        "additionalProperties": False,
    },
}


async def lookup_known_caller(args: dict[str, Any]) -> dict[str, Any]:
    """Look up a fake known caller using a normalized E.164 number."""
    try:
        caller_number = normalize_e164(args.get("caller_number", ""))
    except ValueError as exc:
        return {"success": False, "message": str(exc)}

    caller = KNOWN_CALLERS.get(caller_number)
    if caller is None:
        return {
            "success": True,
            "message": "Caller was not found in the known caller registry.",
            "known_caller": False,
            "caller_number": caller_number,
        }
    return {
        "success": True,
        "message": f"Known caller {caller['name']} was found.",
        "known_caller": True,
        "name": caller["name"],
        "callback_number": caller["callback_number"],
    }


async def list_service_desk_services(_: dict[str, Any]) -> dict[str, Any]:
    """List currently enabled service routes without exposing phone numbers."""
    if _service_desk_store is None:
        return {"success": False, "message": "Service desk store is not configured."}
    try:
        configuration = await _service_desk_store.get_configuration()
    except Exception:  # noqa: BLE001
        logger.exception("Service desk service lookup failed")
        return {
            "success": False,
            "message": "Configured service names could not be loaded. Please try again.",
        }
    services = [
        {"service_id": service["service_id"], "name": service["name"]}
        for service in configuration["services"]
    ]
    return {
        "success": True,
        "message": "Configured service names were loaded.",
        "services": services,
    }


async def create_service_desk_ticket(args: dict[str, Any]) -> dict[str, Any]:
    """Validate incident details and create a ticket through the configured store."""
    if _service_desk_store is None:
        return {"success": False, "message": "Service desk store is not configured."}

    values = {
        field: str(args.get(field) or "").strip()
        for field in (
            "name",
            "callback_number",
            "urgency",
            "affected_service",
            "description",
            "short_description",
        )
    }
    missing = [field for field, value in values.items() if not value]
    if missing:
        return {
            "success": False,
            "message": f"Required fields are missing: {', '.join(missing)}.",
        }
    try:
        values["callback_number"] = normalize_e164(values["callback_number"])
        values["urgency"] = Urgency(values["urgency"].lower()).value
    except ValueError as exc:
        return {"success": False, "message": str(exc)}

    try:
        ticket = await _service_desk_store.create_ticket(
            **values,
            intake_call_id=str(args.get("_call_connection_id") or "").strip() or None,
            intake_session_id=str(args.get("_session_id") or "").strip() or None,
        )
    except ValueError as exc:
        return {"success": False, "message": str(exc)}
    except Exception:  # noqa: BLE001
        logger.exception("Service desk ticket creation failed")
        return {
            "success": False,
            "message": "Service desk ticket could not be created. Please try again.",
        }

    ticket_id = ticket["ticket_id"]
    return {
        "success": True,
        "message": (
            f"Service desk ticket {ticket_id} was created. Give one final response in the "
            "caller's language: thank them, state the ticket ID, explain that the follow-up "
            "call can now occur, ask no further question, and say goodbye."
        ),
        "ticket_id": ticket_id,
        "work_item_id": ticket["work_item_id"],
        "call_control": build_terminal_action(
            ticket_id=ticket_id,
            work_item_id=ticket["work_item_id"],
        ),
    }


async def record_service_desk_confirmation(args: dict[str, Any]) -> dict[str, Any]:
    """Record explicit confirmation and append any caller correction."""
    if _service_desk_store is None:
        return {"success": False, "message": "Service desk store is not configured."}
    ticket_id = str(args.get("ticket_id") or "").strip()
    work_item_id = str(args.get("work_item_id") or "").strip()
    call_id = str(args.get("call_id") or "").strip()
    confirmed = args.get("confirmed")
    correction_note = str(args.get("correction_note") or "").strip()
    if not ticket_id or not work_item_id or not call_id:
        return {
            "success": False,
            "message": "ticket_id, work_item_id, and call_id are required.",
        }
    if not isinstance(confirmed, bool):
        return {"success": False, "message": "confirmed must be a boolean."}
    if not confirmed and not correction_note:
        return {
            "success": False,
            "message": "A correction_note is required when ticket details are not confirmed.",
        }

    try:
        if confirmed:
            ticket = await _service_desk_store.record_confirmation(
                ticket_id,
                True,
                work_item_id=work_item_id,
                call_id=call_id,
            )
        else:
            ticket = await _service_desk_store.append_correction_note(
                ticket_id,
                correction_note,
                work_item_id=work_item_id,
                call_id=call_id,
            )
    except Exception:  # noqa: BLE001
        logger.exception("Service desk confirmation failed for %s", ticket_id)
        return {
            "success": False,
            "message": f"Confirmation for service desk ticket {ticket_id} could not be recorded.",
        }
    if ticket is None:
        return {
            "success": False,
            "message": (
                f"Service desk ticket {ticket_id} is no longer awaiting confirmation "
                "from this call."
            ),
        }

    state = "confirmed" if confirmed else "recorded with a correction"
    return {
        "success": True,
        "message": f"Service desk ticket {ticket_id} was {state}.",
        "ticket_id": ticket_id,
        "confirmed": confirmed,
        "correction_recorded": bool(correction_note),
    }


register_tool(
    "lookup_known_caller",
    LOOKUP_KNOWN_CALLER_SCHEMA,
    lookup_known_caller,
    tags={"service_desk", "caller_lookup"},
)
register_tool(
    "list_service_desk_services",
    LIST_SERVICE_DESK_SERVICES_SCHEMA,
    list_service_desk_services,
    tags={"service_desk", "ticketing"},
)
register_tool(
    "create_service_desk_ticket",
    CREATE_SERVICE_DESK_TICKET_SCHEMA,
    create_service_desk_ticket,
    tags={"service_desk", "ticketing"},
)
register_tool(
    "record_service_desk_confirmation",
    RECORD_SERVICE_DESK_CONFIRMATION_SCHEMA,
    record_service_desk_confirmation,
    tags={"service_desk", "ticketing", "confirmation"},
)

__all__ = [
    "KNOWN_CALLERS",
    "CREATE_SERVICE_DESK_TICKET_SCHEMA",
    "LIST_SERVICE_DESK_SERVICES_SCHEMA",
    "LOOKUP_KNOWN_CALLER_SCHEMA",
    "RECORD_SERVICE_DESK_CONFIRMATION_SCHEMA",
    "configure_service_desk_store",
    "create_service_desk_ticket",
    "list_service_desk_services",
    "lookup_known_caller",
    "record_service_desk_confirmation",
]
