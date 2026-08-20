# Service desk workflow

The `service_desk` scenario supports inbound ticket intake and generated outbound
confirmation calls.

## Select the scenario

Choose **service_desk** in the scenario selector. For deployments that use the
existing global scenario setting, set `AGENT_SCENARIO=service_desk`.

Inbound calls use `ServiceDeskIntakeAgent`, the scenario's `start_agent`.
Generated outbound confirmation calls must use the same scenario and explicitly
select `StandbyConfirmationAgent`; listing it in the scenario makes it available
without changing the inbound start agent.

## Demo registries

`lookup_known_caller` uses the fake caller registry to find a caller by the
incoming phone number and pre-fill the name and callback number. Unknown callers
provide those values during intake. The fake service registry supplies the
service names accepted for `affected_service`. These registries contain demo data
only and should not be treated as authoritative identity or production inventory.

## Ticket lifecycle

The intake agent collects name, callback number, urgency, affected service, and a
description, then generates a short description. It reads back every field and
calls `create_service_desk_ticket` only after explicit confirmation.

Ticket and linked work-item documents are stored in Cosmos DB. Confirmation
events and correction notes are appended to the ticket or work-item history.
`StandbyConfirmationAgent` records an explicit confirmation with
`confirmed=true`. Corrections are appended with `confirmed=false` and
`correction_note`; the original submitted fields remain unchanged and the work
item completes with corrections.

The outbound workflow retries an unanswered confirmation after 10 minutes.
Confirmation eligibility expires 24 hours after ticket creation. Voicemail,
silence, and disconnected calls are not confirmations.

## Inspect tickets

The service desk inspection API is available at:

```text
/api/v1/service-desk/tickets
```

Use it to inspect ticket state, linked work items, and confirmation history.
