import asyncio
import contextlib
from types import SimpleNamespace
from unittest.mock import AsyncMock, MagicMock, patch

import pytest
from apps.artagent.backend.voice.handler import VoiceHandler
from apps.artagent.backend.voice.shared.context import TransportType
from apps.artagent.backend.voice.shared.terminal_action import (
    TerminalAction,
    build_terminal_action,
    terminal_action_from_result,
)
from apps.artagent.backend.voice.voicelive.handler import VoiceLiveSDKHandler
from apps.artagent.backend.voice.voicelive.orchestrator import LiveOrchestrator
from fastapi.websockets import WebSocketState


def test_terminal_action_requires_successful_tool_result() -> None:
    payload = build_terminal_action(
        ticket_id="SD-1",
        work_item_id="WI-1",
    )

    assert terminal_action_from_result(
        {"success": True, "call_control": payload}
    ) == TerminalAction(
        reason="normal",
        ticket_id="SD-1",
        work_item_id="WI-1",
    )
    assert terminal_action_from_result({"success": False, "call_control": payload}) is None


@pytest.mark.asyncio
async def test_cascade_suppresses_input_and_terminates_after_tts_drain() -> None:
    handler = VoiceHandler.__new__(VoiceHandler)
    action = TerminalAction(reason="normal", ticket_id="SD-1", work_item_id="WI-1")
    websocket = MagicMock()
    handler._terminal_action = None
    handler._termination_in_progress = False
    handler._running = True
    handler._session_id = "session-1"
    handler._session_short = "ession-1"
    handler._transport = TransportType.ACS
    handler._context = SimpleNamespace(
        websocket=websocket,
        call_connection_id="call-1",
    )
    handler._app_state = SimpleNamespace(service_desk_store=None)
    handler._tts = SimpleNamespace(wait_for_playback_complete=AsyncMock(return_value=True))
    handler._stt_thread = SimpleNamespace(write_audio=MagicMock())
    handler._cancel_idle_monitor = AsyncMock()

    handler.begin_terminal_response(action)
    handler.write_audio(b"caller audio")

    with patch(
        "apps.artagent.backend.src.services.acs.session_terminator.terminate_session",
        new=AsyncMock(),
    ) as terminate:
        await handler.finish_terminal_response(action)
        await handler.finish_terminal_response(action)

    handler._stt_thread.write_audio.assert_not_called()
    handler._tts.wait_for_playback_complete.assert_awaited_once_with(timeout_s=10.0)
    terminate.assert_awaited_once()
    assert terminate.await_args.kwargs["play_goodbye"] is False
    assert terminate.await_args.kwargs["call_connection_id"] == "call-1"


@pytest.mark.asyncio
async def test_cascade_does_not_release_browser_callback_when_close_fails() -> None:
    handler = VoiceHandler.__new__(VoiceHandler)
    action = TerminalAction(reason="normal", ticket_id="SD-1", work_item_id="WI-1")
    store = SimpleNamespace(activate_after_intake_disconnect=AsyncMock())
    handler._terminal_action = None
    handler._termination_in_progress = False
    handler._running = True
    handler._session_id = "session-1"
    handler._session_short = "ession-1"
    handler._transport = TransportType.BROWSER
    handler._context = SimpleNamespace(
        websocket=MagicMock(),
        call_connection_id=None,
    )
    handler._app_state = SimpleNamespace(service_desk_store=store)
    handler._tts = None
    handler._cancel_idle_monitor = AsyncMock()

    with patch(
        "apps.artagent.backend.src.services.acs.session_terminator.terminate_session",
        new=AsyncMock(return_value=SimpleNamespace(websocket_closed=False)),
    ):
        await handler.finish_terminal_response(action)

    store.activate_after_intake_disconnect.assert_not_awaited()


@pytest.mark.asyncio
async def test_voicelive_suppresses_audio_while_terminal_response_is_pending() -> None:
    websocket = MagicMock()
    websocket.state = SimpleNamespace()
    websocket.app.state = SimpleNamespace()
    handler = VoiceLiveSDKHandler(
        websocket=websocket,
        session_id="session-1",
        call_connection_id="call-1",
    )
    handler._running = True
    handler._connection = SimpleNamespace(input_audio_buffer=SimpleNamespace(append=AsyncMock()))

    await handler.begin_terminal_response(
        TerminalAction(reason="normal", ticket_id="SD-1", work_item_id="WI-1")
    )
    await handler.handle_audio_data('{"kind":"AudioData","audioData":{"data":"AQI="}}')

    handler._connection.input_audio_buffer.append.assert_not_awaited()
    assert handler._terminal_timeout_task is not None
    handler._terminal_timeout_task.cancel()
    with contextlib.suppress(asyncio.CancelledError):
        await handler._terminal_timeout_task


@pytest.mark.asyncio
async def test_voicelive_terminal_tool_result_arms_post_tool_response() -> None:
    connection = SimpleNamespace(
        response=SimpleNamespace(create=AsyncMock(), cancel=AsyncMock()),
        conversation=SimpleNamespace(
            item=SimpleNamespace(create=AsyncMock()),
        ),
    )
    messenger = MagicMock()
    messenger.session_id = "session-1"
    messenger.set_active_agent = MagicMock()
    messenger.notify_tool_start = AsyncMock()
    messenger.notify_tool_end = AsyncMock()
    messenger.prepare_terminal_response = AsyncMock()
    messenger.mark_terminal_response_started = AsyncMock()
    messenger.advance_turn_for_tool = MagicMock()
    agent = SimpleNamespace(name="Concierge", description="Intake agent")

    with patch("apps.artagent.backend.voice.voicelive.orchestrator.initialize_tools"):
        orchestrator = LiveOrchestrator(
            conn=connection,
            agents={"Concierge": agent},
            start_agent="Concierge",
            messenger=messenger,
            call_connection_id="call-1",
        )

    orchestrator._handoff_service = MagicMock()
    orchestrator._handoff_service.is_handoff.return_value = False
    terminal_result = {
        "success": True,
        "call_control": build_terminal_action(
            ticket_id="SD-1",
            work_item_id="WI-1",
        ),
    }

    with patch(
        "apps.artagent.backend.voice.voicelive.orchestrator.execute_tool",
        new=AsyncMock(return_value=terminal_result),
    ):
        was_handoff = await orchestrator._execute_tool_call(
            "tool-call-1",
            "create_service_desk_ticket",
            "{}",
        )

    action = TerminalAction(reason="normal", ticket_id="SD-1", work_item_id="WI-1")
    assert was_handoff is False
    assert orchestrator._pending_terminal_action == action
    messenger.prepare_terminal_response.assert_awaited_once_with(action)

    orchestrator._emit_model_metrics = MagicMock()
    orchestrator._update_session_context = AsyncMock()
    orchestrator._schedule_background_sync = MagicMock()
    orchestrator._schedule_throttled_session_update = MagicMock()
    await orchestrator._handle_response_done(SimpleNamespace(response=SimpleNamespace(id="r1")))

    connection.response.create.assert_awaited_once()
    messenger.mark_terminal_response_started.assert_awaited_once_with(action)
    assert orchestrator._pending_terminal_action is None


@pytest.mark.asyncio
async def test_voicelive_no_audio_timeout_starts_termination() -> None:
    handler = VoiceLiveSDKHandler.__new__(VoiceLiveSDKHandler)
    handler.session_id = "session-1"
    handler._terminal_last_progress_at = 1.0
    handler._start_terminal_termination = MagicMock()

    with patch(
        "apps.artagent.backend.voice.voicelive.handler.time.monotonic",
        return_value=22.0,
    ):
        await handler._terminal_response_timeout()

    handler._start_terminal_termination.assert_called_once_with()


@pytest.mark.asyncio
async def test_voicelive_waits_for_queued_audio_playback_before_termination() -> None:
    handler = VoiceLiveSDKHandler.__new__(VoiceLiveSDKHandler)
    handler._terminal_response_started = True
    handler._terminal_action = TerminalAction(reason="normal", ticket_id="SD-1")
    handler._terminal_response_id = "final-response"
    handler._terminal_last_progress_at = None
    handler._terminal_playback_deadline = 0.0
    handler._terminate_after_terminal_response = AsyncMock()

    with patch(
        "apps.artagent.backend.voice.voicelive.handler.time.monotonic",
        return_value=100.0,
    ):
        handler._record_terminal_audio_progress(
            "final-response",
            duration_seconds=0.5,
        )
        with patch(
            "apps.artagent.backend.voice.voicelive.handler.asyncio.sleep",
            new=AsyncMock(),
        ) as sleep:
            await handler._terminate_after_terminal_playback()

    sleep.assert_awaited_once_with(0.75)
    handler._terminate_after_terminal_response.assert_awaited_once_with()


@pytest.mark.asyncio
async def test_voicelive_browser_disconnect_enables_callback_work() -> None:
    store = SimpleNamespace(activate_after_intake_disconnect=AsyncMock())
    websocket = MagicMock()
    websocket.application_state = WebSocketState.DISCONNECTED
    websocket.client_state = WebSocketState.DISCONNECTED
    websocket.app.state = SimpleNamespace(service_desk_store=store)
    handler = VoiceLiveSDKHandler(
        websocket=websocket,
        session_id="session-1",
        call_connection_id="call-1",
        transport="realtime",
    )
    handler._terminal_action = TerminalAction(
        reason="normal",
        ticket_id="SD-1",
        work_item_id="WI-1",
    )

    await handler._activate_browser_follow_up()

    store.activate_after_intake_disconnect.assert_awaited_once_with(
        session_id="session-1",
        work_item_id="WI-1",
    )


def test_voicelive_only_accepts_matching_final_audio_completion() -> None:
    handler = VoiceLiveSDKHandler.__new__(VoiceLiveSDKHandler)
    handler._terminal_response_started = True
    handler._terminal_action = TerminalAction(reason="normal", ticket_id="SD-1")
    handler._terminal_response_id = "final-response"

    assert handler._is_terminal_response_complete("earlier-response") is False
    assert handler._is_terminal_response_complete("final-response") is True
