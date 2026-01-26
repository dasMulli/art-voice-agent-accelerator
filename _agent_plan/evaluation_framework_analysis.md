# Evaluation Framework Analysis

## Executive Summary

After thorough analysis of `tests/evaluation/`, the framework is **well-architected** with clean separation of concerns. However, your instinct about growing complexity is valid - the current design has accumulated **conceptual duplication** and **configuration sprawl** that will become problematic as scenarios scale.

**Key Finding**: The duplication isn't in the Python code (which is well-factored), but in the **YAML scenario definitions** and the **three different scenario configuration paradigms** that have evolved organically.

---

## Current Architecture Overview

```
tests/evaluation/
├── Core Engine (~2,500 lines)
│   ├── schemas.py (659 lines)      # 14+ Pydantic models
│   ├── scenario_runner.py (1,147)  # ScenarioRunner + ComparisonRunner
│   ├── scorer.py (805 lines)       # MetricsScorer + comparison
│   ├── recorder.py (334 lines)     # EventRecorder
│   ├── wrappers.py (334 lines)     # EvaluationOrchestratorWrapper
│   ├── validator.py (382 lines)    # ExpectationValidator
│   └── foundry_exporter.py (708)   # Azure AI Foundry integration
│
├── Scenarios (YAML configs)
│   ├── ab_tests/fraud_detection_comparison.yaml
│   └── session_based/banking_multi_agent.yaml, all_agents_discovery.yaml
```

---

## Identified Problems

### 1. Three Competing Configuration Paradigms

The framework now supports **three different ways** to configure scenario execution:

| Paradigm | Key Field | Purpose | Files |
|----------|-----------|---------|-------|
| **Legacy** | `agent` + `model_override` | Single-agent simple scenarios | (deprecated, still supported) |
| **Template** | `scenario_template` | References scenariostore configs | fraud_detection_comparison.yaml |
| **Session** | `session_config` | Inline orchestration config | banking_multi_agent.yaml |

**Problem**: `scenario_runner.py:581-647` has complex branching logic to detect and handle all three:

```python
if session_config_data:
    # New: Session-based scenario
    ...
else:
    # Existing: Use scenario_template or legacy approach
    ...
```

This creates cognitive overhead and maintenance burden.

### 2. Duplicated Agent Override Patterns

In YAML scenarios, model overrides are repeated for every agent:

```yaml
# fraud_detection_comparison.yaml - GPT-4o variant
agent_overrides:
  - agent: BankingConcierge
    model_override:
      deployment_id: gpt-4o
      temperature: 0.6
      max_tokens: 200
  - agent: CardRecommendation
    model_override:
      deployment_id: gpt-4o      # Same as above
      temperature: 0.6           # Same as above
      max_tokens: 200            # Same as above
  - agent: InvestmentAdvisor
    model_override:
      deployment_id: gpt-4o      # Same as above
      temperature: 0.6           # Same as above
      max_tokens: 200            # Same as above
```

**Each agent's config is nearly identical**, leading to verbose 50+ line variant definitions that should be 5 lines.

### 3. Handoff Configuration Redundancy

In `banking_multi_agent.yaml`, handoffs are defined verbosely:

```yaml
handoffs:
  - from: BankingConcierge
    to: CardRecommendation
    tool: handoff_card_recommendation
    type: discrete
    share_context: true
    handoff_condition: |
      Transfer to CardRecommendation when...

  - from: CardRecommendation
    to: BankingConcierge
    tool: handoff_concierge
    type: discrete
```

**Problem**: This duplicates what's already defined in the agentstore agent YAML files. The evaluation framework should ideally **inherit** handoff topology from agent definitions rather than redeclaring.

### 4. Expectation Schema Verbosity

Turn expectations require full object structures even for simple cases:

```yaml
turns:
  - turn_id: turn_5
    user_input: "What's the difference between a 401k and an IRA?"
    expectations:
      tools_called: []           # Empty but required
      no_handoff: true
      response_constraints:
        must_include:
          - "401k"
          - "IRA"
```

This could be simplified to:
```yaml
- turn: 5
  input: "What's the difference between a 401k and an IRA?"
  expect:
    no_tools: true
    contains: ["401k", "IRA"]
```

### 5. Schema Explosion in schemas.py

The file defines **14 distinct models** across three concerns:
- Core events: `TurnEvent`, `ToolCall`, `EvidenceBlob`, `HandoffEvent`
- Configuration: `SessionAgentConfig`, `SessionHandoffConfig`, `EvalModelConfig`
- Results: `TurnScore`, `PerTurnSummary`, `RunSummary`, `ScenarioExpectations`
- Foundry: `FoundryEvaluatorConfig`, `FoundryDataRow`, `FoundryExportConfig`

**Problem**: These are tightly coupled but spread across one massive file without clear module boundaries.

---

## Scalability Concerns

### Current: O(agents * variants) Configuration Size

For an A/B test with 2 variants and 5 agents:
- Current: 2 * 5 = 10 agent override blocks (duplicative)
- Each block: ~6 lines
- Total: 60+ lines of repetitive YAML

### Projected: Enterprise Scale

For 10 variants and 20 agents:
- Current pattern: 200 override blocks = 1,200+ lines per scenario file

---

## Proposed Composable Architecture

### Principle 1: Configuration Inheritance

```yaml
# New pattern: model_profile + selective overrides
model_profiles:
  fast:
    deployment_id: gpt-4o
    temperature: 0.6
    max_tokens: 200

  reasoning:
    deployment_id: o3-mini
    reasoning_effort: medium
    max_completion_tokens: 2000

variants:
  - id: gpt4o_fast
    apply_profile: fast  # All agents inherit

  - id: o3_reasoning
    apply_profile: reasoning
    agent_overrides:     # Only specify exceptions
      - agent: CardRecommendation
        reasoning_effort: low  # Override just this field
```

**Reduction**: 60 lines → 15 lines

### Principle 2: Handoff Topology Inheritance

```yaml
session_config:
  agents: [BankingConcierge, CardRecommendation, InvestmentAdvisor]
  start_agent: BankingConcierge

  # NEW: Inherit handoffs from agentstore definitions
  handoffs: inherit  # Don't redeclare what agents already define

  # Or: Only specify additions/overrides
  handoff_overrides:
    - from: CardRecommendation
      to: FraudAgent  # New edge not in agentstore
```

### Principle 3: Compact Expectation Syntax

```yaml
# Current verbose:
turns:
  - turn_id: turn_1
    user_input: "Check my balance..."
    expectations:
      tools_called:
        - verify_client_identity
      handoff:
        to_agent: null
      response_constraints:
        must_include: []

# Proposed compact:
turns:
  - input: "Check my balance..."
    expect: [verify_client_identity]  # Tools as array

  - input: "Transfer me to cards"
    expect:
      handoff: CardRecommendation

  - input: "What's a 401k?"
    expect:
      no_tools: true
      contains: ["401k", "IRA"]
```

### Principle 4: Modular Schema Organization

```
schemas/
├── __init__.py           # Public exports only
├── events.py             # TurnEvent, ToolCall, etc.
├── config.py             # Session/agent configuration
├── expectations.py       # ScenarioExpectations, validation
├── results.py            # TurnScore, RunSummary
└── foundry.py            # Foundry-specific schemas
```

### Principle 5: Single Configuration Paradigm

Deprecate the three-way paradigm split. Standardize on **session_config** as the unified approach:

```yaml
# Unified: session_config is always used
scenario_name: my_scenario
session_config:
  template: banking       # Optional: inherit from scenariostore
  agents: all             # Or explicit list
  start_agent: BankingConcierge

model_profiles: { ... }
variants: [ ... ]
turns: [ ... ]
```

---

## Recommended Refactoring Roadmap

### Phase 1: YAML Schema Simplification (Low Risk)

1. Add `model_profiles` support to reduce agent override duplication
2. Add shorthand expectation syntax (with full syntax still supported)
3. Add `handoffs: inherit` support

**Effort**: ~200 lines of schema changes, backward compatible

### Phase 2: Schema Modularization (Medium Risk)

1. Split `schemas.py` into focused modules
2. Create clear public API in `__init__.py`
3. Add schema versioning for future changes

**Effort**: ~400 lines refactored, no behavior change

### Phase 3: Configuration Unification (Higher Risk)

1. Deprecate legacy `agent` + `model_override` pattern
2. Make `session_config` the canonical format
3. Add migration script for existing scenarios

**Effort**: ~300 lines, requires scenario migration

---

## Metrics: Before vs After

| Metric | Current | After Phase 1 | After Phase 3 |
|--------|---------|---------------|---------------|
| Lines per variant (5 agents) | 30-40 | 5-10 | 5-10 |
| Scenario paradigms | 3 | 3 | 1 |
| Schema files | 1 (659 lines) | 5 (~130 each) | 5 (~130 each) |
| Handoff redeclaration | Full | Optional | None |

---

## Files to Modify

| File | Change Type | Priority |
|------|-------------|----------|
| `schemas.py` | Split into modules | Phase 2 |
| `scenario_runner.py:581-647` | Simplify paradigm detection | Phase 3 |
| `scenarios/*.yaml` | Migrate to new syntax | Phase 1+ |
| New: `schemas/profiles.py` | Model profile support | Phase 1 |

---

## Conclusion

The evaluation framework's Python code is solid, but the **YAML configuration layer** has grown organically and needs rationalization. The primary issues are:

1. **Three competing paradigms** for scenario configuration
2. **Repetitive agent overrides** in variant definitions
3. **Redundant handoff declarations** that duplicate agentstore

The proposed composable patterns would reduce configuration verbosity by 60-70% while maintaining full expressiveness for complex scenarios. The refactoring can be done incrementally with full backward compatibility.
