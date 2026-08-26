"""Tests for the read-only service desk inspection API."""

from __future__ import annotations

from datetime import UTC, datetime, timedelta

import pytest
from apps.artagent.backend.api.v1 import v1_router
from apps.artagent.backend.api.v1.endpoints.service_desk import router
from apps.artagent.backend.src.services.service_desk.store import (
    ServiceDeskConfigurationConflictError,
    ServiceDeskServiceInUseError,
)
from fastapi import FastAPI
from fastapi.testclient import TestClient

NOW = datetime(2026, 8, 17, 3, 0, tzinfo=UTC)


def _ticket(ticket_id: str, work_item_id: str, *, created_at: datetime) -> dict:
    return {
        "_id": ticket_id,
        "document_type": "service_desk_ticket",
        "ticket_id": ticket_id,
        "work_item_id": work_item_id,
        "name": "Ada Lovelace",
        "callback_number": "+14255550101",
        "urgency": "high",
        "affected_service": "email",
        "description": "Cannot send messages.",
        "short_description": "Email failure",
        "confirmation_status": "pending",
        "correction_notes": [],
        "created_at": created_at,
        "updated_at": created_at,
    }


def _work_item(
    ticket_id: str,
    work_item_id: str,
    *,
    status: str,
    created_at: datetime,
) -> dict:
    return {
        "_id": work_item_id,
        "document_type": "service_desk_work_item",
        "work_item_id": work_item_id,
        "ticket_id": ticket_id,
        "callback_number": "+14255550101",
        "standby_number": "+14255550201",
        "status": status,
        "attempt_count": 1,
        "retry_interval_seconds": 600,
        "next_attempt_at": created_at,
        "expires_at": created_at + timedelta(hours=24),
        "created_at": created_at,
        "updated_at": created_at,
    }


class FakeServiceDeskStore:
    """Async fake matching the service desk store inspection API."""

    def __init__(self, tickets: list[dict], work_items: list[dict]) -> None:
        self.tickets = tickets
        self.work_items = work_items
        self.list_ticket_limits: list[int] = []
        self.list_work_item_calls: list[tuple[str | None, int]] = []
        self.configuration = {
            "revision": 1,
            "retry_intervals_minutes": [10],
            "services": [
                {
                    "service_id": "email",
                    "name": "Email",
                    "phone_numbers": ["+14255550201"],
                }
            ],
            "created_at": NOW,
            "updated_at": NOW,
        }
        self.update_error: Exception | None = None

    async def list_tickets(self, *, limit: int = 100) -> list[dict]:
        self.list_ticket_limits.append(limit)
        return self.tickets if limit == 0 else self.tickets[:limit]

    async def list_work_items(
        self,
        *,
        status: str | None = None,
        limit: int = 100,
    ) -> list[dict]:
        self.list_work_item_calls.append((status, limit))
        items = [item for item in self.work_items if status is None or item["status"] == status]
        return items if limit == 0 else items[:limit]

    async def get_ticket(self, ticket_id: str) -> dict | None:
        return next((item for item in self.tickets if item["ticket_id"] == ticket_id), None)

    async def get_work_item(self, work_item_id: str) -> dict | None:
        return next(
            (item for item in self.work_items if item["work_item_id"] == work_item_id),
            None,
        )

    async def get_configuration(self) -> dict:
        return self.configuration

    async def update_configuration(self, **values) -> dict:
        if self.update_error:
            raise self.update_error
        self.configuration = {
            **self.configuration,
            "revision": self.configuration["revision"] + 1,
            "retry_intervals_minutes": values["retry_intervals_minutes"],
            "services": values["services"],
            "updated_at": NOW + timedelta(minutes=1),
        }
        return self.configuration


def _client(store: FakeServiceDeskStore | None = None) -> TestClient:
    app = FastAPI()
    app.include_router(router, prefix="/api/v1/service-desk")
    if store is not None:
        app.state.service_desk_store = store
    return TestClient(app)


def test_v1_router_registers_service_desk_paths() -> None:
    app = FastAPI()
    app.include_router(v1_router)
    paths = set(app.openapi()["paths"])

    assert "/api/v1/service-desk/tickets" in paths
    assert "/api/v1/service-desk/tickets/{ticket_id}" in paths
    assert "/api/v1/service-desk/configuration" in paths


def test_list_tickets_filters_by_work_status_and_paginates() -> None:
    tickets = [
        _ticket("SD-3", "WI-3", created_at=NOW),
        _ticket("SD-2", "WI-2", created_at=NOW - timedelta(minutes=1)),
        _ticket("SD-1", "WI-1", created_at=NOW - timedelta(minutes=2)),
    ]
    work_items = [
        _work_item("SD-3", "WI-3", status="retry", created_at=NOW),
        _work_item("SD-2", "WI-2", status="pending", created_at=NOW),
        _work_item("SD-1", "WI-1", status="retry", created_at=NOW),
    ]
    store = FakeServiceDeskStore(tickets, work_items)

    response = _client(store).get(
        "/api/v1/service-desk/tickets",
        params={"status": "retry", "offset": 1, "limit": 1},
    )

    assert response.status_code == 200
    assert response.json() == {
        "tickets": [
            {
                "ticket_id": "SD-1",
                "work_item_id": "WI-1",
                "name": "Ada Lovelace",
                "urgency": "high",
                "affected_service": "email",
                "short_description": "Email failure",
                "confirmation_status": "pending",
                "status": "retry",
                "created_at": "2026-08-17T02:58:00Z",
                "updated_at": "2026-08-17T02:58:00Z",
            }
        ],
        "total": 2,
        "offset": 1,
        "limit": 1,
    }
    assert store.list_ticket_limits == [0]
    assert store.list_work_item_calls == [("retry", 0)]


def test_get_ticket_returns_typed_associated_history() -> None:
    ticket = _ticket("SD-1", "WI-1", created_at=NOW)
    ticket["correction_notes"] = [{"note": "Affects two users.", "timestamp": NOW}]
    ticket["confirmation_events"] = [{"confirmed": False, "timestamp": NOW}]
    work_item = _work_item("SD-1", "WI-1", status="retry", created_at=NOW)
    work_item["attempt_history"] = [
        {
            "attempt_number": 1,
            "call_id": "call-1",
            "status": "failed",
            "started_at": NOW,
            "ended_at": NOW + timedelta(seconds=5),
            "reason": "busy",
        }
    ]

    response = _client(FakeServiceDeskStore([ticket], [work_item])).get(
        "/api/v1/service-desk/tickets/SD-1"
    )

    assert response.status_code == 200
    body = response.json()
    assert body["ticket"]["ticket_id"] == "SD-1"
    assert body["work_item"]["work_item_id"] == "WI-1"
    assert body["status"] == "retry"
    assert body["attempt_history"][0]["call_id"] == "call-1"
    assert body["correction_notes"][0]["note"] == "Affects two users."
    assert body["confirmation_events"][0]["confirmed"] is False
    assert "_id" not in body["ticket"]


def test_get_ticket_derives_latest_attempt_from_store_fields() -> None:
    ticket = _ticket("SD-1", "WI-1", created_at=NOW)
    work_item = _work_item("SD-1", "WI-1", status="retry", created_at=NOW)
    work_item.update(
        {
            "call_id": "call-1",
            "attempt_started_at": NOW,
            "released_at": NOW + timedelta(seconds=5),
            "last_release_reason": "call disconnected",
        }
    )

    response = _client(FakeServiceDeskStore([ticket], [work_item])).get(
        "/api/v1/service-desk/tickets/SD-1"
    )

    assert response.status_code == 200
    assert response.json()["attempt_history"] == [
        {
            "attempt_number": 1,
            "call_id": "call-1",
            "status": "retry",
            "started_at": "2026-08-17T03:00:00Z",
            "ended_at": "2026-08-17T03:00:05Z",
            "reason": "call disconnected",
            "round_number": None,
            "target_index": None,
            "target_number": None,
        }
    ]


def test_get_ticket_returns_404_for_unknown_ticket() -> None:
    response = _client(FakeServiceDeskStore([], [])).get("/api/v1/service-desk/tickets/SD-MISSING")

    assert response.status_code == 404
    assert response.json() == {"detail": "Service desk ticket not found"}


def test_configuration_can_be_read_and_updated() -> None:
    store = FakeServiceDeskStore([], [])
    client = _client(store)

    current = client.get("/api/v1/service-desk/configuration")
    updated = client.put(
        "/api/v1/service-desk/configuration",
        json={
            "expected_revision": 1,
            "retry_intervals_minutes": [1, 2, 5, 10, 30],
            "services": [
                {
                    "service_id": "email",
                    "name": "Messaging",
                    "phone_numbers": ["+14255550999", "%initial_caller%"],
                }
            ],
        },
    )

    assert current.status_code == 200
    assert current.json()["retry_intervals_minutes"] == [10]
    assert updated.status_code == 200
    assert updated.json()["revision"] == 2
    assert updated.json()["services"][0]["name"] == "Messaging"


def test_configuration_update_returns_conflict_for_stale_revision() -> None:
    store = FakeServiceDeskStore([], [])
    store.update_error = ServiceDeskConfigurationConflictError("reload and try again")

    response = _client(store).put(
        "/api/v1/service-desk/configuration",
        json={
            "expected_revision": 1,
            "retry_intervals_minutes": [10],
            "services": [
                {
                    "service_id": "email",
                    "name": "Email",
                    "phone_numbers": ["+14255550201"],
                }
            ],
        },
    )

    assert response.status_code == 409
    assert response.json() == {
        "detail": {
            "code": "revision_conflict",
            "message": "reload and try again",
        }
    }


def test_configuration_update_rejects_non_integer_retry_values() -> None:
    response = _client(FakeServiceDeskStore([], [])).put(
        "/api/v1/service-desk/configuration",
        json={
            "expected_revision": 1,
            "retry_intervals_minutes": [True],
            "services": [
                {
                    "service_id": "email",
                    "name": "Email",
                    "phone_numbers": ["+14255550201"],
                }
            ],
        },
    )

    assert response.status_code == 422


def test_configuration_update_returns_in_use_conflict_without_revision_code() -> None:
    store = FakeServiceDeskStore([], [])
    store.update_error = ServiceDeskServiceInUseError("service is still used")

    response = _client(store).put(
        "/api/v1/service-desk/configuration",
        json={
            "expected_revision": 1,
            "retry_intervals_minutes": [10],
            "services": [
                {
                    "service_id": "email",
                    "name": "Email",
                    "phone_numbers": ["+14255550201"],
                }
            ],
        },
    )

    assert response.status_code == 409
    assert response.json() == {
        "detail": {
            "code": "service_in_use",
            "message": "service is still used",
        }
    }


@pytest.mark.parametrize(
    "path",
    [
        "/api/v1/service-desk/tickets",
        "/api/v1/service-desk/tickets/SD-1",
        "/api/v1/service-desk/configuration",
    ],
)
def test_endpoints_return_503_when_store_is_unavailable(path: str) -> None:
    response = _client().get(path)

    assert response.status_code == 503
    assert response.json() == {"detail": "Service desk store is unavailable"}
