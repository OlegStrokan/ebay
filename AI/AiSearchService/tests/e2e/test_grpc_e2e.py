"""
E2E tests for AiSearchService.

The FastAPI health/ready endpoints are tested through the full app lifecycle.
The gRPC servicer is tested by calling it directly with all downstream clients
mocked — this validates the full in-process pipeline from a single entry point
without requiring generated proto stubs or a running gRPC server.
"""
import sys
from types import SimpleNamespace
from unittest.mock import AsyncMock, MagicMock, patch

import httpx
import pytest
import respx
from asgi_lifespan import LifespanManager

# Inject mock proto stubs (same pattern as tests/unit/test_grpc_servicer.py)
_pb2 = MagicMock()
_pb2_grpc = MagicMock()
_pb2_grpc.AiSearchServiceServicer = type("AiSearchServiceServicer", (), {})
_generated = MagicMock(ai_search_pb2=_pb2, ai_search_pb2_grpc=_pb2_grpc)
sys.modules.setdefault("generated", _generated)
_pb2 = sys.modules.setdefault("generated.ai_search_pb2", _pb2)
_pb2_grpc = sys.modules.setdefault("generated.ai_search_pb2_grpc", _pb2_grpc)
if isinstance(_pb2, MagicMock):
    _pb2.SearchResultItem.side_effect = lambda **fields: SimpleNamespace(**fields)

from grpc_server import AiSearchServicer  # noqa: E402
from models import Filters, ParsedQuery, ScoredResult, SearchPipelineResult, SearchResultItem


# ---------------------------------------------------------------------------
# HTTP app tests
# ---------------------------------------------------------------------------

FAKE_VECTOR = [0.1, 0.2, 0.3]


@pytest.fixture
async def http_client():
    with (
        respx.mock(base_url="http://localhost:8001", assert_all_called=False) as emb_mock,
        respx.mock(base_url="http://localhost:8002", assert_all_called=False) as llm_mock,
    ):
        emb_mock.post("/embed").mock(
            return_value=httpx.Response(200, json={"embeddings": [FAKE_VECTOR]})
        )
        llm_mock.post("/parse-query").mock(
            return_value=httpx.Response(200, json={
                "semantic_query": "keyboard", "filters": {
                    "price_max": None, "price_min": None, "color": None,
                    "category": None, "brand": None,
                    "attributes_required": [], "attributes_excluded": [],
                },
                "keywords": ["keyboard"], "confidence": 0.8, "raw_query": "keyboard",
            })
        )
        from main import app
        with patch("main.serve", new=AsyncMock()):
            async with LifespanManager(app) as manager:
                async with httpx.AsyncClient(
                    transport=httpx.ASGITransport(app=manager.app),
                    base_url="http://test",
                ) as ac:
                    yield ac


async def test_health_endpoint_returns_ok(http_client: httpx.AsyncClient) -> None:
    response = await http_client.get("/health")
    assert response.status_code == 200
    assert response.json() == {"status": "ok"}


async def test_ready_endpoint_returns_ok(http_client: httpx.AsyncClient) -> None:
    response = await http_client.get("/ready")
    assert response.status_code == 200


# ---------------------------------------------------------------------------
# Servicer integration — all downstream clients mocked, pipeline runs for real
# ---------------------------------------------------------------------------

class _FakeRequest:
    def __init__(self, query: str = "keyboard", page: int = 1, page_size: int = 10, debug: bool = False):
        self.query = query
        self.page = page
        self.page_size = page_size
        self.debug = debug
        self.user_id = ""  # proto3 default: no personalized reranking


def _make_servicer_with_mocks():
    llm = AsyncMock()
    llm.parse_query.return_value = ParsedQuery(
        semantic_query="keyboard", filters=Filters(),
        keywords=["keyboard"], confidence=0.9, raw_query="keyboard",
    )
    embedding = AsyncMock()
    embedding.embed.return_value = [0.5] * 4
    qdrant = AsyncMock()
    qdrant.search.return_value = [ScoredResult(product_id="p1", score=0.9)]
    es = AsyncMock()
    es.search.return_value = [ScoredResult(product_id="p2", score=0.7)]
    return AiSearchServicer(llm, embedding, qdrant, es)


async def test_search_returns_merged_results() -> None:
    servicer = _make_servicer_with_mocks()
    await servicer.Search(_FakeRequest(query="keyboard"), MagicMock())

    kwargs = _pb2.SearchResponse.call_args.kwargs
    assert kwargs["total_count"] >= 1
    assert kwargs["used_ai"] is True


async def test_search_with_debug_true_returns_non_empty_debug_json() -> None:
    servicer = _make_servicer_with_mocks()
    await servicer.Search(_FakeRequest(debug=True), MagicMock())

    import json
    kwargs = _pb2.SearchResponse.call_args.kwargs
    data = json.loads(kwargs["parsed_query_debug"])
    assert data["semantic_query"] == "keyboard"
    assert data["confidence"] == 0.9


async def test_search_pagination_page2_returns_different_results() -> None:
    servicer = _make_servicer_with_mocks()
    # 10 products from each source to ensure page 2 exists
    qdrant_results = [ScoredResult(product_id=f"q{i}", score=float(10 - i)) for i in range(10)]
    es_results = [ScoredResult(product_id=f"e{i}", score=float(10 - i)) for i in range(10)]
    servicer._qdrant.search.return_value = qdrant_results
    servicer._es.search.return_value = es_results

    await servicer.Search(_FakeRequest(page=1, page_size=5), MagicMock())
    kwargs_p1 = _pb2.SearchResponse.call_args.kwargs
    items_p1 = [i.product_id for i in kwargs_p1["items"]]

    await servicer.Search(_FakeRequest(page=2, page_size=5), MagicMock())
    kwargs_p2 = _pb2.SearchResponse.call_args.kwargs
    items_p2 = [i.product_id for i in kwargs_p2["items"]]

    assert set(items_p1).isdisjoint(set(items_p2))
