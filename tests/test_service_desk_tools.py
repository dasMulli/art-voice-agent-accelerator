from __future__ import annotations

from unittest.mock import AsyncMock

import pytest
from apps.artagent.backend.registries.toolstore import service_desk


@pytest.fixture(autouse=True)
def reset_store():
    service_desk.configure_service_desk_store(None)
    yield
    service_desk.configure_service_desk_store(None)


def test_service_desk_tool_schemas_are_openai_compatible():
    for schema in (
        service_desk.LOOKUP_KNOWN_CALLER_SCHEMA,
        service_desk.LIST_SERVICE_DESK_SERVICES_SCHEMA,
        service_desk.CREATE_SERVICE_DESK_TICKET_SCHEMA,
        service_desk.RECORD_SERVICE_DESK_CONFIRMATION_SCHEMA,
    ):
        assert schema["name"]
        assert schema["description"]
        assert schema["parameters"]["type"] == "object"
        assert schema["parameters"]["additionalProperties"] is False

    urgency = service_desk.CREATE_SERVICE_DESK_TICKET_SCHEMA["parameters"]["properties"]["urgency"]
    assert urgency["enum"] == ["low", "medium", "high", "critical"]
    affected_service = service_desk.CREATE_SERVICE_DESK_TICKET_SCHEMA["parameters"]["properties"][
        "affected_service"
    ]
    assert "enum" not in affected_service


@pytest.mark.asyncio
async def test_lookup_known_caller_normalizes_number():
    known_number = next(iter(service_desk.KNOWN_CALLERS))
    formatted = f"{known_number[:2]} ({known_number[2:5]}) {known_number[5:8]}-{known_number[8:]}"

    result = await service_desk.lookup_known_caller({"caller_number": formatted})

    assert result["success"] is True
    assert result["known_caller"] is True
    assert result["callback_number"] == known_number
    assert result["message"]


@pytest.mark.asyncio
async def test_create_ticket_validates_fields_before_calling_store():
    store = AsyncMock()
    service_desk.configure_service_desk_store(store)

    result = await service_desk.create_service_desk_ticket(
        {
            "name": "",
            "callback_number": "not-a-number",
            "urgency": "immediate",
            "affected_service": "unknown",
            "description": "",
            "short_description": "",
        }
    )

    assert result["success"] is False
    assert result["message"]
    store.create_ticket.assert_not_awaited()


@pytest.mark.asyncio
async def test_create_ticket_returns_success_and_message():
    store = AsyncMock()
    store.create_ticket.return_value = {"ticket_id": "SD-123", "work_item_id": "WI-123"}
    service_desk.configure_service_desk_store(store)
    service = "email"

    result = await service_desk.create_service_desk_ticket(
        {
            "name": "Ada Lovelace",
            "callback_number": "+14255550101",
            "urgency": "high",
            "affected_service": service,
            "description": "Cannot send messages.",
            "short_description": "Email send failure",
        }
    )

    assert result == {
        "success": True,
        "message": (
            "Service desk ticket SD-123 was created. Give one final response in the "
            "caller's language: thank them, state the ticket ID, explain that the follow-up "
            "call can now occur, ask no further question, and say goodbye."
        ),
        "ticket_id": "SD-123",
        "work_item_id": "WI-123",
        "call_control": {
            "action": "terminate_after_response",
            "reason": "normal",
            "ticket_id": "SD-123",
            "work_item_id": "WI-123",
        },
    }
    store.create_ticket.assert_awaited_once_with(
        name="Ada Lovelace",
        callback_number="+14255550101",
        urgency="high",
        affected_service=service,
        description="Cannot send messages.",
        short_description="Email send failure",
        intake_call_id=None,
        intake_session_id=None,
    )


@pytest.mark.asyncio
async def test_create_ticket_returns_failure_when_persistence_fails():
    store = AsyncMock()
    store.create_ticket.side_effect = RuntimeError("database unavailable")
    service_desk.configure_service_desk_store(store)

    result = await service_desk.create_service_desk_ticket(
        {
            "name": "Ada Lovelace",
            "callback_number": "+14255550101",
            "urgency": "high",
            "affected_service": "email",
            "description": "Cannot send messages.",
            "short_description": "Email send failure",
        }
    )

    assert result["success"] is False
    assert "could not be created" in result["message"].lower()


@pytest.mark.asyncio
async def test_record_confirmation_can_append_correction_note():
    store = AsyncMock()
    store.record_confirmation.return_value = {"ticket_id": "SD-123", "confirmed": False}
    store.append_correction_note.return_value = {"ticket_id": "SD-123"}
    service_desk.configure_service_desk_store(store)

    result = await service_desk.record_service_desk_confirmation(
        {
            "ticket_id": "SD-123",
            "work_item_id": "WI-123",
            "call_id": "call-123",
            "confirmed": False,
            "correction_note": "The callback number was repeated incorrectly.",
        }
    )

    assert result["success"] is True
    assert result["message"]
    store.record_confirmation.assert_not_awaited()
    store.append_correction_note.assert_awaited_once_with(
        "SD-123",
        "The callback number was repeated incorrectly.",
        work_item_id="WI-123",
        call_id="call-123",
    )


@pytest.mark.asyncio
async def test_tools_fail_cleanly_until_store_is_configured():
    result = await service_desk.create_service_desk_ticket(
        {
            "name": "Ada Lovelace",
            "callback_number": "+14255550101",
            "urgency": "high",
            "affected_service": "email",
            "description": "Cannot send messages.",
            "short_description": "Email send failure",
        }
    )

    assert result["success"] is False
    assert "not configured" in result["message"].lower()


@pytest.mark.asyncio
async def test_list_services_returns_dynamic_names_without_phone_numbers():
    store = AsyncMock()
    store.get_configuration.return_value = {
        "services": [
            {
                "service_id": "svc-1",
                "name": "Identity Platform",
                "phone_number": "+14255550999",
            }
        ]
    }
    service_desk.configure_service_desk_store(store)

    result = await service_desk.list_service_desk_services({})

    assert result == {
        "success": True,
        "message": "Configured service names were loaded.",
        "services": [{"service_id": "svc-1", "name": "Identity Platform"}],
    }
