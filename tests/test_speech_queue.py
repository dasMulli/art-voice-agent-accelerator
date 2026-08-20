#!/usr/bin/env python3
"""
Minimal test script to debug the speech queue timeout issue.
This will help us isolate whether the problem is with:
1. The queue mechanism itself
2. The speech recognition callbacks
3. The cross-thread communication
"""

import asyncio
import logging
import time
from dataclasses import dataclass, field
from enum import Enum

# Simple logging setup without OpenTelemetry complications
logging.basicConfig(
    level=logging.DEBUG, format="%(asctime)s - %(name)s - %(levelname)s - %(message)s"
)
logger = logging.getLogger("test_speech_queue")


class SpeechEventType(Enum):
    """Types of speech recognition events."""

    PARTIAL = "partial"
    FINAL = "final"
    ERROR = "error"
    GREETING = "greeting"


@dataclass
class SpeechEvent:
    """Speech recognition event with metadata."""

    event_type: SpeechEventType
    text: str
    language: str | None = None
    speaker_id: str | None = None
    confidence: float | None = None
    timestamp: float | None = field(
        default_factory=time.time
    )  # Use time.time() instead of asyncio loop time


async def test_basic_queue():
    """Test 1: Basic queue put/get functionality"""
    logger.info("🧪 Test 1: Basic queue functionality")

    queue = asyncio.Queue(maxsize=10)

    # Test event
    test_event = SpeechEvent(
        event_type=SpeechEventType.FINAL, text="Hello world test", language="en-US"
    )

    # Put event
    await queue.put(test_event)
    logger.info(f"✅ Event queued successfully. Queue size: {queue.qsize()}")

    # Get event with timeout
    try:
        retrieved_event = await asyncio.wait_for(queue.get(), timeout=1.0)
        logger.info(
            f"✅ Event retrieved successfully: {retrieved_event.event_type.value} - '{retrieved_event.text}'"
        )
        return True
    except TimeoutError:
        logger.error("❌ Queue get timed out - this should not happen!")
        return False


async def test_processing_loop():
    """Test 2: Processing loop similar to Route Turn Thread"""
    logger.info("🧪 Test 2: Processing loop simulation")

    queue = asyncio.Queue(maxsize=10)
    running = True
    events_processed = 0

    async def processing_loop():
        nonlocal events_processed
        while running:
            try:
                logger.debug(f"🔄 Waiting for events (queue size: {queue.qsize()})")
                speech_event = await asyncio.wait_for(queue.get(), timeout=1.0)

                logger.info(
                    f"📢 Processing loop received event: {speech_event.event_type.value} - '{speech_event.text}'"
                )
                events_processed += 1

                if events_processed >= 3:  # Stop after processing 3 events
                    break

            except TimeoutError:
                logger.debug("⏰ Processing loop timeout (normal)")
                continue
            except Exception as e:
                logger.error(f"❌ Error in processing loop: {e}")
                break

    # Start processing loop
    processing_task = asyncio.create_task(processing_loop())

    # Send test events
    test_events = [
        SpeechEvent(SpeechEventType.GREETING, "Welcome message"),
        SpeechEvent(SpeechEventType.FINAL, "User speech input"),
        SpeechEvent(SpeechEventType.FINAL, "Another user input"),
    ]

    for i, event in enumerate(test_events):
        logger.info(f"📤 Sending test event {i+1}: {event.text}")
        await queue.put(event)
        await asyncio.sleep(0.5)  # Small delay between events

    # Wait for processing to complete
    await processing_task

    running = False
    logger.info(f"✅ Processing loop completed. Events processed: {events_processed}")
    return events_processed == 3


async def test_cross_thread_queue():
    """Test 3: Cross-thread queue communication simulation"""
    logger.info("🧪 Test 3: Cross-thread queue communication")

    import threading

    queue = asyncio.Queue(maxsize=10)
    main_loop = asyncio.get_running_loop()
    events_received = []

    def background_thread_func():
        """Simulate Speech SDK Thread sending events"""
        logger.info("🧵 Background thread started")

        test_events = [
            SpeechEvent(SpeechEventType.PARTIAL, "Partial speech..."),
            SpeechEvent(SpeechEventType.FINAL, "Complete speech recognition"),
        ]

        for event in test_events:
            logger.info(f"🧵 Background thread queuing: {event.text}")

            # Method 1: Try put_nowait (fastest)
            try:
                queue.put_nowait(event)
                logger.info("🧵 Event queued via put_nowait")
                continue
            except Exception as e:
                logger.debug(f"🧵 put_nowait failed: {e}, trying run_coroutine_threadsafe...")

            # Method 2: Fall back to run_coroutine_threadsafe
            try:
                future = asyncio.run_coroutine_threadsafe(queue.put(event), main_loop)
                future.result(timeout=0.1)
                logger.info("🧵 Event queued via run_coroutine_threadsafe")
            except Exception as e:
                logger.error(f"🧵 Failed to queue event: {e}")

    # Start background thread
    thread = threading.Thread(target=background_thread_func, daemon=True)
    thread.start()

    # Process events in main thread
    timeout_count = 0
    max_timeouts = 5

    while timeout_count < max_timeouts:
        try:
            logger.debug(f"🔄 Main thread waiting for events (queue size: {queue.qsize()})")
            event = await asyncio.wait_for(queue.get(), timeout=1.0)
            logger.info(f"📢 Main thread received: {event.event_type.value} - '{event.text}'")
            events_received.append(event)

            if len(events_received) >= 2:  # Got both events
                break

        except TimeoutError:
            timeout_count += 1
            logger.debug(f"⏰ Main thread timeout {timeout_count}/{max_timeouts}")
            continue

    thread.join(timeout=1.0)

    logger.info(f"✅ Cross-thread test completed. Events received: {len(events_received)}")
    return len(events_received) == 2


async def main():
    """Run all tests"""
    logger.info("🚀 Starting speech queue debug tests")

    tests = [
        ("Basic Queue", test_basic_queue),
        ("Processing Loop", test_processing_loop),
        ("Cross-Thread Queue", test_cross_thread_queue),
    ]

    results = {}

    for test_name, test_func in tests:
        logger.info(f"\n{'='*50}")
        logger.info(f"🧪 Running {test_name} Test")
        logger.info(f"{'='*50}")

        try:
            result = await test_func()
            results[test_name] = result
            status = "✅ PASSED" if result else "❌ FAILED"
            logger.info(f"{test_name}: {status}")
        except Exception as e:
            logger.error(f"{test_name}: ❌ EXCEPTION - {e}")
            results[test_name] = False

    logger.info(f"\n{'='*50}")
    logger.info("📊 Test Results Summary")
    logger.info(f"{'='*50}")

    for test_name, result in results.items():
        status = "✅ PASSED" if result else "❌ FAILED"
        logger.info(f"{test_name}: {status}")

    all_passed = all(results.values())
    overall_status = "✅ ALL TESTS PASSED" if all_passed else "❌ SOME TESTS FAILED"
    logger.info(f"\nOverall: {overall_status}")

    return all_passed


if __name__ == "__main__":
    asyncio.run(main())
