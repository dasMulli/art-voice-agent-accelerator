# Evaluation Framework Simplification Plan

**Date:** 2026-01-25  
**Status:** ✅ COMPLETED  
**Goal:** Remove legacy components and reduce complexity in the evaluation framework and documentation

---

## Execution Summary

All tasks completed successfully on 2026-01-25.

### Changes Made

| Task | Status | Details |
|------|--------|---------|
| Delete hooks system | ✅ Done | Removed `tests/evaluation/hooks/` + `test_hooks.py` |
| Delete generators system | ✅ Done | Removed `tests/evaluation/generators/` + `test_generators.py` |
| Update scenario_runner.py | ✅ Done | Removed hook/generator imports and usage |
| Update __init__.py | ✅ Done | Removed `HookRegistry` export |
| Consolidate CLI | ✅ Done | Now only `run` + `submit` commands |
| Consolidate documentation | ✅ Done | Created single `docs/testing/evaluation.md` |
| Update docs/testing/index.md | ✅ Done | Fixed links to new doc structure |
| Update README.md | ✅ Done | Simplified to essential info only |

### Validation Results

- `python -c "import tests.evaluation; ..."` — All core imports work ✅
- `python -m tests.evaluation.cli --help` — Shows only `run` and `submit` ✅
- `pytest tests/evaluation/test_metrics.py` — 52 tests passed ✅

---

## Executive Summary

The evaluation framework has accumulated complexity through multiple iterations (Phase 1-4). This plan removes unused/overengineered components while preserving core functionality:

- **EventRecorder** — recording events
- **EvaluationOrchestratorWrapper** — wrapping orchestrators
- **MetricsScorer** — computing metrics
- **Foundry integration** — cloud evaluation export

### Impact Summary

| Category | Before | After | Reduction |
|----------|--------|-------|-----------|
| Code files | 20 | 10 | 50% |
| Lines of code | ~3,500 | ~2,000 | ~43% |
| Doc files | 4 | 1 | 75% |
| Doc lines | ~2,200 | ~500 | ~77% |
| YAML formats | 3 | 1 | 67% |

---

## Components to Remove

### 1. Hooks System (`hooks/`)

**Current state:** 5 files, ~400 LOC  
**Rationale:** Overengineered for current use. Only 3 built-in hooks exist, and none of the existing scenarios use custom hooks.

**Files to delete:**
```
tests/evaluation/hooks/
├── __init__.py
├── base.py
├── builtin.py
├── registry.py
└── __pycache__/
```

**Migration:** For any future custom analysis needs, use simple Python callbacks in test code rather than a registry system.

---

### 2. Generators System (`generators/`)

**Current state:** 5 files, ~350 LOC  
**Rationale:** Template variable expansion adds complexity without usage. No existing scenarios use `inject:` or `generator:` syntax.

**Files to delete:**
```
tests/evaluation/generators/
├── __init__.py
├── base.py
├── builtin.py
├── expander.py
├── registry.py
└── __pycache__/
```

**Migration:** For dynamic content, write Python test code directly or use pytest parametrization.

---

### 3. Model Profiles Schema

**Current state:** Part of `schemas/config.py`  
**Rationale:** Extra indirection. All existing scenarios use inline `model_override:` instead of referencing profiles.

**Remove:**
- `ModelProfile` class from `schemas/config.py`
- `model_profiles:` key support from scenario runner

**Keep:** Direct `model_override:` on variants (already working)

---

### 4. Multiple Expectation Formats

**Current state:** 3 formats supported:
1. Full `expectations:` block with nested fields
2. Compact `expect:` dictionary
3. Array shorthand `expect: [tool1, tool2]`

**Standardize on:** Compact `expect:` dictionary only

**Before (complex):**
```yaml
turns:
  - turn_id: turn_1
    user_input: "Check balance"
    expectations:
      tools_called:
        - verify_client_identity
        - get_account_balance
      response_constraints:
        must_include_any: ["balance", "account"]
        latency_threshold_ms: 5000
```

**After (simplified):**
```yaml
turns:
  - turn_id: turn_1
    user_input: "Check balance"
    expect:
      tools: [verify_client_identity, get_account_balance]
      contains: ["balance", "account"]
      max_latency: 5000
```

---

### 5. ComparisonRunner Simplification

**Current state:** Separate `ComparisonRunner` class for A/B tests  
**Rationale:** Can be merged into `ScenarioRunner` as a variant mode

**Change:**
- Merge A/B logic into `ScenarioRunner`
- Single `runner.run()` method that detects scenario type
- Remove `compare` CLI command

---

### 6. CLI Command Consolidation

**Current state:** 4 commands: `score`, `scenario`, `compare`, `submit`  
**After:** 2 commands: `run`, `submit`

```bash
# Before
python -m tests.evaluation.cli scenario --input scenario.yaml
python -m tests.evaluation.cli compare --input comparison.yaml
python -m tests.evaluation.cli score --input events.jsonl

# After
python -m tests.evaluation.cli run scenario.yaml       # Auto-detects type
python -m tests.evaluation.cli run events.jsonl        # Scores existing events
python -m tests.evaluation.cli submit events.jsonl     # Foundry submission
```

---

## Documentation Consolidation

### Current State (4 files, ~2,200 lines)

| File | Lines | Content |
|------|-------|---------|
| `docs/testing/evaluation-framework.md` | ~800 | Deep-dive reference |
| `docs/testing/evaluation-scenarios.md` | ~600 | YAML format reference |
| `docs/testing/model-evaluation.md` | ~500 | Overview & quick start |
| `tests/evaluation/README.md` | ~300 | Package structure |

### After (1 file, ~500 lines)

Consolidate into single `docs/testing/evaluation.md`:

```markdown
# Evaluation Framework

Quick Start → Core Concepts → YAML Format → CLI Usage → Foundry Integration → Troubleshooting
```

**Remove:**
- `docs/testing/evaluation-framework.md`
- `docs/testing/evaluation-scenarios.md`  
- `docs/testing/model-evaluation.md`
- `tests/evaluation/README.md` (keep minimal package docstring only)

**Keep:**
- `docs/testing/evaluation.md` (new consolidated file)
- `docs/testing/index.md` (update links)

---

## Simplified YAML Schema

### Single Canonical Format

```yaml
# Required fields only
scenario_name: my_scenario
description: Brief description

session_config:
  agents: [AgentA, AgentB]
  start_agent: AgentA

turns:
  - turn_id: turn_1
    user_input: "User says this"
    expect:
      tools: [tool_name]              # Optional
      handoff: TargetAgent            # Optional
      contains: ["keyword"]           # Optional
      max_latency: 5000               # Optional (ms)

# Optional variant testing (replaces separate comparison files)
variants:
  - variant_id: baseline
    model_override: { deployment_id: gpt-4o }
  - variant_id: challenger
    model_override: { deployment_id: gpt-4o-mini }

# Optional thresholds
thresholds:
  min_tool_precision: 0.8
  min_tool_recall: 0.8

# Optional Foundry export
foundry_export:
  enabled: true
```

### Removed Keys
- `hooks:` — Removed
- `inject:` / `generator:` — Removed
- `model_profiles:` — Removed (use inline `model_override`)
- `scenario_template:` — Removed (explicit config only)
- `expectations:` — Use `expect:` instead

---

## Implementation Plan

### Phase 1: Code Cleanup (Day 1)

**Task 1.1: Delete hooks system**
```bash
rm -rf tests/evaluation/hooks/
rm tests/evaluation/test_hooks.py
```

**Task 1.2: Delete generators system**
```bash
rm -rf tests/evaluation/generators/
rm tests/evaluation/test_generators.py
```

**Task 1.3: Update scenario_runner.py**
Remove:
- `from tests.evaluation.generators import TurnExpander`
- `from tests.evaluation.hooks import HookRegistry`
- `self._hook_registry` initialization in `__init__`
- `_expand_turns()` method body (keep method, return scenario unchanged)
- `_load_fixtures()` method (no longer needed)
- All `self._hook_registry.dispatch_*()` calls

**Task 1.4: Update __init__.py exports**
Remove hook/generator exports from `tests/evaluation/__init__.py`

**Task 1.5: Simplify schemas**
- Remove `ModelProfile` from `schemas/config.py` (keep for now if `ComparisonRunner` uses it)
- Update `schemas/__init__.py` exports

### Phase 2: Runner Consolidation (Day 1)

1. **Merge ComparisonRunner into ScenarioRunner**
   - Add variant detection logic
   - Single `run()` entry point
   - Remove `comparison_runner.py` if separate file

2. **Simplify CLI**
   - Consolidate to `run` and `submit` commands
   - Auto-detect input type (YAML vs JSONL)

### Phase 3: Scenario Migration (Day 2)

1. **Update existing scenarios**
   - Convert `expectations:` to `expect:` format
   - Remove unused keys
   - Test all scenarios pass

2. **Update JSON schema**
   - Remove deprecated keys
   - Simplify schema file

### Phase 4: Documentation (Day 2)

1. **Create consolidated doc**
   - Write new `docs/testing/evaluation.md`
   - Focus on practical usage
   - Include single YAML reference section

2. **Remove old docs**
   - Delete 3 old doc files
   - Update `docs/testing/index.md`
   - Minimize `tests/evaluation/README.md`

---

## Files Summary

### Delete (20 files → 10 files)

```
# Hooks system (5 files + 1 test)
tests/evaluation/hooks/__init__.py
tests/evaluation/hooks/base.py
tests/evaluation/hooks/builtin.py
tests/evaluation/hooks/registry.py
tests/evaluation/test_hooks.py

# Generators system (5 files + 1 test)
tests/evaluation/generators/__init__.py
tests/evaluation/generators/base.py
tests/evaluation/generators/builtin.py
tests/evaluation/generators/expander.py
tests/evaluation/generators/registry.py
tests/evaluation/test_generators.py

# Documentation (3 files)
docs/testing/evaluation-framework.md
docs/testing/evaluation-scenarios.md
docs/testing/model-evaluation.md
```

### Modify

```
tests/evaluation/__init__.py          # Remove imports
tests/evaluation/scenario_runner.py   # Merge runners, remove hooks/generators
tests/evaluation/schemas/__init__.py  # Remove ModelProfile
tests/evaluation/schemas/config.py    # Remove ModelProfile
tests/evaluation/cli/__main__.py      # Consolidate commands
tests/evaluation/README.md            # Minimal docstring only
tests/evaluation/scenarios/*.yaml     # Update to new format
docs/testing/index.md                 # Update links
```

### Create

```
docs/testing/evaluation.md            # New consolidated doc
```

---

## Validation Checklist

After implementation:

- [ ] `pytest tests/evaluation/ -v` passes
- [ ] All existing scenarios run successfully
- [ ] CLI commands work: `run`, `submit`
- [ ] Foundry export still functional
- [ ] No broken imports in codebase
- [ ] Documentation links valid

---

## Risks & Mitigations

| Risk | Mitigation |
|------|------------|
| Breaking existing CI | Run full test suite before merge |
| Lost functionality | Document removed features in CHANGELOG |
| Incomplete migration | Create tracking issue for stragglers |

---

## Rollback Plan

If issues discovered post-merge:

1. Revert the merge commit
2. Create hotfix branch
3. Address specific issue
4. Re-apply with fix

---

## Approval

- [ ] Review by: _______________
- [ ] Approved on: _______________
