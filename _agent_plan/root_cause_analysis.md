# Root Cause Analysis: Voice Live Agent Orchestration Issues

## Issue 1: Welcome Prompt Not Respected

### Observed Behavior

When the agent welcome prompt (greeting) is updated, the changes are not reflected when a new conversation starts.

### Code Flow Analysis

```
start() → _switch_to(start_agent, system_vars)
       → _select_pending_greeting()
          → HandoffService.select_greeting()
             → GreetingService.select_greeting()
                → agent.render_greeting(context) ← Uses cached agent
       → Store in _pending_greeting
       → agent.apply_voicelive_session()
       → SESSION_UPDATED event arrives
       → _handle_session_updated()
          → trigger_voicelive_response(say=greeting)
```

### Potential Root Causes

#### 1. Agent Registry Caching (Most Likely)

In [orchestrator.py:333-344](apps/artagent/backend/voice/voicelive/orchestrator.py#L333-L344), the orchestrator config is cached:

```python
@property
def _orchestrator_config(self):
    if not hasattr(self, "_cached_orchestrator_config"):
        self._cached_orchestrator_config = resolve_orchestrator_config(...)
    return self._cached_orchestrator_config
```

The agents are loaded once via `discover_agents()` in [loader.py](apps/artagent/backend/registries/agentstore/loader.py) which reads from YAML files. If the agent YAML file is updated but the process isn't restarted, the old greeting is cached.

#### 2. No Hot Reload for Agent Configs

The agent discovery happens at module import or first access. There's no mechanism to refresh agent configs when YAML files change.

#### 3. Session-scoped Caching

The `_cached_orchestrator_config` is per-instance, but if the same orchestrator instance is reused across calls, it would use stale configs.

### Verification Steps

1. Check if restarting the backend service picks up the new greeting
2. Check if `discover_agents()` is being called fresh for each session
3. Look for any module-level caching of agents

---

## Issue 2: Discrete Handoff Inconsistency

### Observed Behavior

Discrete handoffs from agent to agent via tool call do not consistently invoke the target agent post handoff, causing silent behavior (agent doesn't respond).

### Expected Flow (Simple Pattern)

```
Tool call → invoke target agent with configs → single response from target agent
```

### Actual Code Flow

Looking at [orchestrator.py:1583-1758](apps/artagent/backend/voice/voicelive/orchestrator.py#L1583-L1758):

```python
# Line 1584: Detect handoff
if self.handoff_service.is_handoff(name):
    # Line 1586-1593: Resolve handoff
    resolution = self.handoff_service.resolve_handoff(...)

    # Line 1627-1628: Cancel old agent response
    await self.conn.response.cancel()  # CRITICAL

    # Line 1645: Switch to new agent
    await self._switch_to(target, ctx)

    # Line 1672-1686: Create tool output
    handoff_output = FunctionCallOutputItem(...)
    await self.conn.conversation.item.create(item=handoff_output)

    # Line 1710-1755: Schedule response trigger (ASYNC TASK)
    async def _trigger_handoff_response():
        await asyncio.sleep(0.25)  # 250ms delay
        # Build instruction...
        await self.conn.send(ClientEventResponseCreate(...))

    asyncio.create_task(_trigger_handoff_response())
```

### Identified Problems

#### Problem 1: Race Condition with Response Triggering

The response trigger is scheduled as a background task with a 250ms delay:

```python
async def _trigger_handoff_response():
    await asyncio.sleep(0.25)  # <-- 250ms delay
    await self.conn.send(ClientEventResponseCreate(...))

asyncio.create_task(_trigger_handoff_response(), name=f"handoff-response-{target}")
```

**Issue**: This background task can fail silently or be affected by:
- The task completing before the session update is fully applied
- The task being garbage collected if the orchestrator moves on
- No error handling if `conn.send()` fails
- The task not being tracked/awaited

#### Problem 2: Competing Response Mechanisms

For discrete handoffs, there are TWO potential response triggers:

1. **Greeting Fallback** (from `_switch_to`):
   - [Line 1318-1319](apps/artagent/backend/voice/voicelive/orchestrator.py#L1318-L1319): `_schedule_greeting_fallback(agent_name)` with 350ms delay
   - But for discrete handoffs, `greet_on_switch=False` so `_pending_greeting` is None
   - So this doesn't trigger (by design)

2. **Handoff Response Trigger** (from `_execute_tool_call`):
   - [Line 1710-1755](apps/artagent/backend/voice/voicelive/orchestrator.py#L1710-L1755): Background task with 250ms delay
   - This is supposed to make the agent respond

**Problem**: For discrete handoffs, ONLY the handoff response trigger fires, but it's:
- Not awaited
- Not tracked in `_greeting_tasks`
- Has no retry mechanism
- Can fail silently

#### Problem 3: Session Update Race

The flow is:
1. `_switch_to()` calls `agent.apply_voicelive_session()` which sends session update
2. Session update is async - takes time to be applied by VoiceLive
3. After 250ms, `_trigger_handoff_response()` tries to trigger a response
4. If session isn't fully applied, the response might fail or use old session

#### Problem 4: Missing `response.create()` Call

Looking at the business tool flow (non-handoff) at [line 1798](apps/artagent/backend/voice/voicelive/orchestrator.py#L1798):

```python
# Business tool flow
await self.conn.conversation.item.create(item=output_item)
await self._update_session_context()
await self.conn.response.create()  # <-- Explicit response trigger
```

But for handoff tools, `response.create()` is NOT called. Instead, a background task uses:

```python
await self.conn.send(ClientEventResponseCreate(...))
```

These are different mechanisms:
- `conn.response.create()` is the standard SDK method
- `ClientEventResponseCreate` sends a raw event

The inconsistency may cause issues with how VoiceLive processes the request.

#### Problem 5: Tool Output Before Response

The handoff flow creates the tool output:

```python
await self.conn.conversation.item.create(item=handoff_output)  # Line 1683
```

Then schedules the response trigger as a background task. This means:
- Tool output is created
- Function returns immediately (return True at line 1758)
- Response trigger runs in background

If anything goes wrong with the background task, the model received the tool output but never got triggered to respond.

### Root Cause Summary for Issue 2

**Primary Cause**: The discrete handoff response trigger is a fire-and-forget background task that:
1. Has a fixed 250ms delay that may not be enough for session to settle
2. Is not awaited or tracked
3. Has no retry mechanism
4. Can fail silently
5. Uses a different response mechanism than other parts of the code

**Secondary Cause**: There's no fallback for discrete handoffs. The greeting fallback only fires when `_pending_greeting` is set, but discrete handoffs explicitly skip the greeting.

---

## Proposed Fixes

### Fix for Issue 1: Welcome Prompt Not Respected

#### Option A: Add Agent Config Refresh

Add a method to refresh agent configs when session starts:

```python
async def start(self, system_vars: dict | None = None, refresh_agents: bool = False):
    if refresh_agents:
        self.agents = discover_agents()
        # Rebuild handoff map
        self._handoff_map = build_handoff_map(self.agents)
```

#### Option B: Disable Agent Caching

In `discover_agents()`, add an option to bypass cache:

```python
def discover_agents(use_cache: bool = True) -> dict[str, UnifiedAgent]:
    if not use_cache:
        return _load_agents_from_yaml()
    # existing cached logic
```

#### Option C: Session Override for Greeting

Allow passing greeting override in system_vars:

```python
system_vars = {
    "greeting": "Custom welcome message...",
    # or
    "session_overrides": {
        "greeting": "Custom welcome message..."
    }
}
```

This is already supported in `GreetingService.select_greeting()` (Priority 1 check).

### Fix for Issue 2: Discrete Handoff Inconsistency

#### Recommended Fix: Synchronous Response Trigger

Replace the background task with synchronous response triggering:

```python
# After _switch_to and creating tool output:
await self.conn.conversation.item.create(item=handoff_output)

# Wait for session to be ready (brief delay)
await asyncio.sleep(0.1)

# Trigger response synchronously
if resolution.is_discrete:
    # For discrete: just trigger the agent to respond
    await self.conn.response.create()
else:
    # For announced: use existing greeting mechanism
    # (already handled in _switch_to via _pending_greeting)
    pass
```

This simplifies the discrete handoff to:
1. Switch to target agent (applies session)
2. Create tool output
3. Call `response.create()` to let agent respond naturally

The target agent's instructions already contain the context from the session update, so it will respond appropriately without needing a custom instruction.

#### Alternative Fix: Track and Await Handoff Response Task

```python
# Create task and track it
handoff_task = asyncio.create_task(_trigger_handoff_response())
self._greeting_tasks.add(handoff_task)  # Track it
handoff_task.add_done_callback(lambda t: self._greeting_tasks.discard(t))

# Optionally await with timeout
try:
    await asyncio.wait_for(handoff_task, timeout=2.0)
except asyncio.TimeoutError:
    logger.warning("Handoff response trigger timed out")
```

#### Alternative Fix: Use `response.create()` Instead of Custom Event

```python
async def _trigger_handoff_response():
    await asyncio.sleep(0.1)  # Brief delay for session
    try:
        # Use standard SDK method
        await self.conn.response.create()
    except Exception as e:
        logger.error("Handoff response trigger failed: %s", e)
```

---

## Evaluation Plan

### Test Welcome Prompt Fix

1. Create a test that:
   - Starts session with Agent A (greeting: "Hello from A!")
   - Verifies greeting matches
   - Updates Agent A's greeting in config
   - Starts new session
   - Verifies new greeting is used

### Test Discrete Handoff Fix

1. Modify `banking_multi_agent.yaml` to add explicit discrete handoff test:

```yaml
- turn_id: turn_discrete_handoff
  user_input: "I want to check my cards"
  expectations:
    handoff:
      to_agent: CardRecommendation
      type: discrete
    # Verify target agent responds (not silent)
    response_constraints:
      min_length: 10
      must_not_include:
        - "[silence]"
```

2. Add evaluation metric for "response received after handoff"

3. Run evaluation multiple times to check consistency

---

## Files to Modify

| File | Changes |
|------|---------|
| `orchestrator.py` | Fix handoff response trigger (lines 1710-1758) |
| `loader.py` | Add config refresh option (optional) |
| `banking_multi_agent.yaml` | Add discrete handoff test cases |

---

## Priority

1. **High**: Issue 2 (Discrete Handoff) - causes user-facing silent failures
2. **Medium**: Issue 1 (Welcome Prompt) - affects developer experience during testing
