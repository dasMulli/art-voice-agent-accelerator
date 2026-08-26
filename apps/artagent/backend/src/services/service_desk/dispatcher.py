"""Background dispatcher for service desk outbound confirmation calls."""

from __future__ import annotations

import asyncio
import json
from contextlib import suppress
from datetime import date, datetime
from typing import Any
from uuid import uuid4

from apps.artagent.backend.api.v1.handlers.acs_call_lifecycle import (
    ACSLifecycleHandler,
)
from apps.artagent.backend.src.orchestration.naming import find_agent_by_name
from apps.artagent.backend.src.orchestration.session_agents import (
    persist_session_agents_to_redis,
    set_session_agent,
)
from apps.artagent.backend.src.services.service_desk.store import (
    WORK_ITEM_EXPIRY_HOURS,
    WORK_ITEM_LEASE_SECONDS,
    ServiceDeskStore,
)
from src.enums.stream_modes import StreamMode
from src.stateful.state_managment import MemoManager
from utils.ml_logging import get_logger

logger = get_logger(__name__)

_AGENT_NAME = "StandbyConfirmationAgent"
_SCENARIO_NAME = "service_desk"
_CALL_MAPPING_PREFIX = "service_desk:call:"
_DEFAULT_POLL_SECONDS = 5.0
_MAX_CLAIMS_PER_POLL = 25


def _json_safe(value: Any) -> Any:
    """Convert persisted service desk values to JSON-compatible primitives."""
    if isinstance(value, (datetime, date)):
        return value.isoformat()
    if isinstance(value, dict):
        return {str(key): _json_safe(item) for key, item in value.items()}
    if isinstance(value, (list, tuple)):
        return [_json_safe(item) for item in value]
    if value is None or isinstance(value, (str, int, float, bool)):
        return value
    return str(value)


class ServiceDeskDispatcher:
    """Claim due callback work and initiate app-scoped ACS calls."""

    def __init__(
        self,
        *,
        store: ServiceDeskStore,
        acs_caller: Any,
        redis_mgr: Any,
        conn_manager: Any,
        app_state: Any,
        lifecycle_handler: ACSLifecycleHandler | None = None,
        worker_id: str | None = None,
        poll_interval_seconds: float = _DEFAULT_POLL_SECONDS,
    ) -> None:
        if poll_interval_seconds <= 0:
            raise ValueError("poll_interval_seconds must be positive.")
        self._store = store
        self._acs_caller = acs_caller
        self._redis = redis_mgr
        self._conn_manager = conn_manager
        self._app_state = app_state
        self._lifecycle_handler = lifecycle_handler or ACSLifecycleHandler()
        self._worker_id = worker_id or f"service-desk-{uuid4().hex}"
        self._poll_interval_seconds = poll_interval_seconds
        self._lease_seconds = WORK_ITEM_LEASE_SECONDS
        self._task: asyncio.Task[None] | None = None
        self._stop_event = asyncio.Event()
        self._call_mappings: dict[str, dict[str, str]] = {}

    async def start(self) -> None:
        """Start the dispatcher task if it is not already running."""
        if self._task and not self._task.done():
            return
        self._stop_event.clear()
        self._task = asyncio.create_task(
            self._run(),
            name="service-desk-dispatcher",
        )

    async def stop(self) -> None:
        """Cancel and await the dispatcher task."""
        self._stop_event.set()
        task = self._task
        if task is None:
            return
        task.cancel()
        with suppress(asyncio.CancelledError):
            await task
        self._task = None

    async def _run(self) -> None:
        while not self._stop_event.is_set():
            try:
                await self.dispatch_once()
            except Exception:
                logger.exception("Service desk dispatcher poll failed")

            try:
                await asyncio.wait_for(
                    self._stop_event.wait(),
                    timeout=self._poll_interval_seconds,
                )
            except TimeoutError:
                continue

    async def dispatch_once(self) -> int:
        """Expire overdue work and dispatch a bounded batch of due items."""
        await self._rehydrate_active_call_mappings()
        await self._renew_active_call_leases()
        await self._store.reconcile_intake_disconnects()
        await self._store.expire_overdue()
        dispatched = 0
        for _ in range(_MAX_CLAIMS_PER_POLL):
            work_item = await self._store.claim_due_work(
                self._worker_id,
                lease_seconds=self._lease_seconds,
            )
            if work_item is None:
                break
            if await self._dispatch_work_item(work_item):
                dispatched += 1
        return dispatched

    async def _dispatch_work_item(self, work_item: dict[str, Any]) -> bool:
        work_item_id = str(work_item["work_item_id"])
        ticket_id = str(work_item["ticket_id"])
        ticket = await self._store.get_ticket(ticket_id)
        if ticket is None:
            await self._store.release_after_failure(
                work_item_id,
                self._worker_id,
                f"ticket {ticket_id} was not found",
            )
            return False
        route = await self._store.resolve_work_item_route(work_item, ticket)
        if route is None:
            await self._store.release_after_failure(
                work_item_id,
                self._worker_id,
                "configured service route was not found",
            )
            return False

        ticket = {
            **ticket,
            "service_id": route["service_id"],
            "affected_service": route["name"],
        }
        work_item = {
            **work_item,
            "service_id": route["service_id"],
            "standby_number": route["phone_number"],
        }

        session_id = (
            f"service-desk-{work_item_id}-"
            f"{int(work_item.get('attempt_count', 0)) + 1}-{uuid4().hex[:8]}"
        )
        context = self._build_call_context(session_id, ticket, work_item)
        await self._prepare_session_context(session_id, context)

        try:
            result = await self._lifecycle_handler.start_outbound_call(
                acs_caller=self._acs_caller,
                target_number=str(route["phone_number"]),
                redis_mgr=self._redis,
                browser_session_id=session_id,
            )
        except Exception as exc:
            logger.exception(
                "Service desk call initiation raised for work item %s",
                work_item_id,
            )
            await self._store.release_after_failure(
                work_item_id,
                self._worker_id,
                str(exc) or type(exc).__name__,
            )
            return False

        if result.get("status") != "success" or not result.get("callId"):
            reason = str(result.get("message") or "call initiation failed")
            await self._store.release_after_failure(
                work_item_id,
                self._worker_id,
                reason,
            )
            return False

        call_id = str(result["callId"])
        context["call_id"] = call_id
        try:
            started = await self._store.mark_attempt_started(
                work_item_id,
                self._worker_id,
                call_id,
                attempt_number=int(work_item.get("attempt_count", 0)) + 1,
            )
        except Exception:
            logger.exception(
                "Failed to durably associate call %s with work item %s",
                call_id,
                work_item_id,
            )
            await self._compensate_unassociated_call(call_id, work_item_id)
            raise
        if started is None:
            logger.error(
                "Could not durably associate call %s with work item %s",
                call_id,
                work_item_id,
            )
            await self._compensate_unassociated_call(call_id, work_item_id)
            return False

        await self._persist_call_mapping(call_id, work_item_id, ticket_id)
        await self._conn_manager.set_call_context(call_id, context)
        await self._persist_call_memory(call_id, context)
        if result.get("streaming_mode") == str(StreamMode.VOICE_LIVE):
            self._start_voicelive_warmup(call_id, session_id)
        logger.info(
            "Service desk confirmation call started | work_item=%s call=%s",
            work_item_id,
            call_id,
        )
        return True

    async def _compensate_unassociated_call(
        self,
        call_id: str,
        work_item_id: str,
    ) -> None:
        """Terminate an untracked ACS call before making its work claim retryable."""
        call_connection = self._acs_caller.get_call_connection(call_id)
        if call_connection is None:
            logger.error(
                "Could not retrieve orphaned ACS call %s; preserving work claim %s",
                call_id,
                work_item_id,
            )
            return
        try:
            await asyncio.to_thread(call_connection.hang_up, is_for_everyone=True)
        except Exception:
            logger.exception(
                "Could not terminate orphaned ACS call %s; preserving work claim %s",
                call_id,
                work_item_id,
            )
            return

        released = await self._store.release_after_failure(
            work_item_id,
            self._worker_id,
            "call association failed",
            call_id=call_id,
        )
        if released is None:
            await self._store.release_after_failure(
                work_item_id,
                self._worker_id,
                "call association failed",
            )

    async def _rehydrate_active_call_mappings(self) -> None:
        """Restore active call ownership from Cosmos after a worker restart."""
        active_items = await self._store.list_work_items(status="calling", limit=0)
        for item in active_items:
            call_id = str(item.get("call_id") or "")
            work_item_id = str(item.get("work_item_id") or "")
            ticket_id = str(item.get("ticket_id") or "")
            worker_id = str(item.get("lease_owner") or "")
            if not all((call_id, work_item_id, ticket_id, worker_id)):
                continue
            self._call_mappings.setdefault(
                call_id,
                {
                    "work_item_id": work_item_id,
                    "ticket_id": ticket_id,
                    "worker_id": worker_id,
                },
            )

    async def _renew_active_call_leases(self) -> None:
        """Keep active calls from being reclaimed by another worker."""
        for call_id, mapping in list(self._call_mappings.items()):
            renewed = await self._store.renew_call_lease(
                mapping["work_item_id"],
                mapping["worker_id"],
                call_id,
                lease_seconds=self._lease_seconds,
            )
            if not renewed:
                self._call_mappings.pop(call_id, None)

    def _build_call_context(
        self,
        session_id: str,
        ticket: dict[str, Any],
        work_item: dict[str, Any],
    ) -> dict[str, Any]:
        return {
            "scenario": _SCENARIO_NAME,
            "scenario_name": _SCENARIO_NAME,
            "active_agent": _AGENT_NAME,
            "start_agent": _AGENT_NAME,
            "session_id": session_id,
            "browser_session_id": session_id,
            "ticket_id": str(ticket["ticket_id"]),
            "work_item_id": str(work_item["work_item_id"]),
            "ticket": _json_safe(ticket),
            "work_item": _json_safe(work_item),
        }

    async def _prepare_session_context(
        self,
        session_id: str,
        context: dict[str, Any],
    ) -> None:
        agents = getattr(self._app_state, "unified_agents", {}) or {}
        _, standby_agent = find_agent_by_name(agents, _AGENT_NAME)
        if standby_agent is None:
            raise RuntimeError(f"{_AGENT_NAME} is not loaded.")

        set_session_agent(session_id, standby_agent, set_active=True)
        await persist_session_agents_to_redis(session_id)
        await self._persist_call_memory(session_id, context)

    async def _persist_call_memory(
        self,
        memory_id: str,
        context: dict[str, Any],
    ) -> None:
        memo = MemoManager.from_redis(memory_id, self._redis)
        for key, value in context.items():
            memo.set_corememory(key, value)
        await memo.persist_to_redis_async(
            self._redis,
            ttl_seconds=WORK_ITEM_EXPIRY_HOURS * 60 * 60,
            raise_on_failure=True,
        )

    async def _persist_call_mapping(
        self,
        call_id: str,
        work_item_id: str,
        ticket_id: str,
    ) -> None:
        mapping = {
            "work_item_id": work_item_id,
            "ticket_id": ticket_id,
            "worker_id": self._worker_id,
        }
        self._call_mappings[call_id] = mapping
        await self._redis.set_value_async(
            f"{_CALL_MAPPING_PREFIX}{call_id}",
            json.dumps(mapping),
            ttl_seconds=WORK_ITEM_EXPIRY_HOURS * 60 * 60,
        )

    def _start_voicelive_warmup(self, call_id: str, session_id: str) -> None:
        from apps.artagent.backend.voice.voicelive.handler import (
            start_voicelive_call_warmup,
        )

        start_voicelive_call_warmup(
            self._app_state,
            call_connection_id=call_id,
            session_id=session_id,
            scenario_name=_SCENARIO_NAME,
        )

    async def _get_call_mapping(self, call_id: str) -> dict[str, str] | None:
        mapping = self._call_mappings.get(call_id)
        if mapping:
            return mapping
        raw = await self._redis.get_value_async(f"{_CALL_MAPPING_PREFIX}{call_id}")
        if not raw:
            return None
        try:
            if isinstance(raw, bytes):
                raw = raw.decode("utf-8")
            parsed = json.loads(str(raw))
        except (json.JSONDecodeError, TypeError, UnicodeDecodeError):
            logger.warning("Ignoring malformed service desk call mapping for %s", call_id)
            return None
        if not isinstance(parsed, dict):
            return None
        work_item_id = parsed.get("work_item_id")
        ticket_id = parsed.get("ticket_id")
        worker_id = parsed.get("worker_id")
        if not all((work_item_id, ticket_id, worker_id)):
            logger.warning("Ignoring incomplete service desk call mapping for %s", call_id)
            return None
        return {
            "work_item_id": str(work_item_id),
            "ticket_id": str(ticket_id),
            "worker_id": str(worker_id),
        }

    async def handle_create_call_failed(self, call_id: str, reason: str) -> None:
        """Requeue work associated with an ACS CreateCallFailed outcome."""
        mapping = await self._get_call_mapping(call_id)
        if mapping is None:
            return
        await self._store.release_after_failure(
            mapping["work_item_id"],
            mapping["worker_id"],
            reason or "create call failed",
            call_id=call_id,
        )
        self._call_mappings.pop(call_id, None)

    async def handle_call_disconnected(self, call_id: str, reason: str) -> None:
        """Handle outbound retry or enable follow-up after intake disconnect."""
        mapping = await self._get_call_mapping(call_id)
        if mapping is None:
            await self._store.record_intake_disconnect(call_id)
            activated = await self._store.activate_after_intake_disconnect(call_id=call_id)
            if activated is not None:
                logger.info(
                    "Service desk follow-up enabled after intake disconnect | work_item=%s call=%s",
                    activated.get("work_item_id"),
                    call_id,
                )
            return
        await self._store.release_after_disconnect(
            mapping["work_item_id"],
            mapping["worker_id"],
            reason or "call disconnected",
            call_id=call_id,
        )
        self._call_mappings.pop(call_id, None)
