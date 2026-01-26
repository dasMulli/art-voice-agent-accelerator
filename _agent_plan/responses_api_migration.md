# Responses API Streaming: Minimal Impact Migration

## Current State

The code at `src/aoai/manager.py:1210-1215` blocks ALL streaming from using Responses API:

```python
# Lines 1210-1215 (OUTDATED)
if kwargs.get("stream", False):
    logger.debug("Using /chat/completions endpoint (streaming...)")
    return False  # Force chat completions for ALL streaming
```

This was written when Responses API had limited streaming support. As of August 2025, Azure OpenAI Responses API is GA with **full streaming support**.

---

## Minimal Impact Change

### Principle: Respect Explicit Preferences, Safe Defaults

**Change the order of checks** so explicit `endpoint_preference` is honored even for streaming, but keep `auto` mode conservative.

### Before (Current Logic)

```
1. If streaming → Force chat (blocks everything)
2. If endpoint_preference == "responses" → Use responses
3. If endpoint_preference == "chat" → Use chat
4. If auto + new params → Use responses
5. Default → Use chat
```

### After (Proposed Logic)

```
1. If endpoint_preference == "responses" → Use responses (even for streaming)
2. If endpoint_preference == "chat" → Use chat
3. If streaming + auto → Use chat (safe default)
4. If auto + new params → Use responses
5. Default → Use chat
```

---

## Code Change

**File**: `src/aoai/manager.py`
**Lines**: 1210-1227

```python
def _should_use_responses_endpoint(
    self, model_config: Any, **kwargs
) -> bool:
    """
    Determine which endpoint to use based on model configuration and parameters.

    Priority:
    1. Explicit endpoint_preference ("responses" or "chat") is always honored
    2. For streaming with "auto", defaults to chat for compatibility
    3. For non-streaming "auto", uses responses for reasoning models

    Args:
        model_config: ModelConfig instance with endpoint preferences
        **kwargs: Runtime parameters that may indicate endpoint preference

    Returns:
        True if /responses endpoint should be used, False for /chat/completions
    """
    # Handle case where model_config might not have the new attributes
    if not hasattr(model_config, "endpoint_preference"):
        return False

    # 1. Explicit preference ALWAYS wins (even for streaming)
    if model_config.endpoint_preference == "responses":
        logger.debug("Using /responses endpoint (explicit preference)")
        return True
    if model_config.endpoint_preference == "chat":
        logger.debug("Using /chat/completions endpoint (explicit preference)")
        return False

    # 2. For "auto" mode with streaming, default to chat for compatibility
    #    Users can opt-in to responses streaming via explicit preference above
    if kwargs.get("stream", False):
        logger.debug("Using /chat/completions endpoint (streaming with auto mode)")
        return False

    # 3. Auto-detection for non-streaming requests
    # ... rest of auto-detection logic unchanged ...
```

---

## Impact Analysis

| Scenario | Before | After | Impact |
|----------|--------|-------|--------|
| `endpoint_preference: "responses"` + streaming | ❌ Chat (forced) | ✅ Responses | **Now works** |
| `endpoint_preference: "chat"` + streaming | ✅ Chat | ✅ Chat | No change |
| `endpoint_preference: "auto"` + streaming | ✅ Chat | ✅ Chat | No change |
| No preference + streaming | ✅ Chat | ✅ Chat | No change |
| Non-streaming (any) | ✅ Works | ✅ Works | No change |

**Backward Compatible**: All existing code paths remain unchanged unless explicitly opting in.

---

## Testing Strategy

### Phase 1: Opt-In Testing

1. Update code as described above
2. In evaluation YAML, explicitly set `endpoint_preference: "responses"`:

```yaml
agent_overrides:
  - agent: BankingConcierge
    model_override:
      deployment_id: gpt-4o
      endpoint_preference: responses  # Opt-in to new behavior
      temperature: 0.7
```

3. Run evaluation scenarios, compare results to `endpoint_preference: chat`

### Phase 2: Gradual Rollout

1. If Phase 1 succeeds, change default for reasoning models (o1, o3, o4, gpt-5) to use responses even with streaming
2. Monitor latency, error rates, token usage

### Phase 3: Full Migration

1. If Phase 2 succeeds, consider making `responses` the default for all models
2. Deprecate `endpoint_preference: "chat"` as legacy fallback

---

## Rollback Plan

If issues discovered:
1. Set `endpoint_preference: "chat"` in affected scenarios
2. Or revert the single code change (6 lines moved, no logic change)

---

## Implementation Checklist

- [ ] Update `_should_use_responses_endpoint()` in `manager.py`
- [ ] Update docstring to reflect new priority order
- [ ] Add unit test for explicit preference + streaming
- [ ] Test with one evaluation scenario using `endpoint_preference: responses`
- [ ] Compare metrics: latency, token usage, error rates
- [ ] Update design docs if successful

---

## Future Considerations

Once Responses API streaming is validated:

1. **Remove dual endpoint complexity** - Consolidate to Responses API only
2. **Simplify ModelConfig** - Remove `endpoint_preference` field
3. **Leverage Responses features**:
   - `previous_response_id` for stateful conversations
   - Built-in Code Interpreter
   - 40-80% better cache utilization
   - Encrypted reasoning items for context preservation
