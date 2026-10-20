import pytest

from clients.es_client import ElasticsearchClient
from models import Filters, ParsedQuery
from tests.integration.conftest import drop_es_index, seed_es_index

INDEX = "test_products_es"

def _catalog_item(item_id: str, name: str, description: str, category: str, min_price: float) -> dict:
    return {
        "id": item_id,
        "productId": item_id,
        "productType": "catalog_item",
        "name": name,
        "description": description,
        "category": category,
        "categoryId": category,
        "minPrice": min_price,
        "minPriceCurrency": "USD",
        "hasActiveListings": True,
        "bestCondition": "New",
    }


_PRODUCTS = [
    _catalog_item("p1", "Mechanical Keyboard", "tactile switches", "keyboards", 80.0),
    _catalog_item("p2", "Gaming Mouse", "high dpi optical sensor", "mice", 40.0),
    _catalog_item("p3", "Expensive Monitor", "4K display", "monitors", 500.0),
]


@pytest.fixture(autouse=True)
async def es_index(es_url: str):
    await seed_es_index(es_url, INDEX, _PRODUCTS)
    yield
    await drop_es_index(es_url, INDEX)


def _parsed(keywords: list[str], price_max: float | None = None) -> ParsedQuery:
    return ParsedQuery(
        semantic_query=" ".join(keywords),
        filters=Filters(price_max=price_max),
        keywords=keywords,
        confidence=0.8,
        raw_query=" ".join(keywords),
    )


async def test_keyword_search_returns_matching_products(es_url: str) -> None:
    client = ElasticsearchClient(url=es_url, index=INDEX)
    results = await client.search(_parsed(["keyboard"]), top_k=10)

    ids = [r.product_id for r in results]
    assert "p1" in ids


async def test_price_max_filter_excludes_expensive_products(es_url: str) -> None:
    client = ElasticsearchClient(url=es_url, index=INDEX)
    results = await client.search(_parsed(["monitor"], price_max=100.0), top_k=10)

    ids = [r.product_id for r in results]
    assert "p3" not in ids


async def test_no_results_for_unrelated_query(es_url: str) -> None:
    client = ElasticsearchClient(url=es_url, index=INDEX)
    results = await client.search(_parsed(["zxqjfaketerm12345"]), top_k=10)

    assert results == []
