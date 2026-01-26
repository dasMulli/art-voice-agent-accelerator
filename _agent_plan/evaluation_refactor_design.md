# Evaluation Framework Refactor: Design Decision Points

## Current State Summary

Model overrides **do work** - fully consumed through:
```
YAML → ModelConfig.from_dict() → agent.cascade_model → AzureOpenAIManager → API
```

But the configuration layer has grown organically with:
- 3 paradigms (legacy/template/session)
- Repetitive per-agent overrides
- No hook points for custom analysis
- Tight coupling between scenario definition and execution

---

## Design Goals

1. **Composable configurations** - DRY principle for model configs
2. **Extensible turn analysis** - Inject custom analyzers without modifying core
3. **Clear extension points** - Plugins for metrics, validators, exporters
4. **Backward compatible** - Existing YAMLs continue to work

---

## Decision Point 1: Configuration Inheritance Model

### Current Problem
```yaml
# Repeated 3x per variant = 18 override blocks in a 2-variant comparison
agent_overrides:
  - agent: BankingConcierge
    model_override: { deployment_id: gpt-4o, temperature: 0.6, max_tokens: 200 }
  - agent: CardRecommendation
    model_override: { deployment_id: gpt-4o, temperature: 0.6, max_tokens: 200 }
  - agent: InvestmentAdvisor
    model_override: { deployment_id: gpt-4o, temperature: 0.6, max_tokens: 200 }
```

### Option A: Model Profiles (Recommended)

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
    agent_overrides:  # Optional: per-agent exceptions
      - agent: CardRecommendation
        reasoning_effort: low  # Override just this field
```

**Pros**: 70% reduction in config size, clear inheritance
**Cons**: New schema concept to learn

### Option B: Default + Override Pattern

```yaml
default_model:
  deployment_id: gpt-4o
  temperature: 0.6

variants:
  - variant_id: baseline
    # Uses default_model for all agents

  - variant_id: custom
    agent_overrides:
      - agent: BankingConcierge
        deployment_id: o3-mini  # Override specific fields only
```

**Pros**: Simpler mental model
**Cons**: Less flexibility for named reusable configs

### Option C: External Config References

```yaml
variants:
  - variant_id: baseline
    model_profile: "@profiles/gpt4o_fast.yaml"  # External file
```

**Pros**: Maximum reuse across scenario files
**Cons**: File management complexity

### Recommendation: Option A

Model profiles provide the best balance of reuse, clarity, and self-contained scenario files.

---

## Decision Point 2: Turn Analysis Hooks

### Current Problem
No way to inject custom analysis between turns or after tool calls without modifying `scenario_runner.py`.

### Proposed: Hook System

```yaml
scenario_name: my_experiment

hooks:
  # Called after each turn completes
  on_turn_complete:
    - type: builtin.log_metrics
    - type: custom
      module: my_analyzers.sentiment
      function: analyze_response

  # Called after each tool execution
  on_tool_complete:
    - type: builtin.validate_result
    - type: custom
      module: my_analyzers.tool_quality
      function: check_tool_usage

  # Called before scoring
  pre_score:
    - type: custom
      module: my_analyzers.custom_metrics
      function: compute_domain_metrics
```

### Hook Interface

```python
# hooks/base.py
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
        """
        Called after each turn. Returns additional metadata to attach.
        """
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

### Built-in Hooks

| Hook | Purpose |
|------|---------|
| `builtin.log_metrics` | Log turn metrics to console |
| `builtin.validate_expectations` | Run expectation checks after each turn |
| `builtin.capture_reasoning` | Extract reasoning tokens for o-series |
| `builtin.groundedness_check` | Compute groundedness ratio |

### Custom Hook Example

```python
# my_analyzers/sentiment.py
from tests.evaluation.hooks import TurnHook

class SentimentAnalyzer(TurnHook):
    async def on_turn_complete(self, turn, context):
        # Could call another LLM, use local model, etc.
        sentiment = await self._analyze(turn.response_text)
        return {"sentiment_score": sentiment}
```

---

## Decision Point 3: Turn Injection / Augmentation

### Use Case
Inject dynamic content into turns based on previous results.

### Current Problem
Turns are static - `user_input` is fixed at YAML load time.

### Option A: Template Variables

```yaml
turns:
  - turn_id: turn_1
    user_input: "Check balance for account {{account_id}}"
    inject:
      account_id:
        source: fixture  # From test fixtures
        key: test_account_1

  - turn_id: turn_2
    user_input: "Transfer ${{amount}} to {{recipient}}"
    inject:
      amount:
        source: previous_turn  # From turn_1 response
        extract: "balance * 0.1"  # Expression
      recipient:
        source: context
        key: test_recipient
```

### Option B: Turn Generators

```yaml
turns:
  - turn_id: turn_1
    generator:
      type: custom
      module: my_generators.banking
      function: generate_balance_check
      params:
        include_ssn: true
```

```python
# my_generators/banking.py
def generate_balance_check(params, context):
    return {
        "user_input": f"Check my balance. My SSN ends in {context['test_ssn']}",
        "expectations": {
            "tools_called": ["verify_client_identity", "get_account_balance"]
        }
    }
```

### Option C: Hybrid - Static + Dynamic

```yaml
turns:
  # Static turns defined in YAML
  - turn_id: turn_1
    user_input: "Hi, I need help"

  # Dynamic turns generated at runtime
  - turn_id: dynamic_1
    generator: banking.fraud_scenario
    count: 3  # Generate 3 variations
```

### Recommendation: Option A + C

Template variables for simple injection, generators for complex dynamic scenarios.

---

## Decision Point 4: Metrics Extension

### Current Problem
`MetricsScorer` has hardcoded metrics. Adding new metrics requires modifying `scorer.py`.

### Proposed: Pluggable Metrics

```yaml
metrics:
  # Built-in metrics
  - builtin.tool_precision
  - builtin.tool_recall
  - builtin.groundedness
  - builtin.latency

  # Custom metrics
  - type: custom
    module: my_metrics.domain
    metrics:
      - name: banking_accuracy
        function: compute_banking_accuracy
      - name: compliance_score
        function: compute_compliance
```

### Metric Interface

```python
# metrics/base.py
from abc import ABC, abstractmethod
from tests.evaluation.schemas import TurnEvent, RunSummary

class MetricPlugin(ABC):
    @property
    @abstractmethod
    def name(self) -> str:
        pass

    @abstractmethod
    def compute_turn(self, turn: TurnEvent) -> float:
        """Compute metric for a single turn."""
        pass

    @abstractmethod
    def aggregate(self, turn_scores: list[float]) -> dict[str, float]:
        """Aggregate turn scores into summary metrics."""
        pass
```

---

## Decision Point 5: Expectation Syntax

### Current Problem
Verbose even for simple cases:

```yaml
expectations:
  tools_called: []
  no_handoff: true
  response_constraints:
    must_include:
      - "401k"
```

### Option A: Shorthand Syntax

```yaml
# Shorthand forms
expect: [verify_identity]  # Just tools
expect:
  tools: [verify_identity, get_balance]
  no_tools: true
  handoff: CardAgent
  contains: ["balance", "$"]
  excludes: ["error"]
  max_latency: 5000
```

### Option B: Assertion DSL

```yaml
assertions:
  - "called(verify_identity)"
  - "called(get_balance)"
  - "not called(transfer_funds)"
  - "handoff to CardAgent"
  - "response contains 'balance'"
  - "latency < 5000ms"
```

### Option C: Keep Verbose, Add Shortcuts

```yaml
# Full form still works
expectations:
  tools_called: [verify_identity]
  response_constraints:
    must_include: ["balance"]

# OR shorthand
expect:
  tools: [verify_identity]
  contains: ["balance"]
```

### Recommendation: Option C

Backward compatible, progressive disclosure of complexity.

---

## Decision Point 6: Scenario Composition

### Use Case
Reuse turn sequences across scenarios.

### Proposed: Turn Sequences

```yaml
# sequences/identity_verification.yaml
sequence_name: identity_verification
turns:
  - turn_id: verify_1
    user_input: "Hi, my name is {{name}} and SSN ends in {{ssn}}"
    expect:
      tools: [verify_client_identity]

  - turn_id: verify_2
    user_input: "Yes, that's correct"
    expect:
      tools: [get_user_profile]
```

```yaml
# scenarios/banking_full.yaml
scenario_name: banking_full

imports:
  - "@sequences/identity_verification.yaml"
  - "@sequences/card_recommendation.yaml"

turns:
  - include: identity_verification
    inject:
      name: "Alice Brown"
      ssn: "1234"

  - turn_id: custom_1
    user_input: "What's my balance?"

  - include: card_recommendation
```

---

## Decision Point 7: Architecture Layers

### Proposed Layer Separation

```
┌─────────────────────────────────────────────────────────┐
│                    Scenario Definition                   │
│  (YAML files with profiles, hooks, turns, expectations) │
└─────────────────────────────────────────────────────────┘
                           │
                           ▼
┌─────────────────────────────────────────────────────────┐
│                   Configuration Layer                    │
│  - Profile resolution                                    │
│  - Template variable injection                           │
│  - Turn sequence expansion                               │
│  - Hook registration                                     │
└─────────────────────────────────────────────────────────┘
                           │
                           ▼
┌─────────────────────────────────────────────────────────┐
│                    Execution Engine                      │
│  - ScenarioRunner (simplified)                           │
│  - Turn executor with hook dispatch                      │
│  - Event recording                                       │
└─────────────────────────────────────────────────────────┘
                           │
                           ▼
┌─────────────────────────────────────────────────────────┐
│                    Analysis Layer                        │
│  - Pluggable metrics                                     │
│  - Validator with custom rules                           │
│  - Aggregation & comparison                              │
└─────────────────────────────────────────────────────────┘
                           │
                           ▼
┌─────────────────────────────────────────────────────────┐
│                     Export Layer                         │
│  - JSONL events                                          │
│  - Foundry format                                        │
│  - Custom exporters                                      │
└─────────────────────────────────────────────────────────┘
```

---

## Recommended Refactor Phases

### Phase 1: Model Profiles (Low Risk, High Value)

1. Add `model_profiles` to schema
2. Add profile resolution in `ScenarioRunner.__init__`
3. Modify `_create_session_agents_with_overrides` to use resolved profiles
4. **Backward compatible**: Existing `agent_overrides` still work

**Files**: `schemas.py`, `scenario_runner.py`
**Effort**: ~150 lines

### Phase 2: Hook System (Medium Risk, High Value)

1. Create `hooks/` module with base classes
2. Add hook dispatch to turn execution loop
3. Implement built-in hooks
4. Add `hooks` section to YAML schema

**Files**: New `hooks/` module, `scenario_runner.py`, `schemas.py`
**Effort**: ~400 lines

### Phase 3: Expectation Shorthand (Low Risk, Medium Value)

1. Add shorthand parsing to `ExpectationValidator`
2. Normalize shorthand → full form before validation
3. Update schema to accept both forms

**Files**: `validator.py`, `schemas.py`
**Effort**: ~100 lines

### Phase 4: Turn Templates & Generators (Medium Risk, High Value)

1. Add template variable system
2. Add generator interface
3. Expand turns at scenario load time

**Files**: New `generators/` module, `scenario_runner.py`
**Effort**: ~300 lines

### Phase 5: Pluggable Metrics (Medium Risk, Medium Value)

1. Extract metric computation to plugin interface
2. Migrate built-in metrics to plugins
3. Add custom metric loading from YAML

**Files**: `scorer.py` refactor, new `metrics/` module
**Effort**: ~350 lines

---

## Open Questions for Discussion

1. **Profile inheritance**: Should profiles support extending other profiles?
   ```yaml
   model_profiles:
     base: { deployment_id: gpt-4o }
     fast:
       extends: base
       max_tokens: 100
   ```

2. **Hook execution order**: Sequential or parallel? Fail-fast or collect-all?

3. **Generator determinism**: Should generators be seeded for reproducibility?

4. **Metric dependencies**: Can custom metrics depend on built-in metrics?

5. **Configuration validation**: Validate at load time or fail at runtime?

6. **Versioning**: Should scenario files have a schema version for migration?

---

## Next Steps

1. Confirm design direction for each decision point
2. Prioritize phases based on immediate needs
3. Start with Phase 1 (model profiles) as proof of concept
4. Iterate based on real-world usage
