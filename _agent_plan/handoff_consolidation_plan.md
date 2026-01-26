# Handoff Tool Consolidation Plan

## Problem Statement

The current implementation has **14 individual handoff tools** plus 1 generic tool, creating:

1. **Variance in behavior** - Each tool has slightly different schemas, validation, and context passing
2. **Maintenance burden** - ~400 lines of redundant code in handoffs.py
3. **Testing complexity** - Each tool path needs separate testing
4. **Inconsistent handoff outcomes** - The "discrete handoff sitting silent" bug may be related to specific tool behaviors

## Current State

| Category | Count |
|----------|-------|
| Individual handoff tools | 14 |
| Generic handoff tool | 1 (`handoff_to_agent`) |
| Agent YAML files using individual tools | 12 |
| Scenarios using individual tools | 2 (banking, insurance) |
| Scenarios already using generic | 1 (default) |

### Individual Tools to Remove

**Banking (7):**
- `handoff_concierge`
- `handoff_fraud_agent`
- `handoff_card_recommendation`
- `handoff_investment_advisor`
- `handoff_to_auth`
- `handoff_compliance_desk`
- `handoff_to_trading`

**Additional (3):**
- `handoff_transfer_agency_agent`
- `handoff_bank_advisor`
- `handoff_general_kb`

**Insurance (4):**
- `handoff_policy_advisor`
- `handoff_fnol_agent`
- `handoff_claims_specialist`
- `handoff_subro_agent`

## Proposed Solution

### Phase 1: Standardize on `handoff_to_agent`

The `default/orchestration.yaml` already proves this works:

```yaml
handoffs:
  - from: Concierge
    to: AuthAgent
    tool: handoff_to_agent  # Generic tool
    type: discrete
    share_context: true
```

**Key insight**: The scenario's `handoffs` config determines:
- Which agent to switch to (`to_agent`)
- Handoff type (`discrete` vs `announced`)
- Context sharing (`share_context`)

The tool itself just needs to signal "handoff requested" - the orchestration layer handles the rest.

### Phase 2: Simplify Orchestrator Handoff Flow

Current flow (complex):
```
Tool call → HandoffService.is_handoff(name) → resolve_handoff()
         → get target from handoff_map or tool name inference
         → _switch_to() → background task for response
```

Proposed flow (simple):
```
Tool call (handoff_to_agent) → Extract target_agent from args
                            → _switch_to(target_agent, context)
                            → response.create()  # Synchronous, reliable
```

## Implementation Plan

### Step 1: Fix the Core Handoff Issue First

Before consolidating tools, fix the discrete handoff response reliability:

**In `orchestrator.py` `_execute_tool_call()`:**

```python
# CURRENT (unreliable):
asyncio.create_task(_trigger_handoff_response())  # Fire-and-forget

# PROPOSED (reliable):
await self.conn.conversation.item.create(item=handoff_output)
await self.conn.response.create()  # Synchronous, simple
```

### Step 2: Update Scenario Configs

Replace individual tool references with `handoff_to_agent`:

**banking/orchestration.yaml:**
```yaml
# BEFORE:
- from: BankingConcierge
  to: CardRecommendation
  tool: handoff_card_recommendation  # Specific tool

# AFTER:
- from: BankingConcierge
  to: CardRecommendation
  tool: handoff_to_agent  # Generic tool
  type: discrete
  share_context: true
```

### Step 3: Update Agent Configs

Remove individual handoff tools from agent `tools` lists:

**agent.yaml:**
```yaml
# BEFORE:
tools:
  - verify_client_identity
  - get_account_balance
  - handoff_fraud_agent      # Remove
  - handoff_card_recommendation  # Remove

# AFTER:
tools:
  - verify_client_identity
  - get_account_balance
  # handoff_to_agent auto-injected by base agent class
```

### Step 4: Deprecate Individual Tools

In `handoffs.py`, mark individual tools as deprecated but don't remove yet:

```python
# DEPRECATED: Use handoff_to_agent instead
# Kept for backward compatibility - will be removed in v2.0
register_tool(
    "handoff_fraud_agent",
    handoff_fraud_agent_schema,
    handoff_fraud_agent,
    is_handoff=True,
    tags={"handoff", "deprecated"},
)
```

### Step 5: Remove Deprecated Tools (Later)

After validation, remove ~400 lines of individual tool code.

## Files to Modify

### Priority 1: Fix Handoff Response (Hotfix)

| File | Change |
|------|--------|
| `orchestrator.py` | Replace fire-and-forget task with `response.create()` |

### Priority 2: Scenario Configs

| File | Change |
|------|--------|
| `banking/orchestration.yaml` | Replace tool names with `handoff_to_agent` |
| `insurance/orchestration.yaml` | Replace tool names with `handoff_to_agent` |

### Priority 3: Agent Configs

| File | Change |
|------|--------|
| 12 agent.yaml files | Remove individual handoff tools from `tools` list |

### Priority 4: Tool Registry (Cleanup)

| File | Change |
|------|--------|
| `handoffs.py` | Remove ~400 lines of individual tool definitions |

## Validation Strategy

### Unit Tests
- Existing `test_handoff_service.py` - Verify generic handoff works
- Existing `test_generic_handoff_tool.py` - Already tests `handoff_to_agent`
- New `test_voicelive_hotfix_baseline.py` - Verify response reliability

### Integration Tests
- Run `banking_multi_agent.yaml` evaluation scenario
- Verify all handoff paths work with generic tool

### Manual Testing
- Test discrete handoffs in actual VoiceLive connection
- Verify no silent agent behavior

## Benefits After Consolidation

1. **Single code path** for all handoffs
2. **~400 lines removed** from handoffs.py
3. **Consistent behavior** - One tool, one behavior
4. **Easier debugging** - Single point of failure analysis
5. **Simpler testing** - One tool to test thoroughly
6. **Flexible routing** - Scenario config controls everything

## Risk Mitigation

1. **Keep deprecated tools temporarily** - Don't break existing code
2. **Feature flag** - Allow rollback if issues found
3. **Gradual rollout** - Start with one scenario, expand after validation
4. **Comprehensive logging** - Track handoff success/failure rates

## Timeline Recommendation

| Phase | Description | Risk |
|-------|-------------|------|
| **Hotfix (Now)** | Fix response.create() reliability | Low |
| **Phase 1 (Next)** | Update scenario configs | Low |
| **Phase 2 (After)** | Update agent configs | Medium |
| **Phase 3 (Later)** | Remove deprecated tools | Low |
