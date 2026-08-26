"""Cosmos-backed persistence for service desk tickets and callback work."""

from __future__ import annotations

import asyncio
from datetime import UTC, datetime, timedelta
from typing import Any
from uuid import uuid4

from apps.artagent.backend.src.services.service_desk.domain import (
    DEFAULT_RETRY_INTERVALS_MINUTES,
    Urgency,
    default_service_routes,
    normalize_e164,
    validate_retry_intervals,
    validate_service_routes,
)
from pymongo import ReturnDocument
from utils.ml_logging import get_logger

CONFIG_DOCUMENT_TYPE = "service_desk_configuration"
CONFIG_DOCUMENT_ID = "service_desk_configuration"
TICKET_DOCUMENT_TYPE = "service_desk_ticket"
WORK_ITEM_DOCUMENT_TYPE = "service_desk_work_item"
# Retained for compatibility with existing imports and stored documents.
WORK_ITEM_RETRY_SECONDS = 600
WORK_ITEM_EXPIRY_HOURS = 24
WORK_ITEM_LEASE_SECONDS = 15 * 60
INTAKE_DISCONNECT_MARKER_TTL_SECONDS = 24 * 60 * 60
_INTAKE_DISCONNECT_MARKER_PREFIX = "service_desk:intake_disconnected:"
_ACTIVE_WORK_STATUSES = [
    "awaiting_intake_disconnect",
    "pending",
    "retry",
    "claimed",
    "calling",
]
logger = get_logger("service_desk.store")


def _utc_now() -> datetime:
    return datetime.now(UTC)


class ServiceDeskConfigurationConflictError(RuntimeError):
    """Raised when a configuration update uses a stale revision."""


class ServiceDeskServiceInUseError(RuntimeError):
    """Raised when configuration removal would orphan active callback work."""


class ServiceDeskStore:
    """Expose a configured synchronous Cosmos manager through async operations."""

    def __init__(self, cosmos_manager: Any, redis_mgr: Any | None = None) -> None:
        if cosmos_manager is None or getattr(cosmos_manager, "collection", None) is None:
            raise ValueError("A configured Cosmos manager with a collection is required.")
        self._manager = cosmos_manager
        self._collection = cosmos_manager.collection
        self._redis = redis_mgr

    async def ensure_configuration(self) -> dict[str, Any]:
        """Create default configuration once and migrate active legacy references."""
        now = _utc_now()
        configuration = await asyncio.to_thread(
            self._collection.find_one_and_update,
            {"_id": CONFIG_DOCUMENT_ID},
            {
                "$setOnInsert": {
                    "_id": CONFIG_DOCUMENT_ID,
                    "document_type": CONFIG_DOCUMENT_TYPE,
                    "revision": 1,
                    "retry_intervals_minutes": list(DEFAULT_RETRY_INTERVALS_MINUTES),
                    "services": default_service_routes(),
                    "created_at": now,
                    "updated_at": now,
                }
            },
            upsert=True,
            return_document=ReturnDocument.AFTER,
        )
        if not isinstance(configuration, dict):
            raise RuntimeError("Service desk configuration could not be initialized.")
        await self._migrate_active_service_ids(configuration)
        return self._public_configuration(configuration)

    async def get_configuration(self, *, include_disabled: bool = False) -> dict[str, Any]:
        """Return the current service desk configuration."""
        configuration = await asyncio.to_thread(
            self._collection.find_one,
            {"_id": CONFIG_DOCUMENT_ID, "document_type": CONFIG_DOCUMENT_TYPE},
        )
        if configuration is None:
            await self.ensure_configuration()
            configuration = await asyncio.to_thread(
                self._collection.find_one,
                {"_id": CONFIG_DOCUMENT_ID, "document_type": CONFIG_DOCUMENT_TYPE},
            )
        if not isinstance(configuration, dict):
            raise RuntimeError("Service desk configuration is unavailable.")
        return self._public_configuration(configuration, include_disabled=include_disabled)

    async def update_configuration(
        self,
        *,
        expected_revision: int,
        retry_intervals_minutes: list[int],
        services: list[dict[str, Any]],
    ) -> dict[str, Any]:
        """Replace editable configuration fields when the revision still matches."""
        current = await self.get_configuration(include_disabled=True)
        if expected_revision != current["revision"]:
            raise ServiceDeskConfigurationConflictError(
                "Service desk configuration changed; reload and try again."
            )

        current_services = current["services"]
        existing_ids = {str(service["service_id"]) for service in current_services}
        normalized_intervals = validate_retry_intervals(retry_intervals_minutes)
        normalized_services = validate_service_routes(
            services,
            existing_service_ids=existing_ids,
        )
        submitted_ids = {service["service_id"] for service in normalized_services}
        removed_ids = {
            str(service["service_id"])
            for service in current_services
            if service.get("enabled", True) and service["service_id"] not in submitted_ids
        }
        if removed_ids:
            active_count = await asyncio.to_thread(
                self._collection.count_documents,
                {
                    "document_type": WORK_ITEM_DOCUMENT_TYPE,
                    "status": {"$in": _ACTIVE_WORK_STATUSES},
                    "service_id": {"$in": sorted(removed_ids)},
                },
            )
            if active_count:
                raise ServiceDeskServiceInUseError(
                    "One or more removed services are still used by open callback work."
                )

        disabled_services = [
            {**service, "enabled": False}
            for service in current_services
            if service["service_id"] not in submitted_ids
        ]
        now = _utc_now()
        updated = await asyncio.to_thread(
            self._collection.find_one_and_update,
            {
                "_id": CONFIG_DOCUMENT_ID,
                "document_type": CONFIG_DOCUMENT_TYPE,
                "revision": expected_revision,
            },
            {
                "$set": {
                    "retry_intervals_minutes": normalized_intervals,
                    "services": [*normalized_services, *disabled_services],
                    "updated_at": now,
                },
                "$inc": {"revision": 1},
            },
            return_document=ReturnDocument.AFTER,
        )
        if updated is None:
            raise ServiceDeskConfigurationConflictError(
                "Service desk configuration changed; reload and try again."
            )
        return self._public_configuration(updated)

    async def resolve_service_route(
        self,
        value: str,
        *,
        include_disabled: bool = False,
    ) -> dict[str, Any] | None:
        """Resolve a service by immutable ID or case-insensitive display name."""
        candidate = str(value or "").strip()
        if not candidate:
            return None
        configuration = await self.get_configuration(include_disabled=include_disabled)
        candidate_name = candidate.casefold()
        return next(
            (
                service
                for service in configuration["services"]
                if service["service_id"] == candidate
                or str(service["name"]).casefold() == candidate_name
            ),
            None,
        )

    async def resolve_work_item_route(
        self,
        work_item: dict[str, Any],
        ticket: dict[str, Any] | None = None,
    ) -> dict[str, Any] | None:
        """Resolve the current route for callback work, including legacy references."""
        configuration = await self.get_configuration(include_disabled=True)
        services = configuration["services"]
        service_id = str(work_item.get("service_id") or "").strip()
        if service_id:
            route = next(
                (service for service in services if service["service_id"] == service_id),
                None,
            )
            if route is not None:
                return route

        standby_number = str(work_item.get("standby_number") or "").strip()
        if standby_number:
            route = next(
                (service for service in services if service["phone_number"] == standby_number),
                None,
            )
            if route is not None:
                return route

        affected_service = str((ticket or {}).get("affected_service") or "").strip().casefold()
        if affected_service:
            return next(
                (
                    service
                    for service in services
                    if str(service["name"]).casefold() == affected_service
                ),
                None,
            )
        return None

    def _public_configuration(
        self,
        configuration: dict[str, Any],
        *,
        include_disabled: bool = False,
    ) -> dict[str, Any]:
        services = [
            dict(service)
            for service in configuration.get("services", [])
            if include_disabled or service.get("enabled", True)
        ]
        return {
            "revision": int(configuration["revision"]),
            "retry_intervals_minutes": list(configuration["retry_intervals_minutes"]),
            "services": services,
            "created_at": configuration["created_at"],
            "updated_at": configuration["updated_at"],
        }

    async def _migrate_active_service_ids(self, configuration: dict[str, Any]) -> None:
        """Attach stable IDs to legacy active work and ticket snapshots."""
        for service in configuration.get("services", []):
            if not service.get("enabled", True):
                continue
            service_id = str(service["service_id"])
            await asyncio.to_thread(
                self._collection.update_many,
                {
                    "document_type": WORK_ITEM_DOCUMENT_TYPE,
                    "status": {"$in": _ACTIVE_WORK_STATUSES},
                    "service_id": {"$exists": False},
                    "standby_number": service["phone_number"],
                },
                {"$set": {"service_id": service_id}},
            )
            await asyncio.to_thread(
                self._collection.update_many,
                {
                    "document_type": TICKET_DOCUMENT_TYPE,
                    "service_id": {"$exists": False},
                    "affected_service": service["name"],
                },
                {"$set": {"service_id": service_id}},
            )

    async def create_ticket(
        self,
        name: str,
        callback_number: str,
        urgency: str,
        affected_service: str,
        description: str,
        short_description: str,
        *,
        intake_call_id: str | None = None,
        intake_session_id: str | None = None,
    ) -> dict[str, Any]:
        """Create an immutable ticket and its initial callback work item."""
        normalized_callback = normalize_e164(callback_number)
        normalized_urgency = Urgency(urgency).value
        configuration = await self.get_configuration()
        affected_service_key = str(affected_service or "").strip().casefold()
        service_route = next(
            (
                service
                for service in configuration["services"]
                if service["service_id"] == affected_service
                or str(service["name"]).casefold() == affected_service_key
            ),
            None,
        )
        if service_route is None:
            raise ValueError(f"Unsupported affected service: {affected_service}")
        service_id = str(service_route["service_id"])
        service_label = str(service_route["name"])
        standby_number = str(service_route["phone_number"])
        initial_retry_seconds = int(configuration["retry_intervals_minutes"][0]) * 60
        required = {
            "name": name,
            "description": description,
            "short_description": short_description,
        }
        missing = [field for field, value in required.items() if not str(value or "").strip()]
        if missing:
            raise ValueError(f"Required fields are missing: {', '.join(missing)}")

        now = _utc_now()
        ticket_id = f"SD-{uuid4().hex[:12].upper()}"
        work_item_id = f"WI-{uuid4().hex[:12].upper()}"
        ticket = {
            "_id": ticket_id,
            "document_type": TICKET_DOCUMENT_TYPE,
            "ticket_id": ticket_id,
            "work_item_id": work_item_id,
            "name": name.strip(),
            "callback_number": normalized_callback,
            "urgency": normalized_urgency,
            "service_id": service_id,
            "affected_service": service_label,
            "description": description.strip(),
            "short_description": short_description.strip(),
            "confirmation_status": "pending",
            "correction_notes": [],
            "created_at": now,
            "updated_at": now,
        }
        waits_for_disconnect = bool(intake_call_id or intake_session_id)
        work_item = {
            "_id": work_item_id,
            "document_type": WORK_ITEM_DOCUMENT_TYPE,
            "work_item_id": work_item_id,
            "ticket_id": ticket_id,
            "callback_number": normalized_callback,
            "service_id": service_id,
            "standby_number": standby_number,
            "status": "awaiting_intake_disconnect" if waits_for_disconnect else "pending",
            "attempt_count": 0,
            "attempt_history": [],
            "retry_interval_seconds": initial_retry_seconds,
            "next_attempt_at": None if waits_for_disconnect else now,
            "intake_call_id": intake_call_id,
            "intake_session_id": intake_session_id,
            "expires_at": now + timedelta(hours=WORK_ITEM_EXPIRY_HOURS),
            "created_at": now,
            "updated_at": now,
        }

        try:
            await asyncio.to_thread(
                self._collection.insert_many,
                [ticket, work_item],
                ordered=True,
            )
        except Exception:
            await asyncio.to_thread(
                self._collection.delete_many,
                {"_id": {"$in": [ticket_id, work_item_id]}},
            )
            raise

        if intake_call_id and await self._intake_disconnect_was_recorded(intake_call_id):
            try:
                await self.activate_after_intake_disconnect(work_item_id=work_item_id)
            except Exception:
                logger.exception(
                    "Failed to reconcile service desk intake disconnect after ticket creation "
                    "| work_item=%s call=%s",
                    work_item_id,
                    intake_call_id,
                )
        return ticket

    async def record_intake_disconnect(self, call_id: str) -> None:
        """Record disconnect before activation so ticket creation cannot miss the event."""
        if not call_id or self._redis is None:
            return
        try:
            await self._redis.set_value_async(
                f"{_INTAKE_DISCONNECT_MARKER_PREFIX}{call_id}",
                "1",
                ttl_seconds=INTAKE_DISCONNECT_MARKER_TTL_SECONDS,
            )
        except Exception:
            logger.exception(
                "Failed to record service desk intake disconnect marker | call=%s",
                call_id,
            )

    async def _intake_disconnect_was_recorded(self, call_id: str) -> bool:
        if self._redis is None:
            return False
        try:
            marker = await self._redis.get_value_async(
                f"{_INTAKE_DISCONNECT_MARKER_PREFIX}{call_id}"
            )
        except Exception:
            logger.exception(
                "Failed to read service desk intake disconnect marker | call=%s",
                call_id,
            )
            return False
        return marker == "1"

    async def reconcile_intake_disconnects(self, *, limit: int = 100) -> int:
        """Retry activation for waiting work whose disconnect marker already exists."""
        if self._redis is None:
            return 0
        waiting_items = await asyncio.to_thread(
            self._find_documents,
            {
                "document_type": WORK_ITEM_DOCUMENT_TYPE,
                "status": "awaiting_intake_disconnect",
            },
            limit,
        )
        activated_count = 0
        for work_item in waiting_items:
            call_id = str(work_item.get("intake_call_id") or "").strip()
            work_item_id = str(work_item.get("work_item_id") or "").strip()
            if not call_id or not work_item_id:
                continue
            if not await self._intake_disconnect_was_recorded(call_id):
                continue
            activated = await self.activate_after_intake_disconnect(work_item_id=work_item_id)
            if activated is not None:
                activated_count += 1
        return activated_count

    async def activate_after_intake_disconnect(
        self,
        *,
        call_id: str | None = None,
        session_id: str | None = None,
        work_item_id: str | None = None,
        now: datetime | None = None,
    ) -> dict[str, Any] | None:
        """Make callback work claimable after its intake session has ended."""
        selectors = {
            key: value
            for key, value in (
                ("intake_call_id", call_id),
                ("intake_session_id", session_id),
                ("work_item_id", work_item_id),
            )
            if value
        }
        if not selectors:
            raise ValueError("A call, session, or work item identifier is required.")
        current_time = now or _utc_now()
        return await asyncio.to_thread(
            self._collection.find_one_and_update,
            {
                "document_type": WORK_ITEM_DOCUMENT_TYPE,
                "status": "awaiting_intake_disconnect",
                **selectors,
            },
            {
                "$set": {
                    "status": "pending",
                    "next_attempt_at": current_time,
                    "intake_disconnected_at": current_time,
                    "updated_at": current_time,
                }
            },
            return_document=ReturnDocument.AFTER,
        )

    async def get_ticket(self, ticket_id: str) -> dict[str, Any] | None:
        """Get one ticket by its public identifier."""
        return await asyncio.to_thread(
            self._collection.find_one,
            {"document_type": TICKET_DOCUMENT_TYPE, "ticket_id": ticket_id},
        )

    async def list_tickets(self, *, limit: int = 100) -> list[dict[str, Any]]:
        """List newest tickets."""
        return await asyncio.to_thread(
            self._find_documents,
            {"document_type": TICKET_DOCUMENT_TYPE},
            limit,
        )

    async def get_work_item(self, work_item_id: str) -> dict[str, Any] | None:
        """Get one callback work item by its public identifier."""
        return await asyncio.to_thread(
            self._collection.find_one,
            {"document_type": WORK_ITEM_DOCUMENT_TYPE, "work_item_id": work_item_id},
        )

    async def list_work_items(
        self,
        *,
        status: str | None = None,
        limit: int = 100,
    ) -> list[dict[str, Any]]:
        """List callback work items, optionally filtered by status."""
        query: dict[str, Any] = {"document_type": WORK_ITEM_DOCUMENT_TYPE}
        if status:
            query["status"] = status
        return await asyncio.to_thread(self._find_documents, query, limit)

    def _find_documents(self, query: dict[str, Any], limit: int) -> list[dict[str, Any]]:
        cursor = self._collection.find(query).sort("created_at", -1)
        if limit > 0:
            cursor = cursor.limit(limit)
        return list(cursor)

    async def record_confirmation(
        self,
        ticket_id: str,
        confirmed: bool,
        *,
        work_item_id: str,
        call_id: str,
    ) -> dict[str, Any] | None:
        """Record an explicit confirmation decision without changing ticket fields."""
        now = _utc_now()
        previous_work_item = await asyncio.to_thread(
            self._collection.find_one_and_update,
            {
                "document_type": WORK_ITEM_DOCUMENT_TYPE,
                "work_item_id": work_item_id,
                "ticket_id": ticket_id,
                "status": "calling",
                "call_id": call_id,
                "expires_at": {"$gt": now},
            },
            {
                "$set": {
                    "status": "completed",
                    "completed_at": now,
                    "updated_at": now,
                    "attempt_history.$[attempt].status": "completed",
                    "attempt_history.$[attempt].ended_at": now,
                },
                "$unset": {"lease_owner": "", "lease_until": ""},
            },
            array_filters=[{"attempt.call_id": call_id, "attempt.ended_at": {"$exists": False}}],
            return_document=ReturnDocument.BEFORE,
        )
        if previous_work_item is None:
            return None

        event = {"confirmed": confirmed, "timestamp": now}
        try:
            ticket = await asyncio.to_thread(
                self._collection.find_one_and_update,
                {"document_type": TICKET_DOCUMENT_TYPE, "ticket_id": ticket_id},
                {
                    "$push": {"confirmation_events": event},
                    "$set": {
                        "confirmation_status": (
                            "confirmed" if confirmed else "correction_requested"
                        ),
                        "confirmed": confirmed,
                        "confirmed_at": now,
                        "updated_at": now,
                    },
                },
                return_document=ReturnDocument.AFTER,
            )
        except Exception:
            await self._rollback_terminal_transition(
                previous_work_item,
                terminal_status="completed",
                completed_at=now,
            )
            raise
        if ticket is None:
            await self._rollback_terminal_transition(
                previous_work_item,
                terminal_status="completed",
                completed_at=now,
            )
        return ticket

    async def append_correction_note(
        self,
        ticket_id: str,
        note: str,
        *,
        work_item_id: str,
        call_id: str,
    ) -> dict[str, Any] | None:
        """Append a timestamped correction while preserving original ticket fields."""
        cleaned_note = str(note or "").strip()
        if not cleaned_note:
            raise ValueError("Correction note is required.")
        now = _utc_now()
        previous_work_item = await asyncio.to_thread(
            self._collection.find_one_and_update,
            {
                "document_type": WORK_ITEM_DOCUMENT_TYPE,
                "work_item_id": work_item_id,
                "ticket_id": ticket_id,
                "status": "calling",
                "call_id": call_id,
                "expires_at": {"$gt": now},
            },
            {
                "$set": {
                    "status": "completed_with_corrections",
                    "completed_at": now,
                    "updated_at": now,
                    "attempt_history.$[attempt].status": "completed_with_corrections",
                    "attempt_history.$[attempt].ended_at": now,
                    "attempt_history.$[attempt].reason": "correction recorded",
                },
                "$unset": {"lease_owner": "", "lease_until": ""},
            },
            array_filters=[{"attempt.call_id": call_id, "attempt.ended_at": {"$exists": False}}],
            return_document=ReturnDocument.BEFORE,
        )
        if previous_work_item is None:
            return None

        try:
            ticket = await asyncio.to_thread(
                self._collection.find_one_and_update,
                {"document_type": TICKET_DOCUMENT_TYPE, "ticket_id": ticket_id},
                {
                    "$push": {
                        "correction_notes": {"note": cleaned_note, "timestamp": now},
                        "confirmation_events": {"confirmed": False, "timestamp": now},
                    },
                    "$set": {
                        "confirmation_status": "correction_recorded",
                        "confirmed": False,
                        "confirmed_at": now,
                        "updated_at": now,
                    },
                },
                return_document=ReturnDocument.AFTER,
            )
        except Exception:
            await self._rollback_terminal_transition(
                previous_work_item,
                terminal_status="completed_with_corrections",
                completed_at=now,
            )
            raise
        if ticket is None:
            await self._rollback_terminal_transition(
                previous_work_item,
                terminal_status="completed_with_corrections",
                completed_at=now,
            )
        return ticket

    async def _rollback_terminal_transition(
        self,
        previous_work_item: dict[str, Any],
        *,
        terminal_status: str,
        completed_at: datetime,
    ) -> None:
        """Restore a calling work item when its linked ticket update fails."""
        call_id = str(previous_work_item["call_id"])
        restored_fields = {
            "status": "calling",
            "updated_at": previous_work_item["updated_at"],
            "attempt_history.$[attempt].status": "calling",
        }
        if "lease_owner" in previous_work_item:
            restored_fields["lease_owner"] = previous_work_item["lease_owner"]
            restored_fields["lease_until"] = _utc_now() + timedelta(
                seconds=WORK_ITEM_LEASE_SECONDS
            )

        result = await asyncio.to_thread(
            self._collection.update_one,
            {
                "document_type": WORK_ITEM_DOCUMENT_TYPE,
                "work_item_id": previous_work_item["work_item_id"],
                "ticket_id": previous_work_item["ticket_id"],
                "status": terminal_status,
                "call_id": call_id,
                "completed_at": completed_at,
            },
            {
                "$set": restored_fields,
                "$unset": {
                    "completed_at": "",
                    "attempt_history.$[attempt].ended_at": "",
                    "attempt_history.$[attempt].reason": "",
                },
            },
            array_filters=[{"attempt.call_id": call_id}],
        )
        if not result.modified_count:
            raise RuntimeError(
                "Failed to restore callback work after the ticket update failed."
            )

    async def claim_due_work(
        self,
        worker_id: str,
        *,
        lease_seconds: int = 60,
        now: datetime | None = None,
    ) -> dict[str, Any] | None:
        """Atomically lease one due work item to a scheduler worker."""
        if not worker_id.strip():
            raise ValueError("worker_id is required.")
        if lease_seconds <= 0:
            raise ValueError("lease_seconds must be positive.")
        current_time = now or _utc_now()
        lease_until = current_time + timedelta(seconds=lease_seconds)
        query = {
            "document_type": WORK_ITEM_DOCUMENT_TYPE,
            "next_attempt_at": {"$lte": current_time},
            "expires_at": {"$gt": current_time},
            "$or": [
                {
                    "status": {"$in": ["pending", "retry"]},
                    "$or": [
                        {"lease_until": {"$exists": False}},
                        {"lease_until": None},
                        {"lease_until": {"$lte": current_time}},
                    ],
                },
                {
                    "status": {"$in": ["claimed", "calling"]},
                    "lease_until": {"$lte": current_time},
                },
            ],
        }
        return await asyncio.to_thread(
            self._collection.find_one_and_update,
            query,
            {
                "$set": {
                    "status": "claimed",
                    "lease_owner": worker_id,
                    "lease_until": lease_until,
                    "claimed_at": current_time,
                    "updated_at": current_time,
                }
            },
            sort=[("next_attempt_at", 1), ("created_at", 1)],
            return_document=ReturnDocument.AFTER,
        )

    async def mark_attempt_started(
        self,
        work_item_id: str,
        worker_id: str,
        call_id: str,
        *,
        attempt_number: int = 1,
        now: datetime | None = None,
    ) -> dict[str, Any] | None:
        """Mark a leased item as calling and associate its outbound call."""
        if not call_id.strip():
            raise ValueError("call_id is required.")
        if attempt_number <= 0:
            raise ValueError("attempt_number must be positive.")
        current_time = now or _utc_now()
        return await asyncio.to_thread(
            self._collection.find_one_and_update,
            {
                "document_type": WORK_ITEM_DOCUMENT_TYPE,
                "work_item_id": work_item_id,
                "status": "claimed",
                "lease_owner": worker_id,
                "lease_until": {"$gt": current_time},
            },
            {
                "$set": {
                    "status": "calling",
                    "call_id": call_id,
                    "attempt_started_at": current_time,
                    "updated_at": current_time,
                },
                "$inc": {"attempt_count": 1},
                "$push": {
                    "attempt_history": {
                        "attempt_number": attempt_number,
                        "call_id": call_id,
                        "status": "calling",
                        "started_at": current_time,
                    }
                },
            },
            return_document=ReturnDocument.AFTER,
        )

    async def release_work_item(
        self,
        work_item_id: str,
        worker_id: str,
        reason: str,
        *,
        call_id: str | None = None,
        now: datetime | None = None,
    ) -> dict[str, Any] | None:
        """Release a leased item and requeue it after failure or disconnect."""
        cleaned_reason = str(reason or "").strip()
        if not cleaned_reason:
            raise ValueError("Release reason is required.")
        current_time = now or _utc_now()
        query: dict[str, Any] = {
            "document_type": WORK_ITEM_DOCUMENT_TYPE,
            "work_item_id": work_item_id,
            "lease_owner": worker_id,
        }
        if call_id:
            query.update({"status": "calling", "call_id": call_id})
        else:
            query["status"] = "claimed"

        current_work_item = await asyncio.to_thread(self._collection.find_one, query)
        if current_work_item is None:
            return None
        configuration = await self.get_configuration()
        retry_intervals = configuration["retry_intervals_minutes"]
        attempt_count = int(current_work_item.get("attempt_count", 0))
        interval_index = min(max(attempt_count - 1, 0), len(retry_intervals) - 1)
        retry_interval_seconds = int(retry_intervals[interval_index]) * 60
        update: dict[str, Any] = {
            "$set": {
                "status": "retry",
                "retry_interval_seconds": retry_interval_seconds,
                "next_attempt_at": current_time + timedelta(seconds=retry_interval_seconds),
                "last_release_reason": cleaned_reason,
                "released_at": current_time,
                "updated_at": current_time,
            },
            "$unset": {"lease_owner": "", "lease_until": ""},
        }
        kwargs: dict[str, Any] = {"return_document": ReturnDocument.AFTER}
        if call_id:
            update["$set"].update(
                {
                    "attempt_history.$[attempt].status": "retry",
                    "attempt_history.$[attempt].ended_at": current_time,
                    "attempt_history.$[attempt].reason": cleaned_reason,
                }
            )
            kwargs["array_filters"] = [
                {"attempt.call_id": call_id, "attempt.ended_at": {"$exists": False}}
            ]

        return await asyncio.to_thread(
            self._collection.find_one_and_update,
            query,
            update,
            **kwargs,
        )

    async def release_after_failure(
        self,
        work_item_id: str,
        worker_id: str,
        reason: str,
        *,
        call_id: str | None = None,
    ) -> dict[str, Any] | None:
        """Requeue work after an outbound-call failure."""
        return await self.release_work_item(
            work_item_id,
            worker_id,
            reason,
            call_id=call_id,
        )

    async def release_after_disconnect(
        self,
        work_item_id: str,
        worker_id: str,
        reason: str = "call disconnected",
        *,
        call_id: str,
    ) -> dict[str, Any] | None:
        """Requeue work after an outbound call disconnects."""
        return await self.release_work_item(
            work_item_id,
            worker_id,
            reason,
            call_id=call_id,
        )

    async def renew_call_lease(
        self,
        work_item_id: str,
        worker_id: str,
        call_id: str,
        *,
        lease_seconds: int,
        now: datetime | None = None,
    ) -> bool:
        """Renew the lease for a locally active outbound call."""
        current_time = now or _utc_now()
        result = await asyncio.to_thread(
            self._collection.update_one,
            {
                "document_type": WORK_ITEM_DOCUMENT_TYPE,
                "work_item_id": work_item_id,
                "status": "calling",
                "lease_owner": worker_id,
                "call_id": call_id,
            },
            {
                "$set": {
                    "lease_until": current_time + timedelta(seconds=lease_seconds),
                    "updated_at": current_time,
                }
            },
        )
        return bool(result.modified_count)

    async def expire_overdue(self, *, now: datetime | None = None) -> int:
        """Expire active work items whose 24-hour callback window elapsed."""
        current_time = now or _utc_now()
        result = await asyncio.to_thread(
            self._collection.update_many,
            {
                "document_type": WORK_ITEM_DOCUMENT_TYPE,
                "status": {"$in": _ACTIVE_WORK_STATUSES},
                "expires_at": {"$lte": current_time},
            },
            {
                "$set": {
                    "status": "expired",
                    "expired_at": current_time,
                    "updated_at": current_time,
                },
                "$unset": {"lease_owner": "", "lease_until": ""},
            },
        )
        return int(result.modified_count)

    async def claim_due_work_item(
        self,
        worker_id: str,
        *,
        lease_seconds: int = 60,
        now: datetime | None = None,
    ) -> dict[str, Any] | None:
        """Compatibility name for claiming one due scheduler work item."""
        return await self.claim_due_work(worker_id, lease_seconds=lease_seconds, now=now)

    async def expire_overdue_work_items(self, *, now: datetime | None = None) -> int:
        """Compatibility name for expiring overdue scheduler work."""
        return await self.expire_overdue(now=now)
