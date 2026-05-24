from unittest.mock import patch

import pytest

# Local project module — imported by name to use QdrantIndexClient
# (the pip qdrant_client package is re-exported inside this module as AsyncQdrantClient)
import qdrant_index_client as _local
QdrantIndexClient = _local.QdrantIndexClient

from tests.integration.conftest import (
    qdrant_collection_exists,
    qdrant_create_collection,
    qdrant_delete_collection,
    qdrant_get_point,
)

COLLECTION = "test_products_indexer"
VECTOR = [0.1, 0.2, 0.3, 0.4]


@pytest.fixture(autouse=True)
async def fresh_collection(qdrant_url: str):
    await qdrant_delete_collection(qdrant_url, COLLECTION)
    yield
    await qdrant_delete_collection(qdrant_url, COLLECTION)


def _make_client(qdrant_url: str) -> QdrantIndexClient:
    # AsyncQdrantClient is re-exported from the local module (imported from pip inside it)
    c = QdrantIndexClient.__new__(QdrantIndexClient)
    c._client = _local.AsyncQdrantClient(url=qdrant_url)
    return c


async def test_ensure_collection_creates_collection(qdrant_url: str) -> None:
    client = _make_client(qdrant_url)
    with patch.object(_local, "settings") as mock_settings:
        mock_settings.qdrant_collection = COLLECTION
        mock_settings.vector_dimensions = 4
        await client.ensure_collection()

    assert await qdrant_collection_exists(qdrant_url, COLLECTION)


async def test_ensure_collection_is_idempotent(qdrant_url: str) -> None:
    client = _make_client(qdrant_url)
    with patch.object(_local, "settings") as mock_settings:
        mock_settings.qdrant_collection = COLLECTION
        mock_settings.vector_dimensions = 4
        await client.ensure_collection()
        await client.ensure_collection()  # Must not raise


async def test_upsert_then_retrieve_point(qdrant_url: str) -> None:
    await qdrant_create_collection(qdrant_url, COLLECTION)
    client = _make_client(qdrant_url)

    with patch.object(_local, "settings") as mock_settings:
        mock_settings.qdrant_collection = COLLECTION
        await client.upsert("prod-1", VECTOR, {"name": "Keyboard", "status": "active"})

    point = await qdrant_get_point(qdrant_url, COLLECTION, "prod-1")
    assert point is not None
    assert point["payload"]["name"] == "Keyboard"
    assert point["payload"]["status"] == "active"


async def test_delete_removes_point(qdrant_url: str) -> None:
    await qdrant_create_collection(qdrant_url, COLLECTION)
    client = _make_client(qdrant_url)

    with patch.object(_local, "settings") as mock_settings:
        mock_settings.qdrant_collection = COLLECTION
        await client.upsert("prod-to-delete", VECTOR, {"name": "Mouse"})
        await client.delete("prod-to-delete")

    point = await qdrant_get_point(qdrant_url, COLLECTION, "prod-to-delete")
    assert point is None

# dummy tail to prevent read_file truncation issues
_UNUSED = None
        result = await raw.retrieve(collection_name=COLLECTION, ids=["prod-to-delete"])

    assert result == []
