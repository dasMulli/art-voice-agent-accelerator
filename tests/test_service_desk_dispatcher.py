import asyncio
import json
from types import SimpleNamespace
from unittest.mock import AsyncMock, MagicMock, patch

import pytest
from apps.artagent.backend.src.services.service_desk.dispatcher import ServiceDeskDispatcher


class FakeRedis:
    def __init__(self) -> None:
        self.values: dict[str, str] = {}
        self.sessions: dict[str, dict[str, str]] = {}
        self.redis_client = SimpleNamespace(expire=lambda *_: True)

    async def set_value_async(self, key: str, value: str, **_: object) -> None:
        self.values[key] = value

    async def get_value_async(self, key: str) -> str | None:
        return self.values.get(key)

    async def store_session_data_async(self, key: str, value: dict[str, str]) -> bool:
        self.sessions[key] = value
        return True

    def get_session_data(self, key: str) -> dict[str, str]:
        return self.sessions.get(key, {})


def make_work_item() -> dict[str, object]:
    return {
        "work_item_id": "WI-1",
        "ticket_id": "SD-1",
        "callback_number": "+14255550101",
        "standby_number": "+14255550201",
        "attempt_count": 0,
    }


@pytest.mark.asyncio
async def test_dispatch_once_claims_calls_and_attaches_confirmation_context() -> None:
    work_item = make_work_item()
    ticket = {
        "ticket_id": "SD-1",
        "short_description": "VPN outage",
        "affected_service": "vpn",
    }
    store = SimpleNamespace(
        expire_overdue=AsyncMock(return_value=0),
        claim_due_work=AsyncMock(side_effect=[work_item, None]),
        get_ticket=AsyncMock(return_value=ticket),
        mark_attempt_started=AsyncMock(return_value={**work_item, "status": "calling"}),
        release_after_failure=AsyncMock(),
        renew_call_lease=AsyncMock(return_value=True),
        list_work_items=AsyncMock(return_value=[]),
    )
    lifecycle_handler = SimpleNamespace(
        start_outbound_call=AsyncMock(
            return_value={"status": "success", "callId": "call-1"}
        )
    )
    redis = FakeRedis()
    conn_manager = SimpleNamespace(set_call_context=AsyncMock())
    standby_agent = MagicMock(name="standby-agent")
    app_state = SimpleNamespace(
        unified_agents={"StandbyConfirmationAgent": standby_agent}
    )
    dispatcher = ServiceDeskDispatcher(
        store=store,
        acs_caller=object(),
        redis_mgr=redis,
        conn_manager=conn_manager,
        app_state=app_state,
        lifecycle_handler=lifecycle_handler,
        worker_id="worker-1",
    )

    with patch(
        "apps.artagent.backend.src.services.service_desk.dispatcher.set_session_agent"
    ) as set_session_agent, patch(
        "apps.artagent.backend.src.services.service_desk.dispatcher."
        "persist_session_agents_to_redis",
        new=AsyncMock(),
    ) as persist_agent, patch.object(
        dispatcher,
        "_start_voicelive_warmup",
    ):
        await dispatcher.dispatch_once()

    lifecycle_handler.start_outbound_call.assert_awaited_once()
    assert (
        lifecycle_handler.start_outbound_call.await_args.kwargs["target_number"]
        == "+14255550201"
    )
    store.mark_attempt_started.assert_awaited_once_with(
        "WI-1",
        "worker-1",
        "call-1",
        attempt_number=1,
    )
    context = conn_manager.set_call_context.await_args.args[1]
    assert context["scenario"] == "service_desk"
    assert context["active_agent"] == "StandbyConfirmationAgent"
    assert context["start_agent"] == "StandbyConfirmationAgent"
    assert context["ticket"] == ticket
    assert context["work_item"]["work_item_id"] == "WI-1"
    assert context["call_id"] == "call-1"
    assert context["session_id"].startswith("service-desk-WI-1-")
    set_session_agent.assert_called_once_with(
        context["session_id"], standby_agent, set_active=True
    )
    persist_agent.assert_awaited_once_with(context["session_id"])

    mapping = json.loads(redis.values["service_desk:call:call-1"])
    assert mapping == {
        "work_item_id": "WI-1",
        "ticket_id": "SD-1",
        "worker_id": "worker-1",
    }
    corememory = json.loads(
        redis.sessions[f"session:{context['session_id']}"]["corememory"]
    )
    assert corememory["scenario_name"] == "service_desk"
    assert corememory["active_agent"] == "StandbyConfirmationAgent"
    call_corememory = json.loads(redis.sessions["session:call-1"]["corememory"])
    assert call_corememory["ticket_id"] == "SD-1"
    assert call_corememory["work_item_id"] == "WI-1"


@pytest.mark.asyncio
async def test_failed_initiation_requeues_claimed_work() -> None:
    work_item = make_work_item()
    store = SimpleNamespace(
        expire_overdue=AsyncMock(return_value=0),
        claim_due_work=AsyncMock(side_effect=[work_item, None]),
        get_ticket=AsyncMock(return_value={"ticket_id": "SD-1"}),
        mark_attempt_started=AsyncMock(),
        release_after_failure=AsyncMock(),
        renew_call_lease=AsyncMock(return_value=True),
        list_work_items=AsyncMock(return_value=[]),
    )
    lifecycle_handler = SimpleNamespace(
        start_outbound_call=AsyncMock(
            return_value={"status": "failed", "message": "busy"}
        )
    )
    dispatcher = ServiceDeskDispatcher(
        store=store,
        acs_caller=object(),
        redis_mgr=FakeRedis(),
        conn_manager=SimpleNamespace(set_call_context=AsyncMock()),
        app_state=SimpleNamespace(
            unified_agents={"StandbyConfirmationAgent": MagicMock()}
        ),
        lifecycle_handler=lifecycle_handler,
        worker_id="worker-1",
    )

    with patch(
        "apps.artagent.backend.src.services.service_desk.dispatcher.set_session_agent"
    ), patch(
        "apps.artagent.backend.src.services.service_desk.dispatcher."
        "persist_session_agents_to_redis",
        new=AsyncMock(),
    ):
        await dispatcher.dispatch_once()

    store.release_after_failure.assert_awaited_once_with(
        "WI-1", "worker-1", "busy"
    )
    store.mark_attempt_started.assert_not_awaited()


@pytest.mark.asyncio
async def test_disconnect_uses_durable_mapping_after_dispatcher_recreation() -> None:
    redis = FakeRedis()
    await redis.set_value_async(
        "service_desk:call:call-1",
        json.dumps(
            {
                "work_item_id": "WI-1",
                "ticket_id": "SD-1",
                "worker_id": "worker-original",
            }
        ),
    )
    store = SimpleNamespace(release_after_disconnect=AsyncMock())
    dispatcher = ServiceDeskDispatcher(
        store=store,
        acs_caller=object(),
        redis_mgr=redis,
        conn_manager=SimpleNamespace(),
        app_state=SimpleNamespace(),
        worker_id="worker-new",
    )

    await dispatcher.handle_call_disconnected("call-1", "remote hangup")

    store.release_after_disconnect.assert_awaited_once_with(
        "WI-1",
        "worker-original",
        "remote hangup",
        call_id="call-1",
    )


@pytest.mark.asyncio
async def test_incomplete_durable_mapping_is_ignored() -> None:
    redis = FakeRedis()
    await redis.set_value_async(
        "service_desk:call:call-1",
        json.dumps({"work_item_id": "WI-1"}),
    )
    store = SimpleNamespace(release_after_disconnect=AsyncMock())
    dispatcher = ServiceDeskDispatcher(
        store=store,
        acs_caller=object(),
        redis_mgr=redis,
        conn_manager=SimpleNamespace(),
        app_state=SimpleNamespace(),
    )

    await dispatcher.handle_call_disconnected("call-1", "remote hangup")

    store.release_after_disconnect.assert_not_awaited()


@pytest.mark.asyncio
async def test_unassociated_call_is_terminated_before_claim_release() -> None:
    call_connection = SimpleNamespace(hang_up=MagicMock())
    acs_caller = SimpleNamespace(
        get_call_connection=MagicMock(return_value=call_connection)
    )
    store = SimpleNamespace(
        release_after_failure=AsyncMock(side_effect=[None, {"status": "retry"}])
    )
    dispatcher = ServiceDeskDispatcher(
        store=store,
        acs_caller=acs_caller,
        redis_mgr=FakeRedis(),
        conn_manager=SimpleNamespace(),
        app_state=SimpleNamespace(),
        worker_id="worker-1",
    )

    await dispatcher._compensate_unassociated_call("call-1", "WI-1")

    call_connection.hang_up.assert_called_once_with(is_for_everyone=True)
    assert store.release_after_failure.await_args_list[0].kwargs == {
        "call_id": "call-1"
    }
    assert store.release_after_failure.await_args_list[1].kwargs == {}


@pytest.mark.asyncio
async def test_dispatch_once_rehydrates_and_renews_active_calls() -> None:
    active = {
        "work_item_id": "WI-1",
        "ticket_id": "SD-1",
        "call_id": "call-1",
        "lease_owner": "worker-original",
    }
    store = SimpleNamespace(
        list_work_items=AsyncMock(return_value=[active]),
        renew_call_lease=AsyncMock(return_value=True),
        expire_overdue=AsyncMock(return_value=0),
        claim_due_work=AsyncMock(return_value=None),
    )
    dispatcher = ServiceDeskDispatcher(
        store=store,
        acs_caller=object(),
        redis_mgr=FakeRedis(),
        conn_manager=SimpleNamespace(),
        app_state=SimpleNamespace(),
        worker_id="worker-new",
    )

    await dispatcher.dispatch_once()

    store.renew_call_lease.assert_awaited_once_with(
        "WI-1",
        "worker-original",
        "call-1",
        lease_seconds=15 * 60,
    )


@pytest.mark.asyncio
async def test_stop_cancels_dispatcher_task_cleanly() -> None:
    store = SimpleNamespace(
        expire_overdue=AsyncMock(return_value=0),
        claim_due_work=AsyncMock(return_value=None),
        renew_call_lease=AsyncMock(return_value=True),
        list_work_items=AsyncMock(return_value=[]),
    )
    dispatcher = ServiceDeskDispatcher(
        store=store,
        acs_caller=object(),
        redis_mgr=FakeRedis(),
        conn_manager=SimpleNamespace(),
        app_state=SimpleNamespace(),
        poll_interval_seconds=60,
    )

    await dispatcher.start()
    task = dispatcher._task
    await asyncio.sleep(0)
    await dispatcher.stop()

    assert task is not None
    assert task.done()
