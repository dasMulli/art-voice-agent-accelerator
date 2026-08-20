# Scenario Orchestration Analysis: Active Agent Not Set Correctly

**Date:** December 11, 2025  
**Status:** Draft - Requires Team Review  
**Issue:** Banking scenario starts with PolicyAdvisor instead of BankingConcierge

---

## Executive Summary

When the `AGENT_SCENARIO=banking` environment variable is set, calls are expected to start with `BankingConcierge` as defined in `orchestration.yaml`. However, the conversation incorrectly starts with a different agent (e.g., `PolicyAdvisor`). This analysis traces the complete flow from scenario configuration to orchestrator initialization to identify root causes and propose fixes.

---

## 🔴 Issue 1: Banking Scenario Uses `orchestration.yaml` Instead of `scenario.yaml`

### Evidence

**Scenario loader only looks for `scenario.yaml`:**

```python
# scenariostore/loader.py:235
def _load_scenario_file(scenario_dir: Path) -> ScenarioConfig | None:
    """Load a scenario from its directory."""
    config_path = scenario_dir / "scenario.yaml"  # ❌ HARDCODED
    if not config_path.exists():
        return None  # Banking returns None!
```

**Banking directory structure:**
```
scenariostore/
├── banking/
│   ├── __init__.py
│   └── orchestration.yaml   # ❌ File exists but never loaded!
├── default/
│   └── scenario.yaml        # ✅ Correctly named
├── insurance/
│   └── scenario.yaml        # ✅ Correctly named
```

### Impact

- `load_scenario("banking")` returns `None`
- `get_scenario_start_agent("banking")` returns `None`
- System falls back to `"Concierge"` which may not exist in the agent registry
- First available agent is selected instead (potentially `PolicyAdvisor`)

### Proposed Fix

**Option A:** Rename `orchestration.yaml` → `scenario.yaml`

```bash
cd apps/artagent/backend/registries/scenariostore/banking
mv orchestration.yaml scenario.yaml
```

**Option B:** Update loader to check for both filenames

```python
def _load_scenario_file(scenario_dir: Path) -> ScenarioConfig | None:
    """Load a scenario from its directory."""
    # Check for both naming conventions
    for filename in ["scenario.yaml", "orchestration.yaml"]:
        config_path = scenario_dir / filename
        if config_path.exists():
            # ... load and return
```

---

## 🔴 Issue 2: Fallback Logic Uses Non-Existent Agent Name

### Evidence

Multiple places fall back to `"Concierge"` when start agent is not resolved:

```python
# main.py:780
start_agent = get_scenario_start_agent(scenario_name) or "Concierge"

# config_resolver.py:57
DEFAULT_START_AGENT = "Concierge"

# voicelive/handler.py (multiple places)
effective_start_agent = DEFAULT_START_AGENT  # "Concierge"

# speech_cascade/orchestrator.py:222
start_agent: str = DEFAULT_START_AGENT  # "Concierge"
```

**But the actual agent registry contains:**
- `AuthAgent`
- `BankingConcierge` (not "Concierge")
- `CardRecommendation`
- `ClaimsSpecialist`
- `ComplianceDesk`
- `CustomAgent`
- `FraudAgent`
- `InvestmentAdvisor`
- `PolicyAdvisor`

### Impact

When `"Concierge"` is not found:
1. `CascadeOrchestratorAdapter.__post_init__` warns and falls back to first agent:

```python
if self._active_agent and self._active_agent not in self.agents:
    available = list(self.agents.keys())
    if available:
        logger.warning(...)
        self._active_agent = available[0]  # ← First alphabetically!
```

2. `list(agents.keys())` order depends on dictionary insertion order → agent discovery order → filesystem order

3. Since `PolicyAdvisor` might come before other agents alphabetically or in discovery order, it becomes the default.

### Proposed Fix

Change default start agent to match actual registry:

```python
# config_resolver.py
DEFAULT_START_AGENT = "BankingConcierge"  # Or another valid default
```

Or better, validate at startup and fail fast if misconfigured.

---

## 🔴 Issue 3: Multiple Competing Start Agent Resolution Paths

### Evidence

The start agent is resolved in at least 4 different places with different logic:

| Location | Resolution Logic | Fallback |
|----------|-----------------|----------|
| `main.py:start_agents()` | `get_scenario_start_agent()` | `"Concierge"` |
| `media_handler.py:create()` | `resolve_orchestrator_config().start_agent` | `app_state.start_agent` → `"Concierge"` |
| `voicelive/handler.py:start()` | `orchestrator_config.start_agent` | `settings.start_agent` → `DEFAULT_START_AGENT` |
| `CascadeOrchestratorAdapter.__post_init__` | `config.start_agent` | First available agent |
| `LiveOrchestrator.__init__` | `start_agent` param | Raises ValueError if not found |

### Flow Diagram

```
┌─────────────────────────────────────────────────────────────────────────────┐
│                          APPLICATION STARTUP                                 │
│  main.py:start_agents()                                                      │
│  ┌─────────────────────────────────────────────────────────────────────────┐│
│  │ scenario_name = os.getenv("AGENT_SCENARIO")                              ││
│  │ if scenario_name:                                                        ││
│  │   scenario = load_scenario(scenario_name)  ← Returns None for banking!  ││
│  │   start_agent = get_scenario_start_agent() or "Concierge"               ││
│  │   app.state.start_agent = start_agent                                    ││
│  └─────────────────────────────────────────────────────────────────────────┘│
└─────────────────────────────────────────────────────────────────────────────┘
                                    │
                                    ▼
┌─────────────────────────────────────────────────────────────────────────────┐
│                          CALL INITIATED                                      │
│  media_handler.py:MediaHandler.create()                                      │
│  ┌─────────────────────────────────────────────────────────────────────────┐│
│  │ if config.scenario:                                                      ││
│  │   scenario_cfg = resolve_orchestrator_config(scenario_name)              ││
│  │   scenario_start_agent = scenario_cfg.start_agent  ← Returns "Concierge"││
│  │                                                                          ││
│  │ if session_agent:                                                        ││
│  │   start_agent = session_agent                                            ││
│  │ elif scenario_start_agent:                                               ││
│  │   start_agent_name = scenario_start_agent                                ││
│  │ else:                                                                    ││
│  │   start_agent_name = app_state.start_agent or "Concierge"               ││
│  │                                                                          ││
│  │ memory_manager.update_corememory("active_agent", start_agent_name)       ││
│  └─────────────────────────────────────────────────────────────────────────┘│
└─────────────────────────────────────────────────────────────────────────────┘
                                    │
                                    ▼
┌─────────────────────────────────────────────────────────────────────────────┐
│                          ORCHESTRATOR INIT                                   │
│  voicelive/orchestrator.py:LiveOrchestrator.__init__                        │
│  OR speech_cascade/orchestrator.py:CascadeOrchestratorAdapter               │
│  ┌─────────────────────────────────────────────────────────────────────────┐│
│  │ self.active = start_agent  # "Concierge"                                 ││
│  │                                                                          ││
│  │ if self.active not in self.agents:                                       ││
│  │   # Fallback to first available                                          ││
│  │   self._active_agent = available[0]  ← PolicyAdvisor?                   ││
│  └─────────────────────────────────────────────────────────────────────────┘│
└─────────────────────────────────────────────────────────────────────────────┘
                                    │
                                    ▼
┌─────────────────────────────────────────────────────────────────────────────┐
│                          ORCHESTRATOR START                                  │
│  orchestrator.start()                                                        │
│  ┌─────────────────────────────────────────────────────────────────────────┐│
│  │ _sync_from_memo_manager()                                                ││
│  │   state = sync_state_from_memo(memo, available_agents)                   ││
│  │   if state.active_agent:                                                 ││
│  │     self.active = state.active_agent  ← "Concierge" stored earlier!     ││
│  │                                                                          ││
│  │ await self._switch_to(self.active, system_vars)                          ││
│  └─────────────────────────────────────────────────────────────────────────┘│
└─────────────────────────────────────────────────────────────────────────────┘
```

### Impact

1. `"Concierge"` is stored in MemoManager
2. Orchestrator reads `"Concierge"` from MemoManager  
3. Validation fails (not in agents)
4. Falls back to first available agent

---

## 🔴 Issue 4: handoff_map Not Built from Scenario

### Evidence

The banking scenario defines handoffs in `orchestration.yaml`:

```yaml
handoffs:
  - from: BankingConcierge
    to: CardRecommendation
    tool: handoff_card_recommendation
```

But this is never loaded because the file isn't read (Issue 1).

Instead, `handoff_map` is built from agent declarations:

```python
# loader.py:build_handoff_map()
def build_handoff_map(agents: dict[str, UnifiedAgent]) -> dict[str, str]:
    handoff_map: dict[str, str] = {}
    for agent in agents.values():
        if agent.handoff.trigger:
            handoff_map[agent.handoff.trigger] = agent.name
    return handoff_map
```

This approach:
- ✅ Works for global handoffs (any agent can call `handoff_concierge` → BankingConcierge)
- ❌ Loses scenario-specific routing context (which agents can call which)
- ❌ Loses handoff type information (discrete vs announced)

### Proposed Fix

Ensure scenario handoff configuration is loaded and merged with agent-level handoffs.

---

## 🟡 Issue 5: Discovery Order Affects Fallback Agent Selection

### Evidence

```python
# loader.py
def discover_agents(agents_dir: Path = AGENTS_DIR) -> dict[str, UnifiedAgent]:
    agents: dict[str, UnifiedAgent] = {}
    for item in agents_dir.iterdir():  # Filesystem order!
        ...
```

```python
# orchestrator.py
available = list(self.agents.keys())
self._active_agent = available[0]  # First in dictionary order
```

### Impact

The fallback agent depends on filesystem enumeration order, which varies by:
- Operating system
- Filesystem type
- Docker image build process

---

## 📊 Summary of Root Causes

| # | Issue | Severity | Fix Complexity |
|---|-------|----------|----------------|
| 1 | `orchestration.yaml` not loaded | **Critical** | Low - Rename file |
| 2 | `"Concierge"` fallback doesn't exist | **Critical** | Low - Change default |
| 3 | Multiple resolution paths | Medium | Medium - Consolidate |
| 4 | Scenario handoffs not applied | Medium | Medium - Fix loader |
| 5 | Non-deterministic fallback | Low | Low - Sort agents |

---

## ✅ Recommended Action Plan

### Phase 1: Immediate Fixes (Critical)

1. **Rename banking scenario file:**
   ```bash
   mv apps/artagent/backend/registries/scenariostore/banking/orchestration.yaml \
      apps/artagent/backend/registries/scenariostore/banking/scenario.yaml
   ```

2. **Update DEFAULT_START_AGENT:**
   ```python
   # config_resolver.py
   DEFAULT_START_AGENT = "BankingConcierge"
   ```

3. **Add startup validation:**
   ```python
   # main.py:start_agents()
   if app.state.start_agent not in unified_agents:
       logger.error(
           "Start agent '%s' not found in registry! Available: %s",
           app.state.start_agent,
           list(unified_agents.keys()),
       )
       raise ValueError(f"Invalid start_agent: {app.state.start_agent}")
   ```

### Phase 2: Consolidation (Recommended)

4. **Single source of truth for start agent:**
   - Remove redundant resolution in `media_handler.py`
   - Use `app.state.start_agent` set at startup
   - Pass through to orchestrators explicitly

5. **Add loader support for `orchestration.yaml`:**
   ```python
   for filename in ["scenario.yaml", "orchestration.yaml"]:
       config_path = scenario_dir / filename
       if config_path.exists():
           break
   ```

### Phase 3: Testing

6. **Add integration tests:**
   - Test `AGENT_SCENARIO=banking` starts with `BankingConcierge`
   - Test `AGENT_SCENARIO=insurance` starts with `AuthAgent`
   - Test fallback behavior when scenario is invalid

---

## 📎 Files to Modify

| File | Change |
|------|--------|
| `scenariostore/banking/orchestration.yaml` | Rename to `scenario.yaml` |
| `voice/shared/config_resolver.py` | Update `DEFAULT_START_AGENT` |
| `scenariostore/loader.py` | Support both filename patterns |
| `main.py` | Add startup validation |
| `media_handler.py` | Simplify start agent resolution |

---

## 🧪 Verification Steps

After applying fixes:

1. Set `AGENT_SCENARIO=banking`
2. Start the application
3. Verify logs show:
   ```
   Loaded scenario: banking
   start_agent=BankingConcierge
   ```
4. Initiate a call
5. Verify first agent response uses BankingConcierge greeting

---

## 📝 Notes for Discussion

- Should we deprecate `orchestration.yaml` in favor of `scenario.yaml` for consistency?
- Should we fail-fast or fall back gracefully when start agent is invalid?
- Consider adding a `--validate-config` CLI flag for CI/CD pipelines

---

*End of Analysis*
