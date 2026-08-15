"""
Unit tests for AiSearchServicer.SearchStream() bidirectional streaming handler.

Uses the same proto stub injection as test_grpc_servicer.py.
"""
import asyncio
import sys
from unittest.mock import AsyncMock, MagicMock

import pytest

# ---------- inject mock proto modules ----------------------------------------
_pb2 = MagicMock()
_pb2_grpc = MagicMock()
# The servicer base class MUST be a real class (not MagicMock) so that
# Python's MRO doesn't shadow async methods defined in subclasses.
_pb2_grpc.AiSearchServiceServicer = type("AiSearchServiceServicer", (), {})
_generated = MagicMock(ai_search_pb2=_pb2, ai_search_pb2_grpc=_pb2_grpc)
sys.modules.setdefault("generated", _generated)
_pb2 = sys.modules.setdefault("generated.ai_search_pb2", _pb2)
_pb2_grpc = sys.modules.setdefault("generated.ai_search_pb2_grpc", _pb2_grpc)
# -----------------------------------------------------------------------------

from grpc_server import AiSearchServicer  # noqa: E402
from pipeline.streaming_orchestrator import PartialSearchResult, SearchPhase
from models import SearchResultItem


# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------

def _make_servicer():
    return AiSearchServicer(
        llm_client=AsyncMock(),
        embedding_client=AsyncMock(),
        qdrant=AsyncMock(),
        es=AsyncMock(),
    )


class _FakeStreamRequest:
    def __init__(self, request_id: str = "req1", query: str = "keyboard", page: int = 1, page_size: int = 20, user_id: str = ""):
        self.request_id = request_id
        self.query = query
        self.page = page
        self.page_size = page_size
        self.user_id = user_id


async def _async_iter(items):
    """Turn a list into an async iterator (simulates request_iterator)."""
    for item in items:
        yield item


async def _collect_responses(async_gen) -> list:
    """Collect all yielded StreamSearchResponse protos."""
    results = []
    async for item in async_gen:
        results.append(item)
    return results


def _keyword_result(product_id: str = "p1") -> PartialSearchResult:
    return PartialSearchResult(
        phase=SearchPhase.KEYWORD,
        items=[SearchResultItem(
            product_id=product_id, name="", category="",
            price=0.0, currency="USD", relevance_score=0.9, image_urls=[],
        )],
        total=1,
        used_ai=True,
    )


def _merged_result(product_id: str = "p1") -> PartialSearchResult:
    return PartialSearchResult(
        phase=SearchPhase.MERGED,
        items=[SearchResultItem(
            product_id=product_id, name="", category="",
            price=0.0, currency="USD", relevance_score=0.95, image_urls=[],
        )],
        total=1,
        used_ai=True,
    )


# ---------------------------------------------------------------------------
# Tests
# ---------------------------------------------------------------------------


async def test_stream_yields_two_responses_for_single_query(monkeypatch) -> None:
    """Single query → keyword response + merged response."""
    servicer = _make_servicer()

    async def mock_streaming_search(**kwargs):
        yield _keyword_result()
        yield _merged_result()

    monkeypatch.setattr(
        "grpc_server.run_streaming_search",
        lambda **kw: mock_streaming_search(**kw),
    )

    request_iter = _async_iter([_FakeStreamRequest(request_id="r1", query="keyboard")])
    context = MagicMock()

    responses = await _collect_responses(servicer.SearchStream(request_iter, context))

    assert len(responses) == 2


async def test_stream_cancels_previous_search_on_new_query(monkeypatch) -> None:
    """When a new query arrives, the previous in-flight search should be cancelled."""
    servicer = _make_servicer()
    cancelled = asyncio.Event()

    async def slow_streaming_search(**kwargs):
        yield _keyword_result("slow_p1")
        try:
            await asyncio.sleep(100)  # Hang on qdrant
            yield _merged_result("slow_p1")
        except asyncio.CancelledError:
            cancelled.set()
            raise

    async def fast_streaming_search(**kwargs):
        yield _keyword_result("fast_p1")
        yield _merged_result("fast_p1")

    call_count = 0

    def route_search(**kwargs):
        nonlocal call_count
        call_count += 1
        if call_count == 1:
            return slow_streaming_search(**kwargs)
        return fast_streaming_search(**kwargs)

    monkeypatch.setattr("grpc_server.run_streaming_search", lambda **kw: route_search(**kw))

    # Send two requests with a tiny delay
    async def two_requests():
        yield _FakeStreamRequest(request_id="r1", query="slow")
        await asyncio.sleep(0.05)  # Give time for first search to start
        yield _FakeStreamRequest(request_id="r2", query="fast")

    context = MagicMock()
    responses = await _collect_responses(servicer.SearchStream(two_requests(), context))

    # First search should have been cancelled
    assert cancelled.is_set()
    # Should get 2 responses for r2 (keyword + merged)
    assert len(responses) >= 2


async def test_stream_drops_stale_results(monkeypatch) -> None:
    """Results from a cancelled query that race into the queue should be dropped."""
    servicer = _make_servicer()

    async def instant_search(**kwargs):
        yield _keyword_result()
        yield _merged_result()

    monkeypatch.setattr("grpc_server.run_streaming_search", lambda **kw: instant_search(**kw))

    # Reset shared mock call history before this test
    _pb2.StreamSearchResponse.reset_mock()

    request_iter = _async_iter([
        _FakeStreamRequest(request_id="latest", query="keyboard")
    ])
    context = MagicMock()

    responses = await _collect_responses(servicer.SearchStream(request_iter, context))

    # All responses should be for "latest"
    for call in _pb2.StreamSearchResponse.call_args_list:
        assert call.kwargs["request_id"] == "latest"


async def test_stream_queue_is_bounded(monkeypatch) -> None:
    """A slow consumer must not let a fast producer queue results without limit."""
    servicer = _make_servicer()
    monkeypatch.setattr("grpc_server.settings.stream_queue_maxsize", 1)

    produced: list[int] = []

    async def fast_producer(**kwargs):
        for i in range(5):
            produced.append(i)
            yield _keyword_result(f"p{i}")

    monkeypatch.setattr("grpc_server.run_streaming_search", lambda **kw: fast_producer(**kw))

    request_iter = _async_iter([_FakeStreamRequest(request_id="r1", query="keyboard")])
    context = MagicMock()

    gen = servicer.SearchStream(request_iter, context)

    # Pull only the first response and let pending tasks run — the producer must
    # not have raced ahead and filled an unbounded queue with all 5 items.
    await gen.__anext__()
    await asyncio.sleep(0.01)
    assert len(produced) < 5

    # Draining the rest must not hang: backpressure releases as items are consumed.
    async for _ in gen:
        pass
    assert len(produced) == 5


async def test_stream_handles_empty_results(monkeypatch) -> None:
    """Query with no results should still yield two phases with empty items."""
    servicer = _make_servicer()

    async def empty_search(**kwargs):
        yield PartialSearchResult(
            phase=SearchPhase.KEYWORD, items=[], total=0, used_ai=False)
        yield PartialSearchResult(
            phase=SearchPhase.MERGED, items=[], total=0, used_ai=False)

    monkeypatch.setattr("grpc_server.run_streaming_search", lambda **kw: empty_search(**kw))

    request_iter = _async_iter([_FakeStreamRequest()])
    context = MagicMock()

    responses = await _collect_responses(servicer.SearchStream(request_iter, context))

    assert len(responses) == 2


async def test_stream_defaults_page_and_page_size(monkeypatch) -> None:
    """Page=0 and page_size=0 should default to 1 and 20."""
    servicer = _make_servicer()
    captured_kwargs = {}

    async def capture_search(**kwargs):
        captured_kwargs.update(kwargs)
        yield _keyword_result()
        yield _merged_result()

    monkeypatch.setattr("grpc_server.run_streaming_search", lambda **kw: capture_search(**kw))

    request_iter = _async_iter([_FakeStreamRequest(page=0, page_size=0)])
    context = MagicMock()

    await _collect_responses(servicer.SearchStream(request_iter, context))

    assert captured_kwargs["page"] == 1
    assert captured_kwargs["page_size"] == 20


async def test_stream_passes_user_id_to_streaming_search(monkeypatch) -> None:
    """user_id from the request should be forwarded to run_streaming_search."""
    servicer = _make_servicer()
    captured_kwargs = {}

    async def capture_search(**kwargs):
        captured_kwargs.update(kwargs)
        yield _keyword_result()
        yield _merged_result()

    monkeypatch.setattr("grpc_server.run_streaming_search", lambda **kw: capture_search(**kw))

    request_iter = _async_iter([_FakeStreamRequest(user_id="user-77")])
    context = MagicMock()

    await _collect_responses(servicer.SearchStream(request_iter, context))

    assert captured_kwargs["user_id"] == "user-77"
