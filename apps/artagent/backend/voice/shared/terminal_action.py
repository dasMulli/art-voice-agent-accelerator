"""Shared contract for tool-triggered terminal voice responses."""

from __future__ import annotations

from dataclasses import dataclass
from typing import Any

TERMINATE_AFTER_RESPONSE = "terminate_after_response"


@dataclass(frozen=True)
class TerminalAction:
    """Describe a request to terminate a voice session after its final response."""

    reason: str
    ticket_id: str | None = None
    work_item_id: str | None = None


def terminal_action_from_result(result: object) -> TerminalAction | None:
    """Parse a successful tool result into a terminal action."""
    if not isinstance(result, dict) or result.get("success") is not True:
        return None
    call_control = result.get("call_control")
    if not isinstance(call_control, dict):
        return None
    if call_control.get("action") != TERMINATE_AFTER_RESPONSE:
        return None
    return TerminalAction(
        reason=str(call_control.get("reason") or "normal"),
        ticket_id=str(call_control["ticket_id"]) if call_control.get("ticket_id") else None,
        work_item_id=(
            str(call_control["work_item_id"]) if call_control.get("work_item_id") else None
        ),
    )


def build_terminal_action(
    *,
    reason: str = "normal",
    ticket_id: str | None = None,
    work_item_id: str | None = None,
) -> dict[str, Any]:
    """Build the serialized terminal-action payload returned by a tool."""
    return {
        "action": TERMINATE_AFTER_RESPONSE,
        "reason": reason,
        "ticket_id": ticket_id,
        "work_item_id": work_item_id,
    }
