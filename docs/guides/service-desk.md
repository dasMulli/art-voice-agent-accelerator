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
provide those values during intake. The persisted Service Desk configuration
supplies the service names accepted for `affected_service`. The caller registry
contains demo data only and should not be treated as authoritative identity.

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

The first outbound confirmation starts as soon as the intake call has disconnected.
Later attempts use the retry sequence in the persisted Service Desk configuration.
For example, `1;2;5;10;30` waits 1 minute after the first failed attempt, then 2,
5, 10, and 30 minutes. The final value repeats for every later failure. Changing
the sequence does not recalculate a retry that is already scheduled; the new
sequence is used after the next failed or disconnected attempt.

Confirmation eligibility expires 24 hours after ticket creation. Voicemail,
silence, and disconnected calls are not confirmations.

## Configure service routing and retries

When **service_desk** is the active scenario, open the scenario selector and choose
**Configure Service Desk…**. The settings dialog manages:

- The ordered retry sequence in minutes.
- Service names presented by the intake agent.
- The E.164 phone number called for each service.
- Adding, renaming, and removing service routes.

Retry values must be whole minutes from 1 through 1440. At least one and at most
20 values are allowed. Service names must be unique regardless of case, and every
phone number must use E.164 format, such as `+15551234567`.

Configuration is global and stored in Cosmos DB. Updates use a revision check so
one administrator cannot overwrite another administrator's changes. If a conflict
occurs, the dialog reloads the latest saved values.

Open callback work resolves the current service name and number before each call,
so number and rename changes apply on its next attempt. A service cannot be removed
while open callback work references it. Completed tickets keep their historical
service snapshot.

On first startup after upgrading, the application creates the configuration from
the previous defaults: Email, Network, Payroll, and VPN routes with a single
10-minute retry interval. Active legacy work items are linked to the corresponding
stable service IDs without changing completed ticket history.

## Intake call completion

After ticket creation succeeds, the intake agent gives one final response in the
caller's conversation language. It thanks the caller, states the actual ticket ID,
explains that the follow-up call can now occur, and says goodbye. New caller input is
ignored while this terminal response is playing. SpeechCascade waits for tracked TTS
playback to drain; VoiceLive waits for the matching response audio-complete event
and the estimated duration of queued PCM playback. The service then hangs up the ACS
call and closes the media WebSocket.

The linked callback work item is initially stored as
`awaiting_intake_disconnect`. It is not eligible for dispatcher claims until ACS
emits `CallDisconnected` for the intake call. A manual caller hang-up follows the
same path. Disconnect is recorded before work activation so an event racing ticket
creation is reconciled after persistence. Browser sessions use confirmed local
WebSocket closure as the equivalent gate. If hang-up or socket closure fails, or the
disconnect event has not arrived, the callback remains blocked rather than racing
the still-active intake call.

## Inspect tickets

The service desk inspection API is available at:

```text
/api/v1/service-desk/tickets
```

Use it to inspect ticket state, linked work items, and confirmation history.
