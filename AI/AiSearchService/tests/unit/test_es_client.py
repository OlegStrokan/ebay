import json
from unittest.mock import AsyncMock, MagicMock, patch

import httpx
import pytest
import respx

from clients.es_client import ElasticsearchClient
from models import Filters, ParsedQuery


@pytest.fixture
def client() -> ElasticsearchClient:
    return ElasticsearchClient(url="http://es-test:9200", index="products")


def _make_parsed(
    keywords: list[str] | None = None,
    price_max: float | None = None,
    price_min: float | None = None,
) -> ParsedQuery:
    return ParsedQuery(
        semantic_query=" ".join(keywords or []),
        filters=Filters(price_max=price_max, price_min=price_min),
        keywords=keywords or ["keyboard"],
        confidence=0.8,
        raw_query="",
    )


def _es_response(hits: list[dict]) -> dict:
    return {"hits": {"hits": hits}}


async def test_search_builds_multi_match_on_correct_fields(client: ElasticsearchClient) -> None:
    mock_es = AsyncMock()
    mock_es.search.return_value = _es_response([])
    client._es = mock_es

    await client.search(_make_parsed(["keyboard"]), top_k=10)

    call_kwargs = mock_es.search.call_args.kwargs
    query = call_kwargs["query"]
    multi_match = query["bool"]["must"][0]["multi_match"]
    assert "name^3" in multi_match["fields"]
    assert "description" in multi_match["fields"]
    assert "category^2" in multi_match["fields"]


async def test_search_uses_self_index_not_self_index_attribute(client: ElasticsearchClient) -> None:
    """Regression test: es_client must use self._index, not self.index (AttributeError bug)."""
    mock_es = AsyncMock()
    mock_es.search.return_value = _es_response([])
    client._es = mock_es

    # Must not raise AttributeError
    await client.search(_make_parsed(), top_k=5)
    assert mock_es.search.call_args.kwargs["index"] == "products"


async def test_search_always_includes_product_type_catalog_item_filter(client: ElasticsearchClient) -> None:
    mock_es = AsyncMock()
    mock_es.search.return_value = _es_response([])
    client._es = mock_es

    await client.search(_make_parsed(), top_k=5)

    filters = mock_es.search.call_args.kwargs["query"]["bool"]["filter"]
    term_filters = [f for f in filters if "term" in f]
    assert any(f["term"].get("productType") == "catalog_item" for f in term_filters)


async def test_price_max_filter_appended_as_range_lte(client: ElasticsearchClient) -> None:
    mock_es = AsyncMock()
    mock_es.search.return_value = _es_response([])
    client._es = mock_es

    await client.search(_make_parsed(price_max=100.0), top_k=5)

    filters = mock_es.search.call_args.kwargs["query"]["bool"]["filter"]
    range_filters = [f for f in filters if "range" in f]
    assert any(f["range"]["minPrice"].get("lte") == 100.0 for f in range_filters)


async def test_price_min_filter_is_range_gte_not_term_color(client: ElasticsearchClient) -> None:
    """Regression test: price_min must produce a range filter, not a term.color filter."""
    mock_es = AsyncMock()
    mock_es.search.return_value = _es_response([])
    client._es = mock_es

    await client.search(_make_parsed(price_min=20.0), top_k=5)

    filters = mock_es.search.call_args.kwargs["query"]["bool"]["filter"]
    # Must not contain a term.color condition
    term_color_filters = [f for f in filters if "term" in f and "color" in f.get("term", {})]
    assert len(term_color_filters) == 0
    # Must contain a range.minPrice.gte condition
    range_filters = [f for f in filters if "range" in f]
    assert any(f["range"]["minPrice"].get("gte") == 20.0 for f in range_filters)


async def test_hits_mapped_to_scored_results(client: ElasticsearchClient) -> None:
    mock_es = AsyncMock()
    mock_es.search.return_value = _es_response([
        # Catalog item documents carry the ID in "productId".
        {"_source": {"productId": "p1"}, "_score": 1.5},
        {"_source": {"productId": "p2"}, "_score": 0.9},
    ])
    client._es = mock_es

    results = await client.search(_make_parsed(), top_k=10)

    assert len(results) == 2
    assert results[0].product_id == "p1"
    assert results[0].score == 1.5
    assert results[1].product_id == "p2"
