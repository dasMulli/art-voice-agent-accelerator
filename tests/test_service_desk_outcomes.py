from types import SimpleNamespace
from unittest.mock import AsyncMock

import pytest
from apps.artagent.backend.api.v1.events.acs_events import CallEventHandlers
from apps.artagent.backend.api.v1.events.types import (
    ACSEventTypes,
    CallEventContext,
)
from azure.core.messaging import CloudEvent


def make_context(event_type: str) -> CallEventContext:
    event = CloudEvent(
        source="test",
        type=event_type,
        data={
            "callConnectionId": "call-1",
            "callConnectionState": "disconnected",
            "resultInformation": {"message": "callee unavailable"},
        },
    )
    dispatcher = SimpleNamespace(
        handle_call_disconnected=AsyncMock(),
        handle_create_call_failed=AsyncMock(),
    )
    return CallEventContext(
        event=event,
        call_connection_id="call-1",
        event_type=event_type,
        app_state=SimpleNamespace(
            service_desk_dispatcher=dispatcher,
            conn_manager=None,
        ),
    )


@pytest.mark.asyncio
async def test_call_disconnected_notifies_service_desk_dispatcher() -> None:
    context = make_context(ACSEventTypes.CALL_DISCONNECTED)

    await CallEventHandlers.handle_call_disconnected(context)

    context.app_state.service_desk_dispatcher.handle_call_disconnected.assert_awaited_once_with(
        "call-1", "disconnected"
    )


@pytest.mark.asyncio
async def test_create_call_failed_notifies_service_desk_dispatcher() -> None:
    context = make_context(ACSEventTypes.CREATE_CALL_FAILED)

    await CallEventHandlers.handle_create_call_failed(context)

    context.app_state.service_desk_dispatcher.handle_create_call_failed.assert_awaited_once_with(
        "call-1", "callee unavailable"
    )
