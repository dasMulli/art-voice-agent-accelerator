# Hotfix Plan: Voice Live Agent Orchestration Issues

## Updated Approach

Based on investigation, the issues stem from **complexity in the handoff system**. The fix has two parts:

1. **Immediate**: Fix the response trigger reliability in orchestrator
2. **Strategic**: Consolidate on `handoff_to_agent` with scenario-defined context schemas

---

## Issue Summary

| Issue | Root Cause | Fix |
|-------|------------|-----|
| Welcome prompt not respected | Agent config caching | Use `session_overrides.greeting` |
| Discrete handoff silent | Fire-and-forget background task | Use synchronous `response.create()` |
| Post-handoff agent calls wrong tool | Old agent's context bleeding | Clear `_last_assistant_message`, skip tool output |
| Inconsistent behavior | 14 different handoff tools | Consolidate on `handoff_to_agent` |

---

## Current State: Phase 1 Hotfix ✅ COMPLETE

### Fix 1: Synchronous Response Trigger (DONE)

Replaced fire-and-forget background task with synchronous `conn.response.create()`:

```python
# BEFORE (unreliable):
async def _trigger_handoff_response():
    await asyncio.sleep(0.25)  # Fixed delay
    await self.conn.send(ClientEventResponseCreate(...))
asyncio.create_task(_trigger_handoff_response())  # Fire-and-forget

# AFTER (reliable):
await self.conn.response.create(additional_instructions=additional_instruction)
```

### Fix 2: Use `additional_instructions` Instead of Override (DONE)

Changed from `ResponseCreateParams(instructions=...)` which **overrides** the system prompt to `additional_instructions=...` which **appends** to it:

```python
# BEFORE (overrides system prompt - loses discrete handoff instructions):
await self.conn.send(ClientEventResponseCreate(
    response=ResponseCreateParams(instructions="Respond now.")
))

# AFTER (appends to system prompt - preserves agent's prompt template):
await self.conn.response.create(
    additional_instructions='The customer\'s request: "...". Respond immediately.'
)
```

### Fix 3: Clear Old Agent Context on Handoff (DONE)

Prevent new agent from seeing old agent's last statement:

```python
# In _switch_to(), when is_handoff=True:
if has_handoff:
    self._last_assistant_message = None  # Clear old agent's statement
```

### Fix 4: Skip Tool Output Injection for Handoffs (DONE)

Don't add old agent's tool call result to new agent's conversation:

```python
# BEFORE: Added tool output after handoff (confused new agent)
await self.conn.conversation.item.create(item=handoff_output)

# AFTER: Skip tool output, trigger response directly
logger.debug("[Handoff] Skipping tool output injection...")
await self.conn.response.create(additional_instructions=...)
```

### Test Results

All 34 tests pass:
- 16 baseline tests (`test_voicelive_hotfix_baseline.py`)
- 18 consistency tests (`test_discrete_handoff_consistency.py`)

---

## Strategic Fix: Tool Consolidation (DEFERRED)

### Current State
- 14 individual handoff tools (`handoff_fraud_agent`, `handoff_concierge`, etc.)
- 1 generic tool (`handoff_to_agent`)
- Each tool has different schemas, validation, context passing

### Concern with Simple Consolidation

Simply removing individual tools and using only `handoff_to_agent` would lose the **nuanced context schemas** that each specific tool provides. For example:

- `handoff_card_recommendation` captures: `customer_goal`, `spending_preferences`, `current_cards`
- `handoff_investment_advisor` captures: `topic`, `employment_change`, `retirement_question`
- Generic `handoff_to_agent` only captures: `target_agent`, `reason`

### Recommended Approach: Scenario-Defined Context Schemas

**Option 1: Scenario-defined context schemas** is the cleanest path because:

- **Single tool, multiple schemas** - The tool is `handoff_to_agent`, but the schema varies per handoff edge
- **No breaking changes** - Current behavior continues working
- **Gradual migration** - Add `context_schema` to handoffs incrementally
- **Testable** - Schemas are validated, missing required fields error out

### Implementation Steps (Future)

1. Add optional `context_schema` field to `HandoffConfig` dataclass
2. Modify tool injection to merge `context_schema` into `handoff_to_agent` parameters
3. Modify `HandoffService` to validate extracted context against schema
4. Migrate one scenario (banking) to use `context_schema`
5. Validate with tests

### Example Schema

```yaml
# In orchestration.yaml
handoffs:
  - from: BankingConcierge
    to: CardRecommendation
    type: discrete
    context_schema:
      type: object
      properties:
        customer_goal:
          type: string
          description: "What they want (lower fees, better rewards, travel perks)"
        spending_preferences:
          type: string
          description: "Where they spend most (travel, dining, groceries)"
        current_cards:
          type: string
          description: "Cards they currently have"
      required: ["customer_goal"]
```

This preserves the rich context capture while consolidating on a single tool.

---

## Implementation Order

### Phase 1: Hotfix ✅ COMPLETE

1. ✅ Fix synchronous response trigger
2. ✅ Use `additional_instructions` to append (not override)
3. ✅ Clear old agent's `_last_assistant_message` on handoff
4. ✅ Skip tool output injection for handoffs
5. ✅ All 34 tests passing

### Phase 2: Context Schema Design (Future)

1. Design `context_schema` field for `HandoffConfig`
2. Prototype with banking scenario
3. Validate context extraction works correctly

### Phase 3: Gradual Migration (Future)

1. Add `context_schema` to high-value handoff edges
2. Keep individual tools working during transition
3. Deprecate individual tools once schemas proven

---

## Key Files Modified (Hotfix)

| File | Changes |
|------|---------|
| [orchestrator.py](apps/artagent/backend/voice/voicelive/orchestrator.py) | Synchronous response, clear context, skip tool output |
| [test_voicelive_hotfix_baseline.py](tests/test_voicelive_hotfix_baseline.py) | Updated 2 tests for new behavior |

---

## Test Commands

```bash
# Run all hotfix tests
python -m pytest tests/test_voicelive_hotfix_baseline.py tests/test_discrete_handoff_consistency.py -v

# Run specific baseline tests
python -m pytest tests/test_voicelive_hotfix_baseline.py -v

# Run handoff consistency tests
python -m pytest tests/test_discrete_handoff_consistency.py -v
```

---

## Related Documents

- [root_cause_analysis.md](_agent_plan/root_cause_analysis.md) - Detailed code analysis
- [handoff_consolidation_plan.md](_agent_plan/handoff_consolidation_plan.md) - Tool consolidation plan
- [baseline_validation.md](_agent_plan/baseline_validation.md) - Test results
