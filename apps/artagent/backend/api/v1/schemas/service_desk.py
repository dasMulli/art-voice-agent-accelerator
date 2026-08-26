"""Typed response schemas for service desk inspection."""

from __future__ import annotations

from datetime import datetime
from typing import Annotated

from pydantic import BaseModel, ConfigDict, Field

RetryIntervalMinutes = Annotated[int, Field(strict=True, ge=1, le=1440)]


class CorrectionNote(BaseModel):
    """A correction appended to an immutable ticket."""

    note: str
    timestamp: datetime


class ConfirmationEvent(BaseModel):
    """A recorded ticket confirmation decision."""

    confirmed: bool
    timestamp: datetime


class AttemptHistoryEntry(BaseModel):
    """One recorded callback attempt."""

    attempt_number: int
    call_id: str | None = None
    status: str | None = None
    started_at: datetime | None = None
    ended_at: datetime | None = None
    reason: str | None = None

    model_config = ConfigDict(extra="ignore")


class ServiceDeskTicket(BaseModel):
    """A persisted service desk ticket."""

    ticket_id: str
    work_item_id: str
    name: str
    callback_number: str
    urgency: str
    service_id: str | None = None
    affected_service: str
    description: str
    short_description: str
    confirmation_status: str
    confirmed: bool | None = None
    confirmed_at: datetime | None = None
    correction_notes: list[CorrectionNote] = Field(default_factory=list)
    confirmation_events: list[ConfirmationEvent] = Field(default_factory=list)
    created_at: datetime
    updated_at: datetime

    model_config = ConfigDict(extra="ignore")


class ServiceDeskWorkItem(BaseModel):
    """Callback work associated with a service desk ticket."""

    work_item_id: str
    ticket_id: str
    callback_number: str
    service_id: str | None = None
    standby_number: str
    status: str
    attempt_count: int
    retry_interval_seconds: int
    next_attempt_at: datetime | None = None
    expires_at: datetime
    call_id: str | None = None
    lease_owner: str | None = None
    lease_until: datetime | None = None
    claimed_at: datetime | None = None
    attempt_started_at: datetime | None = None
    completed_at: datetime | None = None
    released_at: datetime | None = None
    expired_at: datetime | None = None
    last_release_reason: str | None = None
    created_at: datetime
    updated_at: datetime

    model_config = ConfigDict(extra="ignore")


class ServiceDeskTicketSummary(BaseModel):
    """Compact ticket representation used by list responses."""

    ticket_id: str
    work_item_id: str
    name: str
    urgency: str
    affected_service: str
    short_description: str
    confirmation_status: str
    status: str | None
    created_at: datetime
    updated_at: datetime


class ServiceDeskTicketListResponse(BaseModel):
    """A paginated service desk ticket list."""

    tickets: list[ServiceDeskTicketSummary]
    total: int
    offset: int
    limit: int


class ServiceDeskTicketDetailResponse(BaseModel):
    """A ticket and its associated callback inspection data."""

    ticket: ServiceDeskTicket
    work_item: ServiceDeskWorkItem | None
    status: str | None
    attempt_history: list[AttemptHistoryEntry]
    correction_notes: list[CorrectionNote]
    confirmation_events: list[ConfirmationEvent]


class ServiceDeskServiceRoute(BaseModel):
    """One editable service-to-phone route."""

    service_id: str
    name: str
    phone_number: str


class ServiceDeskServiceRouteUpdate(BaseModel):
    """Editable route fields; new services omit the server-owned ID."""

    service_id: str | None = None
    name: str
    phone_number: str


class ServiceDeskConfigurationResponse(BaseModel):
    """Current persisted routing and retry configuration."""

    revision: int
    retry_intervals_minutes: list[int]
    services: list[ServiceDeskServiceRoute]
    created_at: datetime
    updated_at: datetime


class ServiceDeskConfigurationUpdate(BaseModel):
    """Full editable configuration replacement."""

    expected_revision: int = Field(ge=1)
    retry_intervals_minutes: list[RetryIntervalMinutes] = Field(min_length=1, max_length=20)
    services: list[ServiceDeskServiceRouteUpdate] = Field(min_length=1)
