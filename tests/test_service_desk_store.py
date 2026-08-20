from __future__ import annotations

from datetime import UTC, datetime, timedelta
from types import SimpleNamespace
from unittest.mock import MagicMock

import pytest
from apps.artagent.backend.src.services.service_desk import (
    ServiceDeskStore,
    normalize_e164,
)
from pymongo import ReturnDocument


@pytest.mark.parametrize(
    ("raw", "expected"),
    [
        ("+1 (425) 555-0101", "+14255550101"),
        ("+442079460123", "+442079460123"),
    ],
)
def test_normalize_e164_accepts_formatted_international_numbers(raw, expected):
    assert normalize_e164(raw) == expected


@pytest.mark.parametrize("raw", ["", "14255550101", "+0123", "+1234567890123456"])
def test_normalize_e164_rejects_invalid_numbers(raw):
    with pytest.raises(ValueError, match="E.164"):
        normalize_e164(raw)


@pytest.mark.asyncio
async def test_create_ticket_inserts_ticket_and_work_item_together():
    collection = MagicMock()
    store = ServiceDeskStore(SimpleNamespace(collection=collection))

    ticket = await store.create_ticket(
        name="Ada Lovelace",
        callback_number="+14255550101",
        urgency="high",
        affected_service="email",
        description="Cannot send messages from Outlook.",
        short_description="Outlook sending failure",
    )

    collection.insert_many.assert_called_once()
    documents = collection.insert_many.call_args.args[0]
    assert [document["document_type"] for document in documents] == [
        "service_desk_ticket",
        "service_desk_work_item",
    ]
    assert documents[0]["ticket_id"] == ticket["ticket_id"]
    assert documents[0]["name"] == "Ada Lovelace"
    assert documents[1]["ticket_id"] == ticket["ticket_id"]
    assert documents[1]["retry_interval_seconds"] == 600
    assert documents[1]["attempt_history"] == []
    assert documents[1]["expires_at"] - documents[1]["created_at"] == timedelta(hours=24)


@pytest.mark.asyncio
async def test_correction_appends_note_without_replacing_original_fields():
    collection = MagicMock()
    collection.find_one_and_update.side_effect = [
        {"work_item_id": "WI-1", "status": "completed_with_corrections"},
        {
            "ticket_id": "SD-1",
            "work_item_id": "WI-1",
            "description": "Original",
            "correction_notes": [{"note": "The issue affects two users."}],
        },
    ]
    store = ServiceDeskStore(SimpleNamespace(collection=collection))

    result = await store.append_correction_note(
        "SD-1",
        "The issue affects two users.",
        work_item_id="WI-1",
        call_id="call-1",
    )

    assert result is not None
    query, update = collection.find_one_and_update.call_args_list[1].args[:2]
    assert query == {"document_type": "service_desk_ticket", "ticket_id": "SD-1"}
    assert set(update) == {"$push", "$set"}
    assert update["$push"]["correction_notes"]["note"] == "The issue affects two users."
    assert "description" not in update["$set"]
    work_query, work_update = collection.find_one_and_update.call_args_list[0].args[:2]
    assert work_query["work_item_id"] == "WI-1"
    assert work_query["call_id"] == "call-1"
    assert work_update["$set"]["status"] == "completed_with_corrections"


@pytest.mark.asyncio
async def test_confirmation_completes_the_linked_work_item():
    collection = MagicMock()
    collection.find_one_and_update.side_effect = [
        {"work_item_id": "WI-1", "status": "completed"},
        {
            "ticket_id": "SD-1",
            "work_item_id": "WI-1",
            "confirmed": True,
        },
    ]
    store = ServiceDeskStore(SimpleNamespace(collection=collection))

    result = await store.record_confirmation(
        "SD-1",
        True,
        work_item_id="WI-1",
        call_id="call-1",
    )

    assert result is not None
    work_query, work_update = collection.find_one_and_update.call_args_list[0].args[:2]
    assert work_query == {
        "document_type": "service_desk_work_item",
        "work_item_id": "WI-1",
        "ticket_id": "SD-1",
        "status": "calling",
        "call_id": "call-1",
        "expires_at": {"$gt": work_update["$set"]["completed_at"]},
    }
    assert work_update["$set"]["status"] == "completed"
    assert work_update["$unset"] == {"lease_owner": "", "lease_until": ""}
    assert (
        collection.find_one_and_update.call_args_list[0].kwargs["return_document"]
        is ReturnDocument.BEFORE
    )


@pytest.mark.asyncio
async def test_confirmation_rolls_back_work_item_when_ticket_update_fails():
    rollback_started_at = datetime.now(UTC)
    previous = {
        "work_item_id": "WI-1",
        "ticket_id": "SD-1",
        "call_id": "call-1",
        "lease_owner": "worker-1",
        "lease_until": datetime(2026, 8, 17, 1, tzinfo=UTC),
        "updated_at": datetime(2026, 8, 17, tzinfo=UTC),
    }
    collection = MagicMock()
    collection.find_one_and_update.side_effect = [previous, None]
    collection.update_one.return_value.modified_count = 1
    store = ServiceDeskStore(SimpleNamespace(collection=collection))

    result = await store.record_confirmation(
        "SD-1",
        True,
        work_item_id="WI-1",
        call_id="call-1",
    )

    assert result is None
    query, update = collection.update_one.call_args.args[:2]
    assert query["status"] == "completed"
    assert query["call_id"] == "call-1"
    assert update["$set"]["status"] == "calling"
    assert update["$set"]["lease_owner"] == "worker-1"
    assert update["$set"]["lease_until"] > rollback_started_at
    assert update["$unset"]["completed_at"] == ""


@pytest.mark.asyncio
async def test_claim_due_work_uses_atomic_find_one_and_update_with_lease():
    collection = MagicMock()
    collection.find_one_and_update.return_value = {"work_item_id": "WI-1", "status": "claimed"}
    store = ServiceDeskStore(SimpleNamespace(collection=collection))
    now = datetime(2026, 8, 17, tzinfo=UTC)

    claimed = await store.claim_due_work("worker-a", lease_seconds=90, now=now)

    assert claimed == {"work_item_id": "WI-1", "status": "claimed"}
    query, update = collection.find_one_and_update.call_args.args[:2]
    assert query["document_type"] == "service_desk_work_item"
    assert query["next_attempt_at"]["$lte"] == now
    assert query["expires_at"]["$gt"] == now
    assert query["$or"][0]["status"]["$in"] == ["pending", "retry"]
    assert {"lease_until": {"$lte": now}} in query["$or"][0]["$or"]
    assert query["$or"][1] == {
        "status": {"$in": ["claimed", "calling"]},
        "lease_until": {"$lte": now},
    }
    assert update["$set"]["lease_owner"] == "worker-a"
    assert update["$set"]["lease_until"] == now + timedelta(seconds=90)
    assert (
        collection.find_one_and_update.call_args.kwargs["return_document"] is ReturnDocument.AFTER
    )


@pytest.mark.asyncio
async def test_mark_attempt_and_release_require_the_lease_owner():
    collection = MagicMock()
    collection.find_one_and_update.side_effect = [
        {"work_item_id": "WI-1", "status": "calling"},
        {"work_item_id": "WI-1", "status": "retry"},
    ]
    store = ServiceDeskStore(SimpleNamespace(collection=collection))
    now = datetime(2026, 8, 17, tzinfo=UTC)

    await store.mark_attempt_started(
        "WI-1",
        "worker-a",
        "call-123",
        attempt_number=2,
        now=now,
    )
    await store.release_work_item(
        "WI-1",
        "worker-a",
        "disconnected",
        call_id="call-123",
        now=now,
    )

    start_query, start_update = collection.find_one_and_update.call_args_list[0].args[:2]
    assert start_query["lease_owner"] == "worker-a"
    assert start_update["$inc"] == {"attempt_count": 1}
    assert start_update["$set"]["call_id"] == "call-123"
    assert start_update["$push"]["attempt_history"]["attempt_number"] == 2

    release_query, release_update = collection.find_one_and_update.call_args_list[1].args[:2]
    assert release_query["lease_owner"] == "worker-a"
    assert "lease_until" not in release_query
    assert release_update["$set"]["next_attempt_at"] == now + timedelta(seconds=600)
    assert release_update["$unset"] == {"lease_owner": "", "lease_until": ""}
    assert (
        collection.find_one_and_update.call_args_list[1].kwargs["array_filters"]
        == [{"attempt.call_id": "call-123", "attempt.ended_at": {"$exists": False}}]
    )


@pytest.mark.asyncio
async def test_expire_overdue_only_updates_active_work_items():
    collection = MagicMock()
    collection.update_many.return_value.modified_count = 3
    store = ServiceDeskStore(SimpleNamespace(collection=collection))
    now = datetime(2026, 8, 17, tzinfo=UTC)

    count = await store.expire_overdue(now=now)

    assert count == 3
    query, update = collection.update_many.call_args.args
    assert query["status"]["$in"] == ["pending", "retry", "claimed", "calling"]
    assert query["expires_at"]["$lte"] == now
    assert update["$set"]["status"] == "expired"
