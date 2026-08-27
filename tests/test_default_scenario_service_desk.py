"""
Default Scenario Regression Tests
=================================

Guards the contract that ``service_desk`` is the shipped default scenario:

* With no explicit override, every surface (settings, config resolver, startup
  lifecycle, readiness reporting) resolves ``service_desk`` /
  ``ServiceDeskIntakeAgent``.
* An explicit override (``AGENT_SCENARIO`` env var or an explicit
  ``scenario_name`` argument) still wins.
* Readiness reporting agrees with the start agent that startup actually loaded.
* No checked-in deployment surface silently pins a different scenario.

Regression for: deployed PSTN calls answered with the banking Concierge because
the default fallbacks were hardcoded instead of derived from the scenario.
"""

from __future__ import annotations

import asyncio
import json
import os
import re
import shutil
import subprocess
import uuid
from pathlib import Path
from types import SimpleNamespace

import pytest

from apps.artagent.backend.config.appconfig_provider import APPCONFIG_KEY_MAP
from apps.artagent.backend.config.settings import (
    DEFAULT_AGENT_SCENARIO,
    DEFAULT_START_AGENT,
    get_agent_scenario,
)
from apps.artagent.backend.lifecycle.steps import register_agents_step
from apps.artagent.backend.registries.scenariostore import load_scenario
from apps.artagent.backend.voice.shared.config_resolver import (
    resolve_orchestrator_config,
)

REPO_ROOT = Path(__file__).resolve().parents[1]

SERVICE_DESK_SCENARIO = "service_desk"
SERVICE_DESK_START_AGENT = "ServiceDeskIntakeAgent"

ENV_SAMPLE = REPO_ROOT / ".env.sample"
APP_JSX = REPO_ROOT / "apps/artagent/frontend/src/components/App.jsx"
SERVICE_DESK_GUIDE = REPO_ROOT / "docs/guides/service-desk.md"
SYNC_APPCONFIG_SCRIPT = REPO_ROOT / "devops/scripts/azd/helpers/sync-appconfig.sh"


# ═══════════════════════════════════════════════════════════════════════════════
# HELPERS
# ═══════════════════════════════════════════════════════════════════════════════


class _CapturingManager:
    """Minimal LifecycleManager stand-in that captures registered steps."""

    def __init__(self) -> None:
        self.steps: dict[str, object] = {}

    def add_step(self, name, startup, shutdown=None, deferred=False):  # noqa: ANN001
        self.steps[name] = startup


def _run_agents_step() -> SimpleNamespace:
    """Run the ``agents`` startup step against a fake app and return app.state."""
    app = SimpleNamespace(state=SimpleNamespace())
    manager = _CapturingManager()
    register_agents_step(manager, app)
    asyncio.run(manager.steps["agents"]())
    return app.state


def _scratch_dir() -> Path:
    """Repo-local scratch directory (never the system temp dir)."""
    path = REPO_ROOT / "tests" / ".scratch" / uuid.uuid4().hex
    path.mkdir(parents=True, exist_ok=True)
    return path


def _to_posix_path(path: Path) -> str:
    """Convert a path to a form usable inside ``$PATH`` in bash (incl. Git Bash)."""
    text = path.resolve().as_posix()
    if re.match(r"^[A-Za-z]:/", text):  # D:/foo -> /d/foo (MSYS/Git Bash)
        return f"/{text[0].lower()}{text[2:]}"
    return text


def _extract_app_jsx_url_block() -> str:
    """Pull the live conversation-URL construction out of App.jsx."""
    source = APP_JSX.read_text(encoding="utf-8")
    start = source.index("const selectedScenarioKey =")
    end_marker = "${emailParam}${scenarioParam}`;"
    end = source.index(end_marker, start) + len(end_marker)
    return source[start:end]


def _read_normalized_text(path: Path) -> str:
    """Read text with whitespace collapsed for resilient documentation assertions."""
    text = path.read_text(encoding="utf-8")
    text = re.sub(r"(?m)^\s*#\s?", "", text)
    return re.sub(r"\s+", " ", text).strip()


def _build_conversation_url(
    *,
    active_scenario_key: str | None,
    active_scenario_name: str | None,
    stored_scenario: str | None,
) -> str:
    """Execute App.jsx's real URL-building code in node with controlled inputs."""
    node = shutil.which("node")
    if node is None:
        pytest.skip("node is not available")

    block = _extract_app_jsx_url_block()
    scenario_data = {"name": active_scenario_name} if active_scenario_name else None
    store = {"voice_agent_active_scenario": stored_scenario} if stored_scenario else {}
    harness = f"""
const WS_URL = "wss://example.test";
const currentSessionId = "sess-123";
const realtimeMode = "media";
const emailParam = "";
const activeScenarioKey = {json.dumps(active_scenario_key)};
const activeScenarioData = {json.dumps(scenario_data)};
const _store = {json.dumps(store)};
const sessionStorage = {{ getItem: (k) => (k in _store ? _store[k] : null) }};
{block}
process.stdout.write(baseConversationUrl);
"""
    result = subprocess.run(
        [node, "-e", harness],
        capture_output=True,
        text=True,
        check=False,
    )
    assert result.returncode == 0, result.stderr
    return result.stdout


def _find_bash() -> str | None:
    """Locate a POSIX bash that can see the repo (prefer Git Bash over WSL on Windows)."""
    candidates = [
        r"C:\Program Files\Git\bin\bash.exe",
        r"C:\Program Files\Git\usr\bin\bash.exe",
    ]
    for candidate in candidates:
        if Path(candidate).exists():
            return candidate
    return shutil.which("bash")


def _run_sync_appconfig(agent_scenario: str | None) -> list[str]:
    """Run sync-appconfig.sh with stubbed ``az``/``azd`` and return the az calls."""
    bash = _find_bash()
    if bash is None:
        pytest.skip("bash is not available")

    scratch = _scratch_dir()
    try:
        bin_dir = scratch / "bin"
        bin_dir.mkdir()
        log = scratch / "az-calls.log"

        (bin_dir / "az").write_text(
            '#!/usr/bin/env bash\nprintf "%s\\n" "$*" >> "$AZ_CALL_LOG"\nexit 0\n',
            encoding="utf-8",
            newline="\n",
        )
        # No azd env in a test run: every azd lookup must resolve to empty.
        (bin_dir / "azd").write_text(
            "#!/usr/bin/env bash\nexit 1\n", encoding="utf-8", newline="\n"
        )
        for stub in ("az", "azd"):
            os.chmod(bin_dir / stub, 0o755)

        env = {k: v for k, v in os.environ.items() if k != "AGENT_SCENARIO"}
        env["AZ_CALL_LOG"] = _to_posix_path(log)
        env["PATH"] = f"{_to_posix_path(bin_dir)}{os.pathsep}{env.get('PATH', '')}"
        if agent_scenario is not None:
            env["AGENT_SCENARIO"] = agent_scenario

        result = subprocess.run(
            [
                bash,
                SYNC_APPCONFIG_SCRIPT.relative_to(REPO_ROOT).as_posix(),
                "--endpoint",
                "https://unit-test.azconfig.io",
                "--label",
                "unit-test-label",
            ],
            capture_output=True,
            text=True,
            env=env,
            cwd=REPO_ROOT,
            check=False,
        )
        assert result.returncode == 0, f"{result.stdout}\n{result.stderr}"

        if not log.exists():
            return []
        return [line for line in log.read_text(encoding="utf-8").splitlines() if line]
    finally:
        shutil.rmtree(scratch, ignore_errors=True)
        try:
            scratch.parent.rmdir()
        except OSError:
            pass


# ═══════════════════════════════════════════════════════════════════════════════
# SOURCE OF TRUTH
# ═══════════════════════════════════════════════════════════════════════════════


def test_default_scenario_is_service_desk():
    assert DEFAULT_AGENT_SCENARIO == SERVICE_DESK_SCENARIO


def test_default_start_agent_matches_default_scenario_start_agent():
    """DEFAULT_START_AGENT must not drift away from the scenario YAML."""
    scenario = load_scenario(DEFAULT_AGENT_SCENARIO)
    assert scenario is not None, f"scenario '{DEFAULT_AGENT_SCENARIO}' must exist"
    assert scenario.start_agent == DEFAULT_START_AGENT == SERVICE_DESK_START_AGENT


# ═══════════════════════════════════════════════════════════════════════════════
# SCENARIO NAME RESOLUTION (override precedence)
# ═══════════════════════════════════════════════════════════════════════════════


@pytest.mark.parametrize("raw_value", [None, "", "   "])
def test_get_agent_scenario_without_override_uses_default(monkeypatch, raw_value):
    if raw_value is None:
        monkeypatch.delenv("AGENT_SCENARIO", raising=False)
    else:
        monkeypatch.setenv("AGENT_SCENARIO", raw_value)

    assert get_agent_scenario() == SERVICE_DESK_SCENARIO


def test_get_agent_scenario_honours_explicit_override(monkeypatch):
    monkeypatch.setenv("AGENT_SCENARIO", "banking")

    assert get_agent_scenario() == "banking"


# ═══════════════════════════════════════════════════════════════════════════════
# ORCHESTRATOR CONFIG RESOLUTION
# ═══════════════════════════════════════════════════════════════════════════════


def test_resolve_config_without_override_starts_service_desk(monkeypatch):
    monkeypatch.delenv("AGENT_SCENARIO", raising=False)

    config = resolve_orchestrator_config()

    assert config.scenario_name == SERVICE_DESK_SCENARIO
    assert config.start_agent == SERVICE_DESK_START_AGENT
    assert SERVICE_DESK_START_AGENT in config.agents
    assert "Concierge" not in config.agents
    assert "BankingConcierge" not in config.agents


def test_resolve_config_env_override_wins_over_default(monkeypatch):
    monkeypatch.setenv("AGENT_SCENARIO", "banking")

    config = resolve_orchestrator_config()

    assert config.scenario_name == "banking"
    assert config.start_agent == "BankingConcierge"


def test_resolve_config_explicit_scenario_argument_wins_over_env(monkeypatch):
    monkeypatch.setenv("AGENT_SCENARIO", SERVICE_DESK_SCENARIO)

    config = resolve_orchestrator_config(scenario_name="insurance")

    assert config.scenario_name == "insurance"
    assert config.start_agent != SERVICE_DESK_START_AGENT


def test_resolve_config_explicit_start_agent_wins(monkeypatch):
    monkeypatch.delenv("AGENT_SCENARIO", raising=False)

    config = resolve_orchestrator_config(start_agent="StandbyConfirmationAgent")

    assert config.start_agent == "StandbyConfirmationAgent"
    assert config.scenario_name == SERVICE_DESK_SCENARIO


# ═══════════════════════════════════════════════════════════════════════════════
# STARTUP LIFECYCLE
# ═══════════════════════════════════════════════════════════════════════════════


def test_agents_step_without_override_loads_service_desk(monkeypatch):
    monkeypatch.delenv("AGENT_SCENARIO", raising=False)

    state = _run_agents_step()

    assert state.scenario_name == SERVICE_DESK_SCENARIO
    assert state.scenario.name == SERVICE_DESK_SCENARIO
    assert state.start_agent == SERVICE_DESK_START_AGENT
    assert SERVICE_DESK_START_AGENT in state.unified_agents


def test_agents_step_env_override_wins(monkeypatch):
    monkeypatch.setenv("AGENT_SCENARIO", "banking")

    state = _run_agents_step()

    assert state.scenario_name == "banking"
    assert state.start_agent == "BankingConcierge"


def test_agents_step_start_agent_exists_in_loaded_registry(monkeypatch):
    """A start agent that is not loadable would strand every inbound call."""
    monkeypatch.delenv("AGENT_SCENARIO", raising=False)

    state = _run_agents_step()

    assert state.start_agent in state.unified_agents


# ═══════════════════════════════════════════════════════════════════════════════
# READINESS REPORTING AGREES WITH RUNTIME
# ═══════════════════════════════════════════════════════════════════════════════


def test_readiness_reports_the_loaded_start_agent(monkeypatch):
    from apps.artagent.backend.api.v1.endpoints.health import _check_rt_agents_fast

    monkeypatch.delenv("AGENT_SCENARIO", raising=False)
    state = _run_agents_step()

    check = asyncio.run(_check_rt_agents_fast(state))

    assert check.status == "healthy"
    assert f"start_agent={state.start_agent}" in check.details
    assert f"start_agent={SERVICE_DESK_START_AGENT}" in check.details
    assert f"scenario={SERVICE_DESK_SCENARIO}" in check.details


def test_readiness_reports_override_start_agent(monkeypatch):
    from apps.artagent.backend.api.v1.endpoints.health import _check_rt_agents_fast

    monkeypatch.setenv("AGENT_SCENARIO", "banking")
    state = _run_agents_step()

    check = asyncio.run(_check_rt_agents_fast(state))

    assert "start_agent=BankingConcierge" in check.details
    assert "scenario=banking" in check.details


# ═══════════════════════════════════════════════════════════════════════════════
# DEPLOYMENT / CONFIGURATION SURFACES
# ═══════════════════════════════════════════════════════════════════════════════


def test_appconfig_exposes_scenario_override_key():
    """Operators need a supported App Configuration override surface."""
    assert APPCONFIG_KEY_MAP.get("app/agent/scenario") == "AGENT_SCENARIO"


def test_appconfig_sync_does_not_hardcode_a_scenario():
    """postprovision must not pin a scenario when the operator chose none."""
    script = SYNC_APPCONFIG_SCRIPT.read_text(encoding="utf-8")

    setters = re.findall(r'set_kv\s+"app/agent/scenario"\s+"([^"]*)"', script)

    assert setters, "sync-appconfig.sh must be able to sync app/agent/scenario"
    for value in setters:
        assert value.startswith("$"), (
            "app/agent/scenario must be synced from an operator-provided variable, "
            f"not the literal {value!r}"
        )


def test_appconfig_sync_clears_the_key_when_no_override_is_set():
    """The unset branch must delete a stale key, not silently leave it behind."""
    script = SYNC_APPCONFIG_SCRIPT.read_text(encoding="utf-8")

    assert (
        'delete_kv "app/agent/scenario"' in script
    ), "sync-appconfig.sh must delete app/agent/scenario when AGENT_SCENARIO is unset"

    # The delete must live in the `else` (no override) branch of the scenario block.
    block = re.search(
        r'if \[\[ -n "\$agent_scenario" \]\]; then(?P<set>.*?)else(?P<unset>.*?)\nfi\n',
        script,
        re.DOTALL,
    )
    assert block, "expected an if/else block guarded by $agent_scenario"
    assert 'set_kv "app/agent/scenario"' in block.group("set")
    assert 'delete_kv "app/agent/scenario"' in block.group("unset")
    assert 'set_kv "app/agent/scenario"' not in block.group("unset")


def test_appconfig_sync_delete_helper_tolerates_missing_key_but_reports_failures():
    """A missing key is the desired end state; anything else must be surfaced."""
    script = SYNC_APPCONFIG_SCRIPT.read_text(encoding="utf-8")

    helper = re.search(r"^delete_kv\(\) \{.*?^\}", script, re.DOTALL | re.MULTILINE)
    assert helper, "sync-appconfig.sh must define a delete_kv helper"
    body = helper.group(0)

    assert "az appconfig kv delete" in body
    assert '--endpoint "$ENDPOINT"' in body
    assert "--auth-mode login" in body
    assert 'cmd_args+=(--label "$LABEL")' in body
    assert "not found" in body, "key-not-found must be tolerated"
    assert "return 1" in body, "real failures must be surfaced to the caller"
    assert 'if [[ "$DRY_RUN" == "true" ]]' in body


@pytest.mark.parametrize(
    ("agent_scenario", "expect_set", "expect_delete"),
    [
        (None, False, True),
        ("banking", True, False),
    ],
)
def test_appconfig_sync_scenario_key_behaviour(agent_scenario, expect_set, expect_delete):
    """End-to-end: run the real script against a stubbed `az`/`azd` and inspect calls."""
    calls = _run_sync_appconfig(agent_scenario)

    scenario_sets = [
        c for c in calls if c.startswith("appconfig kv set") and "app/agent/scenario" in c
    ]
    scenario_deletes = [
        c for c in calls if c.startswith("appconfig kv delete") and "app/agent/scenario" in c
    ]

    assert bool(scenario_sets) is expect_set, calls
    assert bool(scenario_deletes) is expect_delete, calls

    if expect_set:
        assert f"--value {agent_scenario}" in scenario_sets[0]
    if expect_delete:
        assert "--label unit-test-label" in scenario_deletes[0]
        assert "--auth-mode login" in scenario_deletes[0]

    # Whatever happens, no scenario name may be invented by the deployment script.
    for call in calls:
        if "app/agent/scenario" in call and call.startswith("appconfig kv set"):
            assert f"--value {agent_scenario}" in call


@pytest.mark.parametrize(
    "relative_path",
    [
        "azure.yaml",
        "infra/terraform/containers.tf",
        "infra/terraform/appconfig.tf",
        "infra/terraform/main.tfvars.json",
        "apps/artagent/backend/Dockerfile",
        "devops/scripts/azd/postprovision.sh",
    ],
)
def test_deployment_manifests_do_not_pin_a_scenario(relative_path):
    """No checked-in manifest may silently force a non-default start agent."""
    path = REPO_ROOT / relative_path
    if not path.exists():
        pytest.skip(f"{relative_path} not present")

    content = path.read_text(encoding="utf-8")

    assert "AGENT_SCENARIO" not in content
    assert "Concierge" not in content


def test_browser_client_does_not_hardcode_a_scenario():
    """The real browser WebSocket path must omit `scenario` instead of pinning one."""
    block = _extract_app_jsx_url_block()

    assert (
        "'banking'" not in block
    ), "the conversation WebSocket URL must not fall back to the banking scenario"
    assert (
        "${emailParam}${scenarioParam}" in block
    ), "the conversation WebSocket URL must append `scenario` conditionally"
    assert (
        "sessionStorage.getItem('voice_agent_active_scenario')" in block
    ), "a session-scoped scenario selection must still be honoured"


@pytest.mark.parametrize(
    ("active_scenario_key", "active_scenario_name", "stored_scenario", "expected"),
    [
        # Fresh browser session, nothing selected -> no scenario at all.
        (None, None, None, None),
        # Session-scoped selection (sessionStorage mirror) still wins.
        (None, None, "service_desk", "service%20desk"),
        # Explicit selection from the backend session config wins.
        ("banking", "Banking", None, "Banking"),
        # Explicit selection without a display name falls back to the key.
        ("insurance", None, None, "insurance"),
        # An explicit selection beats a stale sessionStorage value.
        ("insurance", "Insurance", "banking", "Insurance"),
    ],
)
def test_browser_conversation_url_scenario_parameter(
    active_scenario_key, active_scenario_name, stored_scenario, expected
):
    """Behavioural check of the actual App.jsx URL-building code."""
    url = _build_conversation_url(
        active_scenario_key=active_scenario_key,
        active_scenario_name=active_scenario_name,
        stored_scenario=stored_scenario,
    )

    if expected is None:
        assert "scenario=" not in url, url
    else:
        assert f"&scenario={expected}" in url, url

    # The rest of the query string must be unaffected.
    assert "session_id=sess-123" in url
    assert "streaming_mode=media" in url


def test_env_sample_documents_azd_as_the_durable_override_surface():
    content = _read_normalized_text(ENV_SAMPLE)

    assert "durable source of truth" in content
    assert "`azd env set AGENT_SCENARIO banking`" in content
    assert '`azd env set AGENT_SCENARIO ""`' in content
    assert "advanced" in content and "temporary/manual override" in content
    assert "reconciled on the next azd sync" in content
    assert "or directly via `az appconfig kv set`" not in content


def test_service_desk_guide_documents_durable_vs_temporary_deployed_overrides():
    content = _read_normalized_text(SERVICE_DESK_GUIDE)

    assert "`azd env set AGENT_SCENARIO banking`" in content
    assert "`azd env --help` exposes `azd env set` but no `azd env unset`" in content
    assert '`azd env set AGENT_SCENARIO ""`' in content
    assert "advanced temporary/manual override" in content
    assert "This is not co-equal durable configuration" in content
    assert "reconciles the" in content and "azd/ambient `AGENT_SCENARIO` value" in content
    assert "or set the key directly" not in content
