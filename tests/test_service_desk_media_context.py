import json
from types import SimpleNamespace
from unittest.mock import AsyncMock, patch

import pytest
from apps.artagent.backend.api.v1.endpoints.media import (
    _create_media_handler,
    _resolve_call_context,
)
from src.enums.stream_modes import StreamMode


class FakeRedis:
    def __init__(self) -> None:
        self.sessions: dict[str, dict[str, str]] = {}
        self.redis_client = SimpleNamespace(expire=lambda *_: True)

    def get_session_data(self, key: str) -> dict[str, str]:
        return self.sessions.get(key, {})

    async def get_session_data_async(self, key: str) -> dict[str, str]:
        return self.sessions.get(key, {})

    async def store_session_data_async(self, key: str, value: dict[str, str]) -> bool:
        self.sessions[key] = value
        return True


def make_websocket() -> SimpleNamespace:
    return SimpleNamespace(
        state=SimpleNamespace(),
        app=SimpleNamespace(state=SimpleNamespace(redis=FakeRedis())),
        close=AsyncMock(),
    )


def service_desk_context() -> dict[str, object]:
    return {
        "scenario": "service_desk",
        "active_agent": "StandbyConfirmationAgent",
        "start_agent": "StandbyConfirmationAgent",
        "ticket_id": "SD-1",
        "work_item_id": "WI-1",
        "ticket": {"ticket_id": "SD-1", "short_description": "VPN outage"},
        "work_item": {"work_item_id": "WI-1", "attempt_count": 1},
    }


@pytest.mark.asyncio
async def test_media_handler_receives_scenario_and_corememory_context() -> None:
    websocket = make_websocket()
    handler = object()

    with patch(
        "apps.artagent.backend.api.v1.endpoints.media.VoiceHandler.create",
        new=AsyncMock(return_value=handler),
    ) as create_handler:
        result = await _create_media_handler(
            websocket=websocket,
            call_connection_id="call-1",
            session_id="session-1",
            stream_mode=StreamMode.MEDIA,
            call_context=service_desk_context(),
        )

    assert result is handler
    config = create_handler.await_args.args[0]
    assert config.scenario == "service_desk"
    corememory = json.loads(
        websocket.app.state.redis.sessions["session:call-1"]["corememory"]
    )
    assert corememory["active_agent"] == "StandbyConfirmationAgent"
    assert corememory["ticket"]["ticket_id"] == "SD-1"


@pytest.mark.asyncio
async def test_voicelive_handler_receives_scenario_and_corememory_context() -> None:
    websocket = make_websocket()
    handler = object()

    with patch(
        "apps.artagent.backend.api.v1.endpoints.media.consume_voicelive_call_warmup",
        new=AsyncMock(return_value=None),
    ), patch(
        "apps.artagent.backend.api.v1.endpoints.media.VoiceLiveSDKHandler",
        return_value=handler,
    ):
        result = await _create_media_handler(
            websocket=websocket,
            call_connection_id="call-1",
            session_id="session-1",
            stream_mode=StreamMode.VOICE_LIVE,
            call_context=service_desk_context(),
        )

    assert result is handler
    assert websocket.state.scenario == "service_desk"
    assert websocket.state.cm.get_value_from_corememory("active_agent") == (
        "StandbyConfirmationAgent"
    )
    assert websocket.state.cm.get_value_from_corememory("ticket")["ticket_id"] == "SD-1"


@pytest.mark.asyncio
async def test_voicelive_media_session_inherits_inbound_caller_context() -> None:
    websocket = make_websocket()
    redis = websocket.app.state.redis
    redis.sessions["session:call-1"] = {
        "corememory": json.dumps(
            {
                "call_direction": "inbound",
                "caller_id": "+1987654321",
            }
        )
    }
    context = await _resolve_call_context(
        websocket.app.state,
        "call-1",
        "media_call-1",
    )

    with patch(
        "apps.artagent.backend.api.v1.endpoints.media.consume_voicelive_call_warmup",
        new=AsyncMock(return_value=None),
    ), patch(
        "apps.artagent.backend.api.v1.endpoints.media.VoiceLiveSDKHandler",
        return_value=object(),
    ):
        await _create_media_handler(
            websocket=websocket,
            call_connection_id="call-1",
            session_id="media_call-1",
            stream_mode=StreamMode.VOICE_LIVE,
            call_context=context,
        )

    assert websocket.state.cm.get_value_from_corememory("caller_id") == "+1987654321"


@pytest.mark.asyncio
async def test_call_and_session_contexts_are_merged_for_cross_worker_media() -> None:
    websocket = make_websocket()
    redis = websocket.app.state.redis
    redis.sessions["session:call-1"] = {
        "corememory": json.dumps(
            {
                "call_direction": "inbound",
                "caller_id": "+1987654321",
                "scenario": "legacy-call-scenario",
            }
        )
    }
    redis.sessions["session:session-1"] = {
        "corememory": json.dumps(
            {
                "scenario": "service_desk",
                "active_agent": "ServiceDeskIntakeAgent",
            }
        )
    }

    context = await _resolve_call_context(
        websocket.app.state,
        "call-1",
        "session-1",
    )

    assert context["caller_id"] == "+1987654321"
    assert context["scenario"] == "service_desk"
    assert context["active_agent"] == "ServiceDeskIntakeAgent"
