# Service Desk Call Simulator

`service-desk-call-simulator\` is a Windows WinForms desktop app that places a
real outbound PSTN call from Azure Communication Services (ACS) to a running or
deployed service-desk voice agent. The simulator acts as a scripted caller,
publishes ACS callbacks through a temporary Dev Tunnel, listens to the service
desk over a local speaker, and uses Azure AI Services for grounded reply
generation plus speech recognition and synthesis.

## Purpose and architecture

This tool is for local operator and demo use. It does **not** simulate the
service desk in-process; it calls the real service-desk destination number.

High-level flow:

1. Load `service-desk-call-simulator\src\ServiceDeskCallSimulator\appsettings.json`
   plus any `SDCS__` environment overrides.
2. Authenticate with Azure using a deterministic local developer credential
   chain (Azure CLI first, then Visual Studio, then Azure PowerShell), bounded
   by an authentication deadline.
3. Discover outbound-capable ACS purchased phone numbers and select the
   preferred source only if it is actually present.
4. Start a loopback callback host on `127.0.0.1` and expose it through a
   temporary anonymous GitHub-authenticated Dev Tunnel (`create`, `port create`,
   `host <id>`, then poll `show <id> --json` for the port's public URL).
5. Create the outbound ACS Call Automation PSTN call with bidirectional
   16 kHz PCM media streaming.
6. Run the scripted caller conversation:
   - speech recognition on inbound service-desk audio;
   - grounded reply generation constrained to the selected preset facts;
   - speech synthesis for caller responses;
   - optional local listen-along playback that can be muted without affecting
     the real call audio.

## Prerequisites

- Windows desktop environment.
- .NET 10 SDK to build and test.
- .NET 10 Desktop Runtime to run the published or built app.
- Azure CLI installed and signed in:
  - `az login`
  - `az account show`
- `devtunnel` CLI installed and available on `PATH`.
- GitHub sign-in for Dev Tunnels. The app triggers the browser flow when needed;
  you can also pre-authenticate with:
  - `devtunnel user login -g`
- A running or deployed service-desk voice agent that answers the configured
  destination PSTN number. Its ACS resource must have an enabled
  `Microsoft.Communication.IncomingCall` Event Grid subscription targeting the
  backend, and the backend must activate the `service_desk` scenario with
  `ServiceDeskIntakeAgent` as its start agent.
- A local speaker or other output device if you want live listen-along audio.

Quick local checks:

```powershell
az account show --output table
devtunnel --version
devtunnel user show
dotnet --info
```

## Checked-in defaults

The checked-in defaults live in
`service-desk-call-simulator\src\ServiceDeskCallSimulator\appsettings.json`.

### ACS

- Resource name: `acs-ai-demos`
- Resource group: `rg-demos`
- Endpoint: `https://acs-ai-demos.europe.communication.azure.com`
- Preferred source caller ID: `+43800223359`
  - The app selects this number **only if discovery finds it**.
- Editable default destination: `+33801150311`
- Local callback port: `0` (ephemeral loopback port)

### Azure AI Services

- Endpoint:
  `https://aif-demos-swedencentral.cognitiveservices.azure.com/`
- Text deployment: `gpt-5.6-luna`

### Speech locales and voices

- English recognition locale: `en-US`
- English voice: `en-US-JennyNeural`
- German recognition locale: `de-DE`
- German voice: `de-DE-KatjaNeural`
- Polish recognition locale: `pl-PL`
- Polish voice: `pl-PL-ZofiaNeural`

### Presets

The simulator ships with exactly nine editable presets:

1. `[EN] Printer not working`
2. `[DE] Drucker funktioniert nicht`
3. `[EN] VPN access`
4. `[DE] VPN-Zugriff`
5. `[EN] Email outage`
6. `[DE] E-Mail-Ausfall`
7. `[EN] Payroll question`
8. `[DE] Frage zur Gehaltsabrechnung`
9. `[DE→PL] Netzwerkstörung / awaria sieci`

Each preset already carries its locale and voice; there is no separate language
selector.

### Showcase preset: `[DE→PL] Netzwerkstörung / awaria sieci`

The ninth preset demonstrates a **mid-call caller language switch**. It opens in
German and switches the caller to Polish deterministically:

- The opening line is spoken in German with `de-DE-KatjaNeural`.
- After the **first finalized Service Desk transcript turn**, every generated
  caller reply is grounded to Polish and synthesized with `pl-PL-ZofiaNeural`.
- The locale and voice labels show the transition explicitly, for example
  `de-DE → pl-PL (after 1 service desk turn)`.

What it demonstrates:

- **Deterministic, code-owned switching.** The switch is an optional
  `CallerLanguageSwitchPolicy` carried as a first-class preset/draft/snapshot
  fact (target locale, target voice, finalized-turn threshold). It is never
  inferred from free-text script details, and the model is never asked whether
  to switch.
- **No prompt/voice drift.** `CallerResponseLanguageResolver` counts finalized
  Service Desk turns and returns the current response locale, language name, and
  voice. Both the grounded prompt and the caller TTS call use that single result,
  so the prompted language and the synthesized voice always agree. Interim
  recognition fragments never count toward the threshold.
- **Grounded, no-invention behavior is preserved.** Each turn's prompt states the
  exact current response language plus the already-applied transition rule, while
  the immutable caller facts and the strict JSON decision schema stay unchanged.
- **Recognition is unaffected.** Recognition follows the remote service desk
  language (`de-DE` here) and is never restarted mid-call just because the caller
  TTS language changed.

## Azure authentication and RBAC

The simulator is designed for passwordless auth. It does **not** require or
store ACS connection strings, API keys, or Dev Tunnel tokens.

### Azure authentication

Every Azure client (ACS Call Automation, ACS Phone Numbers, Azure OpenAI, and
Speech) shares **one** `TokenCredential` instance, registered once in
`Azure\ServiceCollectionExtensions.cs` and built by
`Azure\AzureCredentialFactory.cs`. It is a deterministic local developer chain,
not `DefaultAzureCredential`:

| Order | Credential                 | Sign-in                       |
| ----- | -------------------------- | ----------------------------- |
| 1     | `AzureCliCredential`       | `az login`                    |
| 2     | `VisualStudioCredential`   | Visual Studio account         |
| 3     | `AzurePowerShellCredential`| `Connect-AzAccount` (optional)|

Why not `DefaultAzureCredential`: its probe order can attempt slower developer
or managed-identity sources before the Azure CLI and stall startup for a long
time even when `az account get-access-token` returns immediately. The
deterministic chain fixes the order, keeps a single shared token cache, and
bounds each process-based credential with a 15-second process timeout.

The initial authentication probe during startup is additionally bounded by a
25-second deadline tied to the window lifetime. If the deadline elapses, the
status header shows an inline **Error** with a **Retry** button instead of
hanging on `Azure authentication: InProgress`.

Use a user, service principal, or managed identity that can:

1. read purchased ACS phone numbers on the configured ACS resource;
2. create and control ACS Call Automation calls on that resource;
3. invoke Azure AI Services / Azure OpenAI / Speech on the configured AI
   Services endpoint by using Microsoft Entra ID.

Built-in role names can vary by service and tenant, so verify by capability at
the exact resource scope instead of assuming one universal role name.

### Verify the current Azure principal

```powershell
az login
az account show --output table
az ad signed-in-user show --query "{displayName:displayName,id:id,userPrincipalName:userPrincipalName}" --output table
```

If you are using a service principal or managed identity, inspect that principal
instead of `az ad signed-in-user show`.

### Verify ACS scope and assignments

```powershell
$acsResourceId = az resource show `
  --resource-group rg-demos `
  --name acs-ai-demos `
  --resource-type Microsoft.Communication/communicationServices `
  --query id -o tsv

$principalObjectId = az ad signed-in-user show --query id -o tsv

az role assignment list `
  --assignee $principalObjectId `
  --scope $acsResourceId `
  --include-inherited `
  --output table
```

Confirm the listed assignments give your principal the ability to:

- read purchased phone numbers;
- create outbound Call Automation calls;
- hang up and receive call/media callbacks.

### Verify AI Services scope and assignments

First identify the AI Services account behind the configured endpoint:

```powershell
az cognitiveservices account list --resource-group rg-demos --output table
```

Then inspect the resource that matches
`https://aif-demos-swedencentral.cognitiveservices.azure.com/`:

```powershell
$aiResourceName = "<resource-name-for-aif-demos-swedencentral endpoint>"
$aiResourceId = az cognitiveservices account show `
  --name $aiResourceName `
  --resource-group rg-demos `
  --query id -o tsv

az role assignment list `
  --assignee $principalObjectId `
  --scope $aiResourceId `
  --include-inherited `
  --output table

az account get-access-token `
  --scope https://cognitiveservices.azure.com/.default `
  --query expiresOn -o tsv
```

Use least privilege. For Speech specifically, Microsoft Learn documents
Speech-specific data-plane roles; verify that the assigned role grants speech
recognition and synthesis, not only management-plane access.

## Configuration

Primary config file:

- `service-desk-call-simulator\src\ServiceDeskCallSimulator\appsettings.json`

The application loads JSON first and then applies environment-variable
overrides with the prefix `SDCS__`.

### Fields

| Setting | Meaning |
| --- | --- |
| `Acs:Endpoint` | ACS resource endpoint |
| `Acs:ResourceGroup` | ACS resource group used for operator context/documentation |
| `Acs:ResourceName` | ACS resource name used for operator context/documentation |
| `Acs:PreferredCallerId` | Preferred source number, selected only if discovery finds it |
| `Acs:DefaultDestination` | Default destination PSTN number shown in the UI and editable per run |
| `Acs:LocalCallbackPort` | Loopback port for the local callback host; `0` means ephemeral |
| `AiServices:Endpoint` | Azure AI Services custom-subdomain endpoint |
| `AiServices:TextDeployment` | Azure OpenAI text deployment used for grounded replies |
| `Speech:English:RecognitionLocale` | English recognition locale |
| `Speech:English:Voice` | English neural voice |
| `Speech:German:RecognitionLocale` | German recognition locale |
| `Speech:German:Voice` | German neural voice |
| `Speech:Polish:RecognitionLocale` | Polish locale used by the `[DE→PL]` switch preset |
| `Speech:Polish:Voice` | Polish neural voice used after the switch |

### PowerShell override examples

```powershell
$env:SDCS__Acs__DefaultDestination = "+33123456789"
$env:SDCS__Acs__PreferredCallerId = "+15551234567"
$env:SDCS__Acs__LocalCallbackPort = "5055"
$env:SDCS__AiServices__TextDeployment = "gpt-5.6-luna"
$env:SDCS__Speech__German__Voice = "de-DE-KatjaNeural"
$env:SDCS__Speech__Polish__Voice = "pl-PL-ZofiaNeural"
```

Clear an override after the run:

```powershell
Remove-Item Env:SDCS__Acs__DefaultDestination
```

### Validation

- Source numbers come only from ACS purchased-number discovery.
- `PreferredCallerId`, destination, and preset callback numbers must be valid
  E.164 values.
- Invalid destination or callback numbers disable the **Call** action until
  fixed.
- Invalid configuration fails fast at startup with safe diagnostics.

### Secret handling

- No secrets are stored in `appsettings.json`.
- The app authenticates passwordlessly with a single shared, deterministic local
  developer credential chain (see
  [Azure authentication](#azure-authentication)); no client secret, connection
  string, or access key is ever read.
- Do not add connection strings, ACS keys, OpenAI keys, or Speech keys to this
  project.

## Build, run, and test

From the repository root:

```powershell
dotnet restore .\service-desk-call-simulator\ServiceDeskCallSimulator.sln
dotnet build .\service-desk-call-simulator\ServiceDeskCallSimulator.sln -c Release -warnaserror --no-restore
dotnet test .\service-desk-call-simulator\ServiceDeskCallSimulator.sln -c Release --no-build
dotnet run --project .\service-desk-call-simulator\src\ServiceDeskCallSimulator\ServiceDeskCallSimulator.csproj -c Release
```

To publish a single-file, self-contained, untrimmed Windows executable, run:

```powershell
.\service-desk-call-simulator\build.ps1
```

To sign the executable, connect the signing token, open its middleware, and
provide the certificate and RFC 3161 timestamp service explicitly:

```powershell
.\service-desk-call-simulator\build.ps1 --sign `
    -CertificateThumbprint <certificate-thumbprint> `
    -TimestampUrl <rfc3161-timestamp-url>
```

The signing path uses the x86 Windows SDK SignTool for compatibility with
smart-card minidrivers. SignTool's own architecture does not constrain the
architecture of the executable being signed. The token middleware may prompt
for its PIN; the script never reads or stores the PIN.
The executable is written to
`service-desk-call-simulator\artifacts\publish\win-x64\ServiceDeskCallSimulator.exe`.
For signed builds, verbose SignTool diagnostics are retained under
`service-desk-call-simulator\artifacts\logs\`.

Use `-RuntimeIdentifier win-arm64` for a native ARM64 build. `-OutputDirectory`,
`-Configuration`, and `-SignToolPath` can override the build and tool defaults.

If you want to inspect the checked-in configuration file directly:

```powershell
Get-Content .\service-desk-call-simulator\src\ServiceDeskCallSimulator\appsettings.json
```

## Window layout and high DPI

The main window is a resizable, per-monitor-v2 DPI-aware form that opens at
1160x760 logical pixels and never shrinks below 960x680 logical pixels. It uses
a three-row root `TableLayoutPanel`:

| Row | Content                      | Sizing                                  |
| --- | ---------------------------- | --------------------------------------- |
| 0   | Status header                | AutoSize, capped at 176 logical px      |
| 1   | Working area `SplitContainer`| 100% of the remaining client height     |
| 2   | Command bar                  | AutoSize, capped at 60 logical px       |

The status header is itself a row-based `TableLayoutPanel` with one explicit
AutoSize row per concern: status, checklist, public callback host, selected
model, and inline initialization error + **Retry**. Rows anchor rather than
dock, so no row can be inflated by a child stretching to fill its cell.

Command-bar buttons carry explicit logical sizes (104x30) plus hard maximum
bounds (160x40) and `Anchor = None`, so they never stretch into oversized blank
rectangles.

`AutoScaleMode` is `Dpi` with `AutoScaleDimensions = (96, 96)`. Using a font
dimension such as `(7, 15)` with `AutoScaleMode.Dpi` would make the runtime
scale factor 96/7 x 96/15 and inflate every explicitly sized control.

All of these numbers live in `UI\ChromeLayoutMetrics.cs`, a WinForms-independent
sizing policy shared by the designer and the tests, so the two can never drift
apart. `ChromeLayoutMetricsTests` covers the DPI arithmetic directly, and
`MainFormChromeLayoutTests` lays the real form out at 96/144/192 DPI and at the
reported 5146x2186 desktop.

> **UI test rule:** tests must never show a native window. WinForms layout does
> not need window handles, so the layout tests set `ClientSize` and call
> `PerformLayout()` on an invisible, handle-free form and assert that no handle
> was created. Showing the window and then changing a font sends `WM_SETFONT`
> into the RichEdit control, which throws `SEHException` and pops an interactive
> just-in-time debugging dialog inside `testhost`.

## UX walkthrough

1. **Start the app**
   - The window opens immediately.
   - Initialization then runs asynchronously.
2. **Azure authentication**
   - The app validates that the current Azure identity can obtain tokens,
     using Azure CLI first, then Visual Studio, then Azure PowerShell.
   - The probe is bounded by a 25-second deadline; on timeout the header shows
     an inline error with **Retry** instead of hanging.
3. **ACS number discovery**
   - The caller-ID list is populated from purchased outbound-capable ACS numbers.
   - If `+43800223359` is present, it becomes the default selection.
   - If not, the list remains valid but no preferred source is forced.
4. **Dev Tunnel readiness**
   - The local callback host binds to loopback.
   - If Dev Tunnel GitHub sign-in is needed, the app enters a sign-in-required
     state while the browser flow completes.
5. **Script selection and editing**
   - Choose one of the English or German presets, or the `[DE→PL]` preset to
     demonstrate a mid-call switch from German to Polish.
   - Edit the script fields locally if needed.
   - If you switch presets with unsaved edits, the app asks you to confirm.
6. **Place the call**
   - Click **Call** once the form is ready and numbers are valid.
   - The app locks setup controls during the active call.
7. **Observe the live interaction**
   - The transcript shows caller, service-desk, and system lines.
   - Local listen-along audio can be muted with **Mute local audio** without
     affecting the actual PSTN call audio.
   - The local monitor plays one source at a time: caller playback owns the
     speaker while the caller speaks, and audible remote audio is played the
     rest of the time. Silent comfort frames are never played locally. This
     affects only local listen-along, not the PSTN call or recognition.
8. **Hang up or let the conversation finish**
   - Use **Hang Up** for a manual end.
   - The grounded caller can also end the call naturally.
9. **Review and retry**
   - After completion, you can review transcript and diagnostics.
   - **Retry** reinitializes after a startup or tunnel failure.
10. **Close the app**
    - Shutdown first ends any active call, then tears down the owned tunnel.

## Security and privacy

- The simulator requires a temporary **anonymous public Dev Tunnel** so ACS can
  reach the local callback host from Azure.
- The callback and media routes use a randomized path token per app session.
- The callback host accepts callbacks only for actively registered
  `callConnectionId` values and requires exact correlation. Media WebSocket
  upgrades are correlated with the `x-ms-call-connection-id` header that ACS
  sends, because the media transport URI is supplied to `CreateCall` before a
  call connection ID exists. Requests without a known value, or with two
  disagreeing correlation values, are rejected.
- Retry and shutdown delete the exact tunnel owned by the current session.
- Diagnostics are intentionally reduced to safe operational messages and do not
  include prompts, transcript content, audio bytes, raw SDK payloads, access
  tokens, or connection strings.
- Script edits and transcript content are runtime memory only; they are not
  persisted by the app.
- Public tunnel exposure and real PSTN calls can incur cost and operational
  exposure. Use approved demo numbers only.

## Troubleshooting

### Azure authentication or RBAC failure

Symptoms:

- initialization stops during Azure authentication;
- `Azure authentication: Failed` with an inline "did not complete within 25
  seconds" error and a **Retry** button;
- number discovery fails immediately;
- call creation or grounded reply generation fails with Azure-service errors.

Actions:

- run `az login` and confirm the intended subscription with `az account show`;
- confirm the token itself is obtainable:
  `az account get-access-token --scope https://communication.azure.com/.default`;
- if you sign in through Visual Studio instead, confirm the Visual Studio
  account is the intended one (the app tries Azure CLI first, then Visual
  Studio, then Azure PowerShell);
- verify role assignments at the ACS and AI Services resource scopes;
- if role assignments changed recently, wait for propagation and retry.

### No ACS numbers discovered or preferred number is absent

Symptoms:

- caller ID list is empty;
- `+43800223359` is not selected even though the app initialized.

Actions:

- verify the current principal can read purchased phone numbers;
- check that the ACS resource actually owns outbound-capable numbers;
- remember that the preferred number is used only when discovery returns it.

### Dev Tunnel CLI missing or sign-in expired

Symptoms:

- initialization reports that Dev Tunnels CLI was not found;
- initialization pauses for sign-in or repeatedly returns to sign-in-required.

Actions:

- ensure `devtunnel` is installed and on `PATH`;
- run `devtunnel --version`;
- run `devtunnel user show`;
- if needed, run `devtunnel user login -g` and complete the browser flow.

### Dev Tunnel initialization stalls or fails after sign-in

The simulator drives the CLI in exactly this order, and each step matters:

```powershell
devtunnel create  <id> --allow-anonymous --json
devtunnel port create <id> --port-number <loopback port> --protocol http --json
devtunnel host <id>            # no port/access flags - see below
devtunnel show <id> --json     # polled until tunnel.ports[].portUri appears
```

- `devtunnel host <id>` must carry **no** `--port-number`, `--protocol`, or
  `--allow-anonymous`. Passing them to `host` on an existing tunnel exits 1 with
  *"Invalid arguments. Batch update of ports is not supported. Add, update, or
  delete ports individually instead."* The port and anonymous access are already
  configured by `create` and `port create`.
- A port's public URL exists only while a host is running. `port create --json`
  and `port show` do not contain it; only `show <id> --json` reports
  `tunnel.ports[].portUri`.
- The app polls `show --json` for the entry whose `portNumber` matches the
  loopback callback port, until it appears or the 30-second startup timeout
  elapses. On timeout, on host exit, or on an unusable/ambiguous URL the tunnel
  is deleted and the header shows an inline error with **Retry**.

To reproduce manually, run the four commands above in two shells (keep `host`
running in the first, run `show` in the second).

### Callback or media connection failure

Symptoms:

- the call starts but never connects media;
- callbacks never arrive;
- the call faults shortly after dialing.

Actions:

- confirm the temporary tunnel is active and the callback host stage completed;
- retry to force a fresh loopback port and fresh tunnel;
- avoid local firewalls or proxies that block loopback hosting or WebSocket use.

### Model, Speech, or deployment permission failure

Symptoms:

- the call connects but the simulator faults during recognition or response;
- initialization succeeds but replies never start.

Actions:

- verify the AI Services endpoint and deployment name;
- confirm the principal can invoke the configured AI Services/OpenAI/Speech
  endpoint with Entra ID;
- confirm the endpoint is a custom-subdomain endpoint, not a regional shortcut.

### No local audio while the call continues

Symptoms:

- transcript continues but you hear nothing locally.

Actions:

- check the Windows output device;
- verify **Mute local audio** is off;
- review diagnostics for a local audio monitor fault;
- the real PSTN call can continue even if local playback is disabled.

### Destination does not answer or wrong scenario answers

Symptoms:

- call rings out;
- a different voice bot or service answers;
- the scripted conversation does not match the remote service-desk workflow.

Actions:

- confirm the destination number is correct and E.164 formatted;
- verify the remote service-desk environment is running and reachable;
- list the destination ACS system topic's subscriptions and confirm one includes
  `Microsoft.Communication.IncomingCall` and targets the deployed backend:

  ```powershell
  az eventgrid system-topic event-subscription list `
    --resource-group <destination-resource-group> `
    --system-topic-name <destination-acs-system-topic> `
    --output table
  ```

- query the backend readiness endpoint and confirm the agent details report
  `start_agent=ServiceDeskIntakeAgent` for the `service_desk` scenario;
- choose a preset that matches the scenario under test.

### Cleanup or tunnel deletion failure

Symptoms:

- retry remains blocked after tunnel failure cleanup;
- closing the window leaves a cleanup error message.

Actions:

- retry the cleanup path once network/CLI issues are resolved;
- if necessary, inspect and remove the retained tunnel manually with
  `devtunnel list` / `devtunnel delete <TUNNELID>`;
- fix the CLI/auth issue before the next run.

## Limitations

- Windows-only UI and local audio monitoring.
- Requires real ACS, AI Services, PSTN, and Dev Tunnel connectivity for live use.
- The anonymous tunnel is for demo/operator workflows, not hardened production
  ingress.
- No offline mode for the live call path.
- Local listen-along depends on a working audio output device.

## Live-smoke checklist

Before a demo:

1. `az account show` returns the expected subscription and principal.
2. `az account get-access-token --scope https://communication.azure.com/.default`
   succeeds; the app's authentication stage should complete just as quickly.
3. `devtunnel user show` confirms an active login.
4. The destination ACS system topic has an enabled
   `Microsoft.Communication.IncomingCall` subscription pointing at the live
   backend.
5. The backend readiness response reports the `service_desk` scenario and
   `ServiceDeskIntakeAgent` as the start agent.
6. The service-desk destination number is live and answers the intended
   scenario.
7. The window opens at a usable size without needing to be maximized, and the
   working area (setup + live call panes) takes most of the window height.
8. The caller-ID list populates and the selected source number is correct.
9. The callback host and Dev Tunnel initialization stages complete.
10. You can place one short call, hear local audio, see transcript activity, and
   hang up cleanly.
