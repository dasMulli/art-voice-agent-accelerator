"""Read-only service desk inspection endpoints."""

from __future__ import annotations

import asyncio
from typing import TYPE_CHECKING, Annotated, cast

from apps.artagent.backend.api.v1.schemas.service_desk import (
    AttemptHistoryEntry,
    ConfirmationEvent,
    CorrectionNote,
    ServiceDeskTicket,
    ServiceDeskTicketDetailResponse,
    ServiceDeskTicketListResponse,
    ServiceDeskTicketSummary,
    ServiceDeskWorkItem,
)
from fastapi import APIRouter, HTTPException, Query, Request, status

if TYPE_CHECKING:
    from apps.artagent.backend.src.services.service_desk.store import ServiceDeskStore

router = APIRouter()


def _store_from_request(request: Request) -> ServiceDeskStore:
    """Return the configured service desk store or fail without creating one."""
    store = getattr(request.app.state, "service_desk_store", None)
    if store is None:
        raise HTTPException(
            status_code=status.HTTP_503_SERVICE_UNAVAILABLE,
            detail="Service desk store is unavailable",
        )
    return cast("ServiceDeskStore", store)


def _attempt_history(work_item: dict | None) -> list[AttemptHistoryEntry]:
    """Return persisted attempts or the latest attempt fields maintained by the store."""
    if work_item is None:
        return []
    persisted_history = work_item.get("attempt_history", [])
    if persisted_history:
        return [AttemptHistoryEntry.model_validate(attempt) for attempt in persisted_history]
    if work_item.get("attempt_count", 0) <= 0:
        return []
    return [
        AttemptHistoryEntry(
            attempt_number=work_item["attempt_count"],
            call_id=work_item.get("call_id"),
            status=work_item.get("status"),
            started_at=work_item.get("attempt_started_at"),
            ended_at=(
                work_item.get("completed_at")
                or work_item.get("released_at")
                or work_item.get("expired_at")
            ),
            reason=work_item.get("last_release_reason"),
        )
    ]


@router.get(
    "/tickets",
    response_model=ServiceDeskTicketListResponse,
    tags=["Service Desk"],
)
async def list_tickets(
    request: Request,
    status_filter: Annotated[str | None, Query(alias="status")] = None,
    offset: Annotated[int, Query(ge=0)] = 0,
    limit: Annotated[int, Query(ge=1, le=100)] = 100,
) -> ServiceDeskTicketListResponse:
    """List tickets, optionally filtering by callback work status."""
    store = _store_from_request(request)
    ticket_documents, work_item_documents = await asyncio.gather(
        store.list_tickets(limit=0),
        store.list_work_items(status=status_filter, limit=0),
    )
    work_items_by_ticket = {
        item["ticket_id"]: item for item in work_item_documents if item.get("ticket_id")
    }
    if status_filter is not None:
        ticket_documents = [
            ticket for ticket in ticket_documents if ticket.get("ticket_id") in work_items_by_ticket
        ]

    total = len(ticket_documents)
    page = ticket_documents[offset : offset + limit]
    summaries = [
        ServiceDeskTicketSummary.model_validate(
            {
                **ticket,
                "status": (work_items_by_ticket.get(ticket["ticket_id"], {}).get("status")),
            }
        )
        for ticket in page
    ]
    return ServiceDeskTicketListResponse(
        tickets=summaries,
        total=total,
        offset=offset,
        limit=limit,
    )


@router.get(
    "/tickets/{ticket_id}",
    response_model=ServiceDeskTicketDetailResponse,
    tags=["Service Desk"],
)
async def get_ticket(
    ticket_id: str,
    request: Request,
) -> ServiceDeskTicketDetailResponse:
    """Get a ticket and its associated callback inspection data."""
    store = _store_from_request(request)
    ticket_document = await store.get_ticket(ticket_id)
    if ticket_document is None:
        raise HTTPException(
            status_code=status.HTTP_404_NOT_FOUND,
            detail="Service desk ticket not found",
        )

    work_item_document = await store.get_work_item(ticket_document["work_item_id"])
    correction_notes = [
        CorrectionNote.model_validate(note) for note in ticket_document.get("correction_notes", [])
    ]
    confirmation_events = [
        ConfirmationEvent.model_validate(event)
        for event in ticket_document.get("confirmation_events", [])
    ]
    attempt_history = _attempt_history(work_item_document)

    return ServiceDeskTicketDetailResponse(
        ticket=ServiceDeskTicket.model_validate(ticket_document),
        work_item=(
            ServiceDeskWorkItem.model_validate(work_item_document)
            if work_item_document is not None
            else None
        ),
        status=work_item_document.get("status") if work_item_document else None,
        attempt_history=attempt_history,
        correction_notes=correction_notes,
        confirmation_events=confirmation_events,
    )
