# Evaluation Framework Refactor Specification

**Status**: Draft  
**Author**: AI Agent  
**Date**: 2026-01-24

---

## 1. Problem Summary

The evaluation framework has evolved organically, resulting in:

| Issue | Impact | Severity |
|-------|--------|----------|
| 3 competing config paradigms (legacy/template/session) | Cognitive overhead, complex branching in `scenario_runner.py:581-647` | High |
| Repetitive per-agent model overrides | Verbose YAML (30-40 lines → should be 5-10) | High |
| No extension points for custom analysis | Can't add metrics without modifying core | Medium |
| Verbose expectation syntax | Simple assertions require full object structures | Medium |
| Monolithic `schemas.py` (659 lines, 14+ models) | Hard to navigate, maintain, and test | Low |

---

## 2. Design Goals

1. **70% reduction** in YAML configuration verbosity
2. **Single canonical format** for scenario definition
3. **Backward compatible** — existing YAMLs continue to work
4. **Extensible** — hooks for custom metrics/analysis without core changes
5. **Modular schemas** — clear separation of concerns

---

## 3. Proposed Architecture

### 3.1 Model Profiles (Phase 1 - High Value)

**Before** (30+ lines repeated per variant):
```yaml
agent_overrides:
  - agent: BankingConcierge
    model_override: { deployment_id: gpt-4o, temperature: 0.6, max_tokens: 200 }
  - agent: CardRecommendation  
    model_override: { deployment_id: gpt-4o, temperature: 0.6, max_tokens: 200 }
  - agent: InvestmentAdvisor
    model_override: { deployment_id: gpt-4o, temperature: 0.6, max_tokens: 200 }
```

**After** (5 lines):
```yaml
model_profiles:
  gpt4o_fast:
    deployment_id: gpt-4o
    endpoint_preference: chat
    temperature: 0.6
    max_tokens: 200

  o3_reasoning:
    deployment_id: o3-mini
    endpoint_preference: responses
    reasoning_effort: medium
    max_completion_tokens: 2000

variants:
  - variant_id: baseline
    model_profile: gpt4o_fast  # All agents use this

  - variant_id: reasoning
    model_profile: o3_reasoning
    agent_overrides:  # Only specify exceptions
      - agent: CardRecommendation
        reasoning_effort: low
```

**Implementation**:
```python
# schemas/profiles.py
class ModelProfile(BaseModel):
    """Reusable model configuration template."""
    deployment_id: str
    endpoint_preference: Literal["chat", "responses"] = "chat"
    temperature: float | None = None
    max_tokens: int | None = None
    max_completion_tokens: int | None = None
    reasoning_effort: Literal["low", "medium", "high"] | None = None
    # ... other fields

class VariantConfig(BaseModel):
    """Variant with profile inheritance."""
    variant_id: str
    model_profile: str | None = None  # References model_profiles key
    agent_overrides: list[AgentOverride] = []  # Per-agent exceptions only
```

**Resolution Logic** (in `scenario_runner.py`):
```python
def _resolve_agent_overrides(
    self,
    variant: VariantConfig,
    profiles: dict[str, ModelProfile],
    agent_names: list[str],
) -> list[dict[str, Any]]:
    """Resolve profile + overrides into per-agent configs."""
    base_config = {}
    if variant.model_profile and variant.model_profile in profiles:
        base_config = profiles[variant.model_profile].model_dump(exclude_none=True)
    
    overrides = []
    override_map = {o.agent: o for o in variant.agent_overrides}
    
    for agent in agent_names:
        agent_config = {**base_config}  # Start with profile
        if agent in override_map:
            # Merge agent-specific overrides
            agent_config.update(override_map[agent].model_override or {})
        overrides.append({"agent": agent, "model_override": agent_config})
    
    return overrides
```

---

### 3.2 Compact Expectation Syntax (Phase 1 - Medium Value)

**Before** (verbose):
```yaml
expectations:
  tools_called:
    - verify_client_identity
  handoff:
    to_agent: CardRecommendation
  response_constraints:
    must_include:
      - "balance"
    must_not_include:
      - "error"
  max_latency_ms: 5000
```

**After** (compact, with backward compatibility):
```yaml
# Compact form (new)
expect:
  tools: [verify_client_identity]
  handoff: CardRecommendation
  contains: ["balance"]
  excludes: ["error"]
  max_latency: 5000

# Or ultra-compact for simple cases
expect: [verify_client_identity]  # Just tools
```

**Normalization Logic** (in `validator.py`):
```python
def _normalize_expectations(raw: dict | list) -> ScenarioExpectations:
    """Convert compact syntax to full ScenarioExpectations."""
    if isinstance(raw, list):
        return ScenarioExpectations(tools_called=raw)
    
    if "expect" in raw:
        exp = raw["expect"]
        if isinstance(exp, list):
            return ScenarioExpectations(tools_called=exp)
        return ScenarioExpectations(
            tools_called=exp.get("tools", []),
            handoff={"to_agent": exp["handoff"]} if "handoff" in exp else None,
            no_handoff=exp.get("no_tools", False),
            response_constraints={
                "must_include": exp.get("contains", []),
                "must_not_include": exp.get("excludes", []),
            },
            max_latency_ms=exp.get("max_latency"),
        )
    
    # Pass through full format unchanged
    return ScenarioExpectations.model_validate(raw.get("expectations", raw))
```

---

### 3.3 Configuration Unification (Phase 2)

**Problem**: Three paradigms create branching complexity.

**Solution**: Standardize on `session_config` as the canonical format, with automatic conversion.

```yaml
# Canonical format (unified)
scenario_name: banking_test
description: Test banking flow

session_config:
  agents: all  # or ["Agent1", "Agent2"]
  start_agent: BankingConcierge
  handoffs: inherit  # NEW: Use agentstore definitions

model_profiles: { ... }
variants: [ ... ]
turns: [ ... ]
```

**Backward Compatibility Shim**:
```python
def _normalize_scenario(self, scenario: dict) -> dict:
    """Convert legacy/template format to session_config format."""
    if "session_config" in scenario:
        return scenario  # Already normalized
    
    # Convert legacy single-agent format
    if "agent" in scenario and "model_override" in scenario:
        return {
            **scenario,
            "session_config": {
                "agents": [scenario["agent"]],
                "start_agent": scenario["agent"],
            },
            "agent_overrides": [{
                "agent": scenario["agent"],
                "model_override": scenario["model_override"],
            }],
        }
    
    # Convert template reference format
    if "scenario_template" in scenario:
        return {
            **scenario,
            "session_config": {
                "agents": "all",
                "start_agent": scenario.get("agent", "unknown"),
                "template": scenario["scenario_template"],
            },
        }
    
    return scenario
```

---

### 3.4 Hook System (Phase 2 - Extensibility)

**Purpose**: Allow custom turn analysis without modifying core runner.

```yaml
# In scenario YAML
hooks:
  on_turn_complete:
    - builtin.log_metrics
    - module: my_analyzers.sentiment
      function: analyze_response

  on_tool_complete:
    - builtin.validate_result

  pre_score:
    - module: my_analyzers.domain_metrics
      function: compute_banking_accuracy
```

**Hook Interface**:
```python
# tests/evaluation/hooks/base.py
from abc import ABC, abstractmethod
from typing import Any
from tests.evaluation.schemas import TurnEvent

class TurnHook(ABC):
    @abstractmethod
    async def on_turn_complete(
        self,
        turn: TurnEvent,
        context: dict[str, Any],
    ) -> dict[str, Any]:
        """Called after each turn. Returns additional metadata."""
        pass

class ToolHook(ABC):
    @abstractmethod
    async def on_tool_complete(
        self,
        tool_name: str,
        tool_result: Any,
        turn_context: dict[str, Any],
    ) -> dict[str, Any]:
        pass
```

**Built-in Hooks**:
- `builtin.log_metrics` — Console output of turn metrics
- `builtin.validate_expectations` — Run expectation checks after each turn
- `builtin.capture_reasoning` — Extract reasoning tokens for o-series models

**Hook Dispatch** (in scenario_runner.py):
```python
async def _dispatch_hooks(
    self,
    hook_type: str,
    payload: dict[str, Any],
) -> dict[str, Any]:
    """Dispatch to registered hooks, merge returned metadata."""
    hooks = self._hook_registry.get(hook_type, [])
    merged = {}
    for hook in hooks:
        result = await hook(payload)
        if result:
            merged.update(result)
    return merged
```

---

### 3.5 Schema Modularization (Phase 3)

**Current**: 659-line `schemas.py` with 14+ models.

**Proposed Structure**:
```
tests/evaluation/schemas/
├── __init__.py          # Public exports only
├── events.py            # TurnEvent, ToolCall, EvidenceBlob, HandoffEvent
├── config.py            # SessionAgentConfig, ModelProfile, VariantConfig  
├── expectations.py      # ScenarioExpectations, validation helpers
├── results.py           # TurnScore, PerTurnSummary, RunSummary
├── foundry.py           # FoundryEvaluatorConfig, FoundryDataRow, etc.
└── profiles.py          # ModelProfile, ProfileResolver
```

**Public API** (`__init__.py`):
```python
# Core events
from .events import TurnEvent, ToolCall, HandoffEvent, EvidenceBlob

# Configuration
from .config import SessionAgentConfig, SessionHandoffConfig
from .profiles import ModelProfile, VariantConfig

# Expectations
from .expectations import ScenarioExpectations

# Results
from .results import TurnScore, RunSummary, PerTurnSummary

# Foundry integration
from .foundry import FoundryExportConfig, FoundryDataRow

__all__ = [
    "TurnEvent", "ToolCall", "HandoffEvent", "EvidenceBlob",
    "SessionAgentConfig", "SessionHandoffConfig",
    "ModelProfile", "VariantConfig",
    "ScenarioExpectations",
    "TurnScore", "RunSummary", "PerTurnSummary",
    "FoundryExportConfig", "FoundryDataRow",
]
```

---

## 4. Implementation Phases

### Phase 1: Model Profiles + Compact Expectations (Week 1)

| Task | Files | Effort | Risk |
|------|-------|--------|------|
| Add `ModelProfile` schema | `schemas.py` | 50 lines | Low |
| Add profile resolution logic | `scenario_runner.py` | 80 lines | Low |
| Add compact expectation normalization | `validator.py` | 60 lines | Low |
| Update comparison YAML examples | `scenarios/` | Config only | Low |
| Add tests for profile resolution | `tests/` | 100 lines | Low |

**Deliverable**: 70% reduction in YAML for comparison scenarios.

### Phase 2: Hooks + Config Unification (Week 2)

| Task | Files | Effort | Risk |
|------|-------|--------|------|
| Create `hooks/` module with base classes | New module | 150 lines | Medium |
| Add hook dispatch to turn loop | `scenario_runner.py` | 80 lines | Medium |
| Implement built-in hooks | `hooks/builtin.py` | 100 lines | Low |
| Add scenario normalization shim | `scenario_runner.py` | 60 lines | Low |
| Deprecation warnings for legacy format | `scenario_runner.py` | 20 lines | Low |

**Deliverable**: Extensible analysis without core changes.

### Phase 3: Schema Modularization (Week 3)

| Task | Files | Effort | Risk |
|------|-------|--------|------|
| Split `schemas.py` into modules | New `schemas/` dir | 0 new code | Medium |
| Update all imports | All evaluation files | Find/replace | Low |
| Add schema versioning header | `schemas/__init__.py` | 10 lines | Low |

**Deliverable**: Maintainable, navigable schema structure.

---

## 5. Migration Guide

### For Existing Scenarios

**No action required** — all existing YAMLs continue to work via normalization shims.

### To Adopt Model Profiles

```yaml
# Before (existing)
variants:
  - variant_id: gpt4o
    agent_overrides:
      - agent: BankingConcierge
        model_override: { deployment_id: gpt-4o, temperature: 0.6 }

# After (with profiles)
model_profiles:
  gpt4o_standard:
    deployment_id: gpt-4o
    temperature: 0.6

variants:
  - variant_id: gpt4o
    model_profile: gpt4o_standard
```

### To Adopt Compact Expectations

```yaml
# Before
expectations:
  tools_called: [verify_client_identity]
  
# After (either works)
expect: [verify_client_identity]
# or
expect:
  tools: [verify_client_identity]
```

---

## 6. Metrics: Before vs After

| Metric | Current | Phase 1 | Phase 3 |
|--------|---------|---------|---------|
| Lines per variant (5 agents) | 30-40 | 5-10 | 5-10 |
| Configuration paradigms | 3 | 3 | 1 |
| Schema files | 1 (659 lines) | 1 | 6 (~110 each) |
| Extension points | 0 | 3 hooks | 3 hooks |
| Expectation syntax options | 1 (verbose) | 2 | 2 |

---

## 7. Files to Modify

| File | Phase | Change Type |
|------|-------|-------------|
| `schemas.py` | 1, 3 | Add `ModelProfile`, then split |
| `scenario_runner.py` | 1, 2 | Profile resolution, normalization, hooks |
| `validator.py` | 1 | Compact expectation normalization |
| `scenarios/*.yaml` | 1 | Migrate to profiles (optional) |
| New: `hooks/` module | 2 | Hook system |
| New: `schemas/` directory | 3 | Schema split |

---

## 8. Testing Strategy

### Unit Tests (per phase)

```python
# Phase 1: Profile resolution
def test_profile_applies_to_all_agents():
    """Profile config applies to all agents without explicit overrides."""
    
def test_agent_override_merges_with_profile():
    """Agent-specific override merges with (not replaces) profile."""

def test_compact_expect_tools_only():
    """expect: [tool1, tool2] normalizes to tools_called."""

# Phase 2: Hooks
def test_hook_dispatch_order():
    """Hooks execute in registration order."""

def test_builtin_log_metrics_hook():
    """Built-in logging hook produces expected output."""

# Phase 3: Schema imports
def test_public_api_stable():
    """All public exports accessible from schemas/__init__.py."""
```

### Integration Tests

- Run existing `fraud_detection_comparison.yaml` with profiles
- Run `banking_multi_agent.yaml` unchanged (backward compat)
- Run scenario with custom hook registered

---

## 9. Open Questions

1. **Profile Inheritance**: Should profiles support `extends: base_profile`?
   - **Recommendation**: No, keep flat for simplicity. Multiple profiles can share a common base via copy.

2. **Hook Failure Mode**: Fail-fast or continue-and-log?
   - **Recommendation**: Continue-and-log with warning, don't block scenario execution.

3. **Schema Versioning**: Add `schema_version` field to scenario YAML?
   - **Recommendation**: Yes, for future-proofing. Default to `1` if missing.

---

## 10. Acceptance Criteria

- [ ] Comparison scenarios use 70% fewer YAML lines with profiles
- [ ] Existing scenarios run without modification
- [ ] Custom hook can be registered and executed via YAML config
- [ ] Schema imports work from both old (`schemas.py`) and new (`schemas/`) paths
- [ ] All existing tests pass
- [ ] New tests cover profile resolution and expectation normalization
