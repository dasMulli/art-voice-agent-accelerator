# GitHub Issues for Voice Module Improvements

**Validation Status:** All issues below have been verified against the current codebase.

---

## Issue 1: Missing `stop_stt_timer_for_barge_in` method causes AttributeError ✅ VALIDATED

**Labels:** `bug`, `voice`, `cascade`

### Description

In [handler.py:1022](apps/artagent/backend/voice/speech_cascade/handler.py#L1022), the `_handle_barge_in_with_stt_stop` method calls `self.speech_sdk_thread.stop_stt_timer_for_barge_in()`, but this method does not exist on `SpeechSDKThread` (defined at lines 300-465).

```python
async def _handle_barge_in_with_stt_stop(self) -> None:
    """Handle barge-in with STT timer stop."""
    # Stop STT timer first (barge-in ends the current recognition)
    if self.speech_sdk_thread:
        self.speech_sdk_thread.stop_stt_timer_for_barge_in()  # <- Method doesn't exist
    await self.barge_in_controller.handle_barge_in()
```

**Validated:** Searched `SpeechSDKThread` class methods - no `stop_stt_timer_for_barge_in` method exists.

### Expected Behavior

Either:
1. Add the `stop_stt_timer_for_barge_in()` method to `SpeechSDKThread`
2. Remove the call if it's not needed

### Steps to Reproduce

1. Trigger a barge-in event during cascade mode that calls `_handle_barge_in_with_stt_stop`
2. Handler attempts to call non-existent method → `AttributeError`

---

## Issue 2: Global mutable state in VoiceLive handler risks memory leaks ✅ VALIDATED

**Labels:** `tech-debt`, `voice`, `voicelive`

### Description

In [handler.py:112](apps/artagent/backend/voice/voicelive/handler.py#L112), `_pending_background_tasks` is a module-level mutable set:

```python
# Module-level set to track pending background tasks for cleanup
# This prevents fire-and-forget tasks from causing memory leaks
_pending_background_tasks: set[asyncio.Task] = set()
```

**Validated:** Grep found 15 references to this module-level variable used throughout the handler.

This creates several problems:
1. **Shared state across all handler instances** - If multiple VoiceLive handlers exist, they share the same task set
2. **Memory leak potential** - Tasks may accumulate if cleanup fails
3. **Race conditions** - Concurrent handlers may interfere with each other's task tracking

### Suggested Solution

Move `_pending_background_tasks` to be an instance variable on `VoiceLiveSDKHandler`:

```python
class VoiceLiveSDKHandler:
    def __init__(self, ...):
        self._pending_background_tasks: set[asyncio.Task] = set()
```

Update `_background_task()` and `_cancel_all_background_tasks()` to be instance methods.

---

## Issue 3: Audio resampling uses linear interpolation instead of proper audio resampling ✅ VALIDATED

**Labels:** `enhancement`, `voice`, `audio-quality`

### Description

In [handler.py:1858-1874](apps/artagent/backend/voice/voicelive/handler.py#L1858-L1874), audio resampling from 24kHz to 16kHz uses numpy linear interpolation:

```python
def _resample_audio(self, audio_bytes: bytes) -> str:
    try:
        source = np.frombuffer(audio_bytes, dtype=np.int16)
        # ...
        new_idx = np.linspace(0, len(source) - 1, new_len)
        resampled = np.interp(new_idx, np.arange(len(source)), source.astype(np.float32))  # Line 1869
        # ...
```

**Validated:** `np.interp` call confirmed at line 1869.

Linear interpolation is not appropriate for audio signals and can introduce:
- Aliasing artifacts
- High-frequency distortion
- Reduced audio fidelity

### Suggested Solution

Use a proper audio resampling library:

```python
# Option 1: scipy (if already a dependency)
from scipy import signal
resampled = signal.resample(source, new_len)

# Option 2: librosa (specialized for audio)
import librosa
resampled = librosa.resample(source.astype(np.float32), orig_sr=24000, target_sr=16000)

# Option 3: soxr (high-quality resampling)
import soxr
resampled = soxr.resample(source, 24000, 16000)
```

---

## Issue 4: Complex queue eviction logic may cause race conditions ✅ VALIDATED

**Labels:** `tech-debt`, `voice`, `cascade`

### Description

In [handler.py:243-287](apps/artagent/backend/voice/speech_cascade/handler.py#L243-L287), the `queue_speech_result` method has complex queue eviction logic that:

1. Drains the entire queue to find PARTIAL events to evict
2. Re-adds non-evicted events back to the queue
3. Uses blocking put with timeout for critical events

**Validated:** Drain-and-refill pattern confirmed at lines 247-264.

```python
# Problematic pattern (lines 247-264)
temp_events = []
while not speech_queue.empty():
    try:
        old_event = speech_queue.get_nowait()
        if not evicted and old_event.event_type == SpeechEventType.PARTIAL:
            evicted = True
        else:
            temp_events.append(old_event)
    except asyncio.QueueEmpty:
        break

# Put back non-evicted events
for e in temp_events:
    try:
        speech_queue.put_nowait(e)
    except asyncio.QueueFull:
        break
```

This pattern has problems:
- **Race condition**: Other threads may add events while queue is being drained
- **Event ordering**: Re-adding events may change processing order
- **Blocking in async context**: 5-second blocking wait (line 281) can cause issues

### Suggested Solution

Consider:
1. Use a priority queue that naturally prioritizes important events
2. Use separate queues for different event priorities
3. Increase queue size to avoid eviction scenarios
4. Add proper locking if drain-and-refill is necessary

---

## Issue 5: VoiceLive handler is too large (2,118 lines) - consider refactoring ✅ VALIDATED

**Labels:** `tech-debt`, `refactoring`, `voicelive`

### Description

[handler.py](apps/artagent/backend/voice/voicelive/handler.py) is **2,118 lines** and handles multiple concerns:

**Validated:** `wc -l` confirmed 2,118 lines.

The file handles:
1. WebSocket connection management
2. Audio format conversion
3. Event routing and handling
4. DTMF processing
5. Session messaging (`_SessionMessenger` class)
6. Metrics/telemetry
7. Turn tracking

This violates single responsibility principle and makes the code:
- Difficult to test in isolation
- Hard to understand and maintain
- Prone to bugs when modifying one area affects another

### Suggested Refactoring

Extract into focused modules:
- `voicelive/audio.py` - Audio format conversion, resampling
- `voicelive/events.py` - Event routing and handling
- `voicelive/metrics.py` - Turn tracking and telemetry (already partially exists)
- `voicelive/messenger.py` - Session messaging (extract `_SessionMessenger` class at lines 177-620)

---

## Summary

| Issue | Severity | Validated | Impact |
|-------|----------|-----------|--------|
| #1 Missing method | Bug | ✅ | Runtime `AttributeError` on barge-in |
| #2 Global mutable state | Tech Debt | ✅ | Memory leaks, race conditions |
| #3 Poor audio resampling | Enhancement | ✅ | Reduced audio quality |
| #4 Queue race conditions | Tech Debt | ✅ | Event ordering issues |
| #5 Large handler file | Refactoring | ✅ | Maintainability concerns |
