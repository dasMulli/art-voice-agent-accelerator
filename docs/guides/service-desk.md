# Service desk workflow

The `service_desk` scenario supports inbound ticket intake and generated outbound
confirmation calls.

## Select the scenario

`service_desk` is the shipped default scenario: with no override, startup, the
readiness report, inbound PSTN calls, and browser sessions all begin with
`ServiceDeskIntakeAgent`.

To run a different scenario, set an explicit override — it always wins over the
default:

- Local: `AGENT_SCENARIO=banking` in `.env.local` (or the process environment).
- Deployed (durable): `azd` / ambient `AGENT_SCENARIO` is the durable source of
  truth that postprovision reconciliation uses. Set a durable override with
  `azd env set AGENT_SCENARIO banking`, then run `azd provision` or the
  equivalent postprovision/sync step for your deployment so
  `sync-appconfig.sh` writes the Azure App Configuration key
  `app/agent/scenario`.
- Restore the shipped default in a deployed environment: current
  `azd env --help` exposes `azd env set` but no `azd env unset`, so either
  remove `AGENT_SCENARIO` from the azd environment file you manage directly or
  run `azd env set AGENT_SCENARIO ""`. On the next `azd provision` /
  postprovision sync, `sync-appconfig.sh` treats the value as unset and deletes
  `app/agent/scenario`, which restores the shipped `service_desk` default and
  prevents stale overrides from surviving.
- Deployed (advanced temporary/manual override): you can set App Configuration
  directly with
  `az appconfig kv set --name <appconfig-name> --auth-mode login --label <env> --key app/agent/scenario --value banking`
  for immediate runtime/restart testing. This is not co-equal durable
  configuration: the next `azd provision` / postprovision sync reconciles the
  key back to the azd/ambient `AGENT_SCENARIO` value (or deletes it when that
  durable value is unset/blank). App Configuration is read at startup, so
  restart the backend revision after either change.
- Per session: choose a scenario in the scenario selector. A session-scoped
  selection takes precedence over both the override and the default for that
  session only.

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

The first outbound confirmation round starts as soon as the intake call has
disconnected. A round calls every configured target for the affected service in
order, without a delay between targets. A target that answers and explicitly
confirms the ticket ends the work immediately. Otherwise, a failed, silent, or
disconnected call advances to the next target.

After all targets in a round have been tried, the next round uses the retry sequence
in the persisted Service Desk configuration. For example, `1;3;5` waits 1 minute
after the first complete round, then 3 minutes after the second, and 5 minutes after
the third. The final value repeats for every later round. Changing the sequence does
not recalculate a retry that is already scheduled.

Confirmation eligibility expires 24 hours after ticket creation. Voicemail,
silence, and disconnected calls are not confirmations.

## Configure service routing and retries

When **service_desk** is the active scenario, open the scenario selector and choose
**Configure Service Desk…**. The settings dialog manages:

- The ordered retry sequence in minutes.
- Service names presented by the intake agent.
- The ordered callback targets called for each service.
- Adding, renaming, and removing service routes.

Retry values must be whole minutes from 1 through 1440. At least one and at most
20 values are allowed. Service names must be unique regardless of case. Each service
accepts one to 10 semicolon-separated callback targets. A target is either an E.164
number, such as `+15551234567`, or the exact `%initial_caller%` placeholder.

`%initial_caller%` resolves to the raw inbound ACS caller ID captured when the ticket
was created. It does not use the callback number confirmed during intake. If the
inbound call has no valid E.164 caller ID, that placeholder is skipped. Resolved
duplicate numbers are called only once per round, preserving their first configured
position.

Configuration is global and stored in Cosmos DB. Updates use a revision check so
one administrator cannot overwrite another administrator's changes. If a conflict
occurs, the dialog reloads the latest saved values.

Open callback work snapshots the current service targets when a round starts, so a
configuration change applies to its next round and cannot reorder one already in
progress. A service cannot be removed while open callback work references it.
Completed tickets keep their historical service snapshot.

On first startup after upgrading, the application creates the configuration from
the previous defaults: Email, Network, Payroll, and VPN routes with one callback
target each and a single 10-minute retry interval. Existing single-number route
documents are migrated to one-element target lists. Active legacy work items are
linked to the corresponding stable service IDs and preserve their current retry
position without changing completed ticket history.

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
