from types import SimpleNamespace
from unittest.mock import AsyncMock, MagicMock

import pytest

from clients.qdrant_client import QdrantSearchClient


@pytest.fixture
def client() -> QdrantSearchClient:
    c = QdrantSearchClient(url="http://qdrant-test:6333", collection="products")
    c._client = AsyncMock()
    return c


def _make_point(point_id, vector, payload):
    point = MagicMock()
    point.id = point_id
    point.vector = vector
    point.payload = payload
    return point


def _make_scored_point(point_id, score, payload):
    point = MagicMock()
    point.id = point_id
    point.score = score
    point.payload = payload
    return point


async def test_find_similar_returns_empty_when_source_not_found(client: QdrantSearchClient) -> None:
    client._client.scroll.return_value = ([], None)

    results = await client.find_similar("nonexistent-id", limit=5)

    assert results == []
    client._client.query_points.assert_not_called()


async def test_find_similar_uses_source_vector_for_search(client: QdrantSearchClient) -> None:
    source_vector = [0.1, 0.2, 0.3]
    source_point = _make_point("point-uuid-1", source_vector, {"product_id": "source-item"})
    client._client.scroll.return_value = ([source_point], None)
    client._client.query_points.return_value = SimpleNamespace(points=[])

    await client.find_similar("source-item", limit=5)

    call_kwargs = client._client.query_points.call_args.kwargs
    assert call_kwargs["query"] == source_vector


async def test_find_similar_excludes_source_point(client: QdrantSearchClient) -> None:
    source_point = _make_point("point-uuid-1", [0.1], {"product_id": "source-item"})
    client._client.scroll.return_value = ([source_point], None)
    client._client.query_points.return_value = SimpleNamespace(points=[])

    await client.find_similar("source-item", limit=5)

    call_kwargs = client._client.query_points.call_args.kwargs
    must_not = call_kwargs["query_filter"].must_not
    assert len(must_not) == 1
    assert "point-uuid-1" in must_not[0].has_id


async def test_find_similar_applies_mandatory_filters(client: QdrantSearchClient) -> None:
    source_point = _make_point("point-uuid-1", [0.1], {"product_id": "source-item"})
    client._client.scroll.return_value = ([source_point], None)
    client._client.query_points.return_value = SimpleNamespace(points=[])

    await client.find_similar("source-item", limit=5)

    call_kwargs = client._client.query_points.call_args.kwargs
    must_conditions = call_kwargs["query_filter"].must
    keys = [c.key for c in must_conditions]
    assert "product_type" in keys
    assert "status" in keys
    assert "has_active_listings" in keys


async def test_find_similar_applies_category_filter(client: QdrantSearchClient) -> None:
    source_point = _make_point("point-uuid-1", [0.1], {"product_id": "source-item"})
    client._client.scroll.return_value = ([source_point], None)
    client._client.query_points.return_value = SimpleNamespace(points=[])

    await client.find_similar("source-item", limit=5, category="Cameras")

    call_kwargs = client._client.query_points.call_args.kwargs
    must_conditions = call_kwargs["query_filter"].must
    category_conditions = [c for c in must_conditions if c.key == "category"]
    assert len(category_conditions) == 1
    assert category_conditions[0].match.value == "Cameras"


async def test_find_similar_applies_condition_filter(client: QdrantSearchClient) -> None:
    source_point = _make_point("point-uuid-1", [0.1], {"product_id": "source-item"})
    client._client.scroll.return_value = ([source_point], None)
    client._client.query_points.return_value = SimpleNamespace(points=[])

    await client.find_similar("source-item", limit=5, condition="New")

    call_kwargs = client._client.query_points.call_args.kwargs
    must_conditions = call_kwargs["query_filter"].must
    condition_conditions = [c for c in must_conditions if c.key == "best_condition"]
    assert len(condition_conditions) == 1
    assert condition_conditions[0].match.value == "New"


async def test_find_similar_returns_scored_results(client: QdrantSearchClient) -> None:
    source_point = _make_point("point-uuid-1", [0.1, 0.2], {"product_id": "source-item"})
    client._client.scroll.return_value = ([source_point], None)

    similar_1 = _make_scored_point("point-uuid-2", 0.95, {"product_id": "similar-item-1"})
    similar_2 = _make_scored_point("point-uuid-3", 0.87, {"product_id": "similar-item-2"})
    client._client.query_points.return_value = SimpleNamespace(points=[similar_1, similar_2])

    results = await client.find_similar("source-item", limit=5)

    assert len(results) == 2
    assert results[0].product_id == "similar-item-1"
    assert results[0].score == 0.95
    assert results[1].product_id == "similar-item-2"
    assert results[1].score == 0.87


async def test_find_similar_respects_limit(client: QdrantSearchClient) -> None:
    source_point = _make_point("point-uuid-1", [0.1], {"product_id": "source-item"})
    client._client.scroll.return_value = ([source_point], None)
    client._client.query_points.return_value = SimpleNamespace(points=[])

    await client.find_similar("source-item", limit=15)

    call_kwargs = client._client.query_points.call_args.kwargs
    assert call_kwargs["limit"] == 15
