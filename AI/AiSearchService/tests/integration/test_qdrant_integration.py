import pytest
from qdrant_client.models import PointStruct

from clients.qdrant_client import QdrantSearchClient
from models import Filters
from tests.integration.conftest import (
    VECTOR_SIZE,
    drop_qdrant_collection,
    seed_qdrant_collection,
)

COLLECTION = "test_products_qdrant"

A_ID = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"
B_ID = "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"
C_ID = "cccccccc-cccc-cccc-cccc-cccccccccccc"


def _catalog_item(point_id: str, vector: list[float], name: str, min_price: float, color: str,
                  active: bool) -> PointStruct:
    return PointStruct(
        id=point_id,
        vector=vector,
        payload={
            "product_id": point_id,
            "product_type": "catalog_item",
            "name": name,
            "color": color,
            "min_price": min_price,
            "has_active_listings": active,
            "status": "active" if active else "out_of_stock",
        },
    )


_POINTS = [
    _catalog_item(A_ID, [1.0, 0.0, 0.0, 0.0], "Keyboard A", 80.0, "black", active=True),
    _catalog_item(B_ID, [0.0, 1.0, 0.0, 0.0], "Monitor B", 200.0, "white", active=True),
    _catalog_item(C_ID, [0.9, 0.1, 0.0, 0.0], "Keyboard C", 50.0, "black", active=False),
]


@pytest.fixture(autouse=True)
async def qdrant_collection(qdrant_url: str):
    await seed_qdrant_collection(qdrant_url, COLLECTION, _POINTS)
    yield
    await drop_qdrant_collection(qdrant_url, COLLECTION)


async def test_vector_search_returns_nearest_by_cosine(qdrant_url: str) -> None:
    client = QdrantSearchClient(url=qdrant_url, collection=COLLECTION)
    # Query vector close to point A
    results = await client.search(vector=[1.0, 0.0, 0.0, 0.0], filters=Filters(), top_k=3)

    ids = [r.product_id for r in results]
    # A is nearest to [1,0,0,0]. C is nearer than B but out of stock, so it is filtered out.
    assert ids == [A_ID, B_ID]


async def test_active_status_filter_excludes_out_of_stock(qdrant_url: str) -> None:
    client = QdrantSearchClient(url=qdrant_url, collection=COLLECTION)
    results = await client.search(vector=[0.9, 0.1, 0.0, 0.0], filters=Filters(), top_k=10)

    ids = [r.product_id for r in results]
    assert ids, "active items must be returned, so the filter check is not vacuous"
    assert C_ID not in ids


async def test_price_max_filter_narrows_results(qdrant_url: str) -> None:
    client = QdrantSearchClient(url=qdrant_url, collection=COLLECTION)
    results = await client.search(
        vector=[0.0, 1.0, 0.0, 0.0],
        filters=Filters(price_max=100.0),
        top_k=10,
    )

    ids = [r.product_id for r in results]
    assert ids == [A_ID]
