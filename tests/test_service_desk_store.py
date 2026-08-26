from __future__ import annotations

from datetime import UTC, datetime, timedelta
from types import SimpleNamespace
from unittest.mock import AsyncMock, MagicMock

import pytest
from apps.artagent.backend.src.services.service_desk import (
    INITIAL_CALLER_TARGET,
    ServiceDeskStore,
    normalize_e164,
    resolve_callback_targets,
    validate_callback_targets,
)
from apps.artagent.backend.src.services.service_desk.store import (
    ServiceDeskConfigurationConflictError,
    ServiceDeskServiceInUseError,
)
from pymongo import ReturnDocument


def _configuration(
    *,
    revision: int = 1,
    retry_intervals_minutes: list[int] | None = None,
) -> dict:
    return {
        "_id": "service_desk_configuration",
        "document_type": "service_desk_configuration",
        "revision": revision,
        "retry_intervals_minutes": retry_intervals_minutes or [10],
        "services": [
            {
                "service_id": "email",
                "name": "email",
                "phone_numbers": ["+14255550201"],
                "enabled": True,
            },
            {
                "service_id": "vpn",
                "name": "vpn",
                "phone_numbers": ["+14255550204"],
                "enabled": True,
            },
        ],
        "created_at": datetime(2026, 8, 17, tzinfo=UTC),
        "updated_at": datetime(2026, 8, 17, tzinfo=UTC),
    }


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


def test_resolve_callback_targets_expands_skips_and_deduplicates_initial_caller():
    assert resolve_callback_targets(
        [
            "+14255550101",
            INITIAL_CALLER_TARGET,
            "+14255550201",
            "+14255550101",
        ],
        initial_caller_number="+1 (425) 555-0101",
    ) == ["+14255550101", "+14255550201"]
    assert resolve_callback_targets(
        [INITIAL_CALLER_TARGET, "+14255550201"],
        initial_caller_number=None,
    ) == ["+14255550201"]


def test_validate_callback_targets_enforces_token_syntax_and_limit():
    assert validate_callback_targets(["+14255550101", "%INITIAL_CALLER%", "+14255550101"]) == [
        "+14255550101",
        INITIAL_CALLER_TARGET,
    ]
    with pytest.raises(ValueError, match="At most 10"):
        validate_callback_targets([f"+14255550{index:03d}" for index in range(11)])
    with pytest.raises(ValueError, match="E.164"):
        validate_callback_targets(["initial_caller"])


@pytest.mark.asyncio
async def test_create_ticket_inserts_ticket_and_work_item_together():
    collection = MagicMock()
    collection.find_one.return_value = _configuration()
    store = ServiceDeskStore(SimpleNamespace(collection=collection))

    ticket = await store.create_ticket(
        name="Ada Lovelace",
        callback_number="+14255550101",
        urgency="high",
        affected_service="email",
        description="Cannot send messages from Outlook.",
        short_description="Outlook sending failure",
        intake_call_id="intake-call-1",
        intake_session_id="session-1",
        initial_caller_number="+14255550199",
    )

    collection.insert_many.assert_called_once()
    documents = collection.insert_many.call_args.args[0]
    assert [document["document_type"] for document in documents] == [
        "service_desk_ticket",
        "service_desk_work_item",
    ]
    assert documents[0]["ticket_id"] == ticket["ticket_id"]
    assert documents[0]["name"] == "Ada Lovelace"
    assert documents[0]["service_id"] == "email"
    assert documents[1]["ticket_id"] == ticket["ticket_id"]
    assert documents[1]["service_id"] == "email"
    assert documents[0]["initial_caller_number"] == "+14255550199"
    assert documents[1]["initial_caller_number"] == "+14255550199"
    assert documents[1]["standby_numbers"] == ["+14255550201"]
    assert documents[1]["retry_interval_seconds"] == 600
    assert documents[1]["attempt_history"] == []
    assert documents[1]["status"] == "awaiting_intake_disconnect"
    assert documents[1]["next_attempt_at"] is None
    assert documents[1]["intake_call_id"] == "intake-call-1"
    assert documents[1]["expires_at"] - documents[1]["created_at"] == timedelta(hours=24)


@pytest.mark.asyncio
async def test_intake_disconnect_makes_callback_work_claimable():
    collection = MagicMock()
    collection.find_one_and_update.return_value = {
        "work_item_id": "WI-1",
        "status": "pending",
    }
    store = ServiceDeskStore(SimpleNamespace(collection=collection))
    now = datetime(2026, 8, 17, tzinfo=UTC)

    result = await store.activate_after_intake_disconnect(call_id="intake-call-1", now=now)

    assert result == {"work_item_id": "WI-1", "status": "pending"}
    query, update = collection.find_one_and_update.call_args.args[:2]
    assert query == {
        "document_type": "service_desk_work_item",
        "status": "awaiting_intake_disconnect",
        "intake_call_id": "intake-call-1",
    }
    assert update["$set"]["status"] == "pending"
    assert update["$set"]["next_attempt_at"] == now


@pytest.mark.asyncio
async def test_create_ticket_reconciles_disconnect_recorded_before_insert():
    collection = MagicMock()
    collection.find_one.return_value = _configuration()
    collection.find_one_and_update.return_value = {
        "work_item_id": "WI-1",
        "status": "pending",
    }
    redis = SimpleNamespace(get_value_async=AsyncMock(return_value="1"))
    store = ServiceDeskStore(SimpleNamespace(collection=collection), redis)

    await store.create_ticket(
        name="Ada Lovelace",
        callback_number="+14255550101",
        urgency="high",
        affected_service="email",
        description="Cannot send messages from Outlook.",
        short_description="Outlook sending failure",
        intake_call_id="intake-call-1",
    )

    marker_key = redis.get_value_async.await_args.args[0]
    assert marker_key == "service_desk:intake_disconnected:intake-call-1"
    query = collection.find_one_and_update.call_args.args[0]
    assert query["work_item_id"].startswith("WI-")
    assert query["status"] == "awaiting_intake_disconnect"


@pytest.mark.asyncio
async def test_record_intake_disconnect_sets_bounded_marker():
    redis = SimpleNamespace(set_value_async=AsyncMock())
    store = ServiceDeskStore(SimpleNamespace(collection=MagicMock()), redis)

    await store.record_intake_disconnect("intake-call-1")

    redis.set_value_async.assert_awaited_once_with(
        "service_desk:intake_disconnected:intake-call-1",
        "1",
        ttl_seconds=86400,
    )


@pytest.mark.asyncio
async def test_reconcile_intake_disconnects_retries_marker_activation():
    collection = MagicMock()
    collection.find_one_and_update.return_value = {
        "work_item_id": "WI-1",
        "status": "pending",
    }
    redis = SimpleNamespace(get_value_async=AsyncMock(return_value="1"))
    store = ServiceDeskStore(SimpleNamespace(collection=collection), redis)
    store._find_documents = MagicMock(
        return_value=[
            {
                "work_item_id": "WI-1",
                "intake_call_id": "intake-call-1",
            }
        ]
    )

    activated = await store.reconcile_intake_disconnects()

    assert activated == 1
    query = collection.find_one_and_update.call_args.args[0]
    assert query["work_item_id"] == "WI-1"
    assert query["status"] == "awaiting_intake_disconnect"


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
    collection.find_one.side_effect = [
        {"work_item_id": "WI-1", "attempt_count": 2},
        _configuration(retry_intervals_minutes=[1, 2, 5]),
    ]
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
    assert release_update["$set"]["retry_interval_seconds"] == 120
    assert release_update["$set"]["next_attempt_at"] == now + timedelta(seconds=120)
    assert release_update["$unset"] == {
        "lease_owner": "",
        "lease_until": "",
        "round_targets": "",
        "current_target_number": "",
    }
    assert collection.find_one_and_update.call_args_list[1].kwargs["array_filters"] == [
        {"attempt.call_id": "call-123", "attempt.ended_at": {"$exists": False}}
    ]


@pytest.mark.asyncio
async def test_prepare_round_targets_preserves_legacy_retry_position():
    collection = MagicMock()
    collection.find_one.return_value = {
        "work_item_id": "WI-1",
        "status": "claimed",
        "attempt_count": 2,
    }
    collection.find_one_and_update.return_value = {"work_item_id": "WI-1"}
    store = ServiceDeskStore(SimpleNamespace(collection=collection))
    now = datetime(2026, 8, 17, tzinfo=UTC)

    await store.prepare_round_targets(
        "WI-1",
        "worker-a",
        ["+14255550101", "+14255550201"],
        now=now,
    )

    _, update = collection.find_one_and_update.call_args.args[:2]
    assert update["$set"]["completed_round_count"] == 2
    assert update["$set"]["round_number"] == 3
    assert update["$set"]["round_targets"] == ["+14255550101", "+14255550201"]
    assert update["$set"]["current_target_number"] == "+14255550101"


@pytest.mark.asyncio
async def test_release_advances_immediately_until_round_is_complete():
    collection = MagicMock()
    configuration = _configuration(retry_intervals_minutes=[1, 3, 5])
    collection.find_one.side_effect = [
        {
            "work_item_id": "WI-1",
            "attempt_count": 1,
            "completed_round_count": 0,
            "round_targets": ["+14255550101", "+14255550201"],
            "current_target_index": 0,
        },
        configuration,
    ]
    collection.find_one_and_update.return_value = {"work_item_id": "WI-1", "status": "retry"}
    store = ServiceDeskStore(SimpleNamespace(collection=collection))
    now = datetime(2026, 8, 17, tzinfo=UTC)

    await store.release_work_item(
        "WI-1",
        "worker-a",
        "busy",
        call_id="call-1",
        now=now,
    )

    _, update = collection.find_one_and_update.call_args.args[:2]
    assert update["$set"]["retry_interval_seconds"] == 0
    assert update["$set"]["next_attempt_at"] == now
    assert update["$set"]["current_target_index"] == 1
    assert update["$set"]["current_target_number"] == "+14255550201"
    assert "round_targets" not in update["$unset"]


@pytest.mark.asyncio
async def test_release_schedules_retry_only_after_final_round_target():
    collection = MagicMock()
    configuration = _configuration(retry_intervals_minutes=[1, 3, 5])
    collection.find_one.side_effect = [
        {
            "work_item_id": "WI-1",
            "attempt_count": 2,
            "completed_round_count": 0,
            "round_targets": ["+14255550101", "+14255550201"],
            "current_target_index": 1,
        },
        configuration,
    ]
    collection.find_one_and_update.return_value = {"work_item_id": "WI-1", "status": "retry"}
    store = ServiceDeskStore(SimpleNamespace(collection=collection))
    now = datetime(2026, 8, 17, tzinfo=UTC)

    await store.release_work_item(
        "WI-1",
        "worker-a",
        "disconnected",
        call_id="call-2",
        now=now,
    )

    _, update = collection.find_one_and_update.call_args.args[:2]
    assert update["$set"]["completed_round_count"] == 1
    assert update["$set"]["retry_interval_seconds"] == 60
    assert update["$set"]["next_attempt_at"] == now + timedelta(minutes=1)
    assert update["$unset"]["round_targets"] == ""


@pytest.mark.asyncio
async def test_expire_overdue_only_updates_active_work_items():
    collection = MagicMock()
    collection.update_many.return_value.modified_count = 3
    store = ServiceDeskStore(SimpleNamespace(collection=collection))
    now = datetime(2026, 8, 17, tzinfo=UTC)

    count = await store.expire_overdue(now=now)

    assert count == 3
    query, update = collection.update_many.call_args.args
    assert query["status"]["$in"] == [
        "awaiting_intake_disconnect",
        "pending",
        "retry",
        "claimed",
        "calling",
    ]
    assert query["expires_at"]["$lte"] == now
    assert update["$set"]["status"] == "expired"


@pytest.mark.asyncio
async def test_ensure_configuration_seeds_defaults_and_migrates_legacy_references():
    collection = MagicMock()
    collection.find_one_and_update.return_value = _configuration()
    store = ServiceDeskStore(SimpleNamespace(collection=collection))

    configuration = await store.ensure_configuration()

    assert configuration["retry_intervals_minutes"] == [10]
    _, update = collection.find_one_and_update.call_args.args[:2]
    assert update["$setOnInsert"]["retry_intervals_minutes"] == [10]
    assert collection.update_many.call_count == 4
    work_query, work_update = collection.update_many.call_args_list[0].args
    assert work_query["status"]["$in"] == [
        "awaiting_intake_disconnect",
        "pending",
        "retry",
        "claimed",
        "calling",
    ]
    assert work_query["standby_number"] == {"$in": ["+14255550201"]}
    assert work_update == {"$set": {"service_id": "email"}}
    ticket_query, ticket_update = collection.update_many.call_args_list[1].args
    assert ticket_query["affected_service"] == "email"
    assert ticket_update == {"$set": {"service_id": "email"}}


@pytest.mark.asyncio
async def test_get_configuration_migrates_legacy_single_phone_number():
    collection = MagicMock()
    legacy = _configuration()
    legacy["services"][0]["phone_number"] = legacy["services"][0].pop("phone_numbers")[0]
    migrated = {
        **legacy,
        "revision": 2,
        "services": [
            {
                **legacy["services"][0],
                "phone_numbers": [legacy["services"][0]["phone_number"]],
            },
            legacy["services"][1],
        ],
    }
    migrated["services"][0].pop("phone_number")
    collection.find_one.return_value = legacy
    collection.find_one_and_update.return_value = migrated
    store = ServiceDeskStore(SimpleNamespace(collection=collection))

    configuration = await store.get_configuration()

    assert configuration["revision"] == 2
    assert configuration["services"][0]["phone_numbers"] == ["+14255550201"]
    _, update = collection.find_one_and_update.call_args.args[:2]
    assert "phone_number" not in update["$set"]["services"][0]


@pytest.mark.asyncio
async def test_resolve_work_item_route_uses_current_number_for_stable_service_id():
    collection = MagicMock()
    configuration = _configuration()
    configuration["services"][0]["name"] = "Messaging"
    configuration["services"][0]["phone_numbers"] = [
        "+14255550999",
        INITIAL_CALLER_TARGET,
    ]
    collection.find_one.return_value = configuration
    store = ServiceDeskStore(SimpleNamespace(collection=collection))

    route = await store.resolve_work_item_route(
        {
            "service_id": "email",
            "standby_number": "+14255550201",
        }
    )

    assert route["name"] == "Messaging"
    assert route["phone_numbers"] == ["+14255550999", INITIAL_CALLER_TARGET]


@pytest.mark.asyncio
async def test_resolve_work_item_route_supports_legacy_phone_reference():
    collection = MagicMock()
    collection.find_one.return_value = _configuration()
    store = ServiceDeskStore(SimpleNamespace(collection=collection))

    route = await store.resolve_work_item_route(
        {"standby_number": "+14255550204"},
        {"affected_service": "old vpn label"},
    )

    assert route["service_id"] == "vpn"


@pytest.mark.asyncio
async def test_update_configuration_rejects_stale_revision():
    collection = MagicMock()
    collection.find_one.return_value = _configuration(revision=2)
    store = ServiceDeskStore(SimpleNamespace(collection=collection))

    with pytest.raises(ServiceDeskConfigurationConflictError):
        await store.update_configuration(
            expected_revision=1,
            retry_intervals_minutes=[1, 2, 5],
            services=[
                {
                    "service_id": "email",
                    "name": "Messaging",
                    "phone_numbers": ["+14255550999"],
                }
            ],
        )


@pytest.mark.asyncio
async def test_update_configuration_blocks_removing_service_used_by_open_work():
    collection = MagicMock()
    collection.find_one.return_value = _configuration()
    collection.count_documents.return_value = 1
    store = ServiceDeskStore(SimpleNamespace(collection=collection))

    with pytest.raises(ServiceDeskServiceInUseError):
        await store.update_configuration(
            expected_revision=1,
            retry_intervals_minutes=[1, 2, 5],
            services=[
                {
                    "service_id": "email",
                    "name": "Email",
                    "phone_numbers": ["+14255550201"],
                }
            ],
        )


@pytest.mark.asyncio
async def test_update_configuration_assigns_new_ids_and_soft_deletes_unused_services():
    collection = MagicMock()
    collection.find_one.return_value = _configuration()
    collection.count_documents.return_value = 0
    updated = _configuration(revision=2, retry_intervals_minutes=[1, 5])
    updated["services"] = [
        {
            "service_id": "email",
            "name": "Messaging",
            "phone_numbers": ["+14255550999"],
            "enabled": True,
        },
        {
            "service_id": "svc-generated",
            "name": "Identity Platform",
            "phone_numbers": ["+14255550888"],
            "enabled": True,
        },
        {
            "service_id": "vpn",
            "name": "vpn",
            "phone_numbers": ["+14255550204"],
            "enabled": False,
        },
    ]
    collection.find_one_and_update.return_value = updated
    store = ServiceDeskStore(SimpleNamespace(collection=collection))

    result = await store.update_configuration(
        expected_revision=1,
        retry_intervals_minutes=[1, 5],
        services=[
            {
                "service_id": "email",
                "name": "Messaging",
                "phone_numbers": ["+14255550999"],
            },
            {
                "service_id": None,
                "name": "Identity Platform",
                "phone_numbers": ["+14255550888"],
            },
        ],
    )

    assert result["revision"] == 2
    assert [service["name"] for service in result["services"]] == [
        "Messaging",
        "Identity Platform",
    ]
    _, update = collection.find_one_and_update.call_args.args[:2]
    persisted_services = update["$set"]["services"]
    assert persisted_services[1]["service_id"].startswith("svc-")
    assert persisted_services[2]["service_id"] == "vpn"
    assert persisted_services[2]["enabled"] is False


@pytest.mark.asyncio
async def test_release_repeats_last_retry_interval_after_schedule_is_exhausted():
    collection = MagicMock()
    collection.find_one.side_effect = [
        {"work_item_id": "WI-1", "attempt_count": 8},
        _configuration(retry_intervals_minutes=[1, 2, 5]),
    ]
    collection.find_one_and_update.return_value = {"work_item_id": "WI-1", "status": "retry"}
    store = ServiceDeskStore(SimpleNamespace(collection=collection))
    now = datetime(2026, 8, 17, tzinfo=UTC)

    await store.release_work_item(
        "WI-1",
        "worker-a",
        "busy",
        now=now,
    )

    _, update = collection.find_one_and_update.call_args.args[:2]
    assert update["$set"]["retry_interval_seconds"] == 300
    assert update["$set"]["next_attempt_at"] == now + timedelta(minutes=5)
