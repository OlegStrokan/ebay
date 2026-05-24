import pytest
from unittest.mock import AsyncMock, MagicMock, patch

import qdrant_index_client as _local
QdrantIndexClient = _local.QdrantIndexClient


@pytest.fixture
def client() -> QdrantIndexClient:
    c = QdrantIndexClient.__new__(QdrantIndexClient)
    c._client = AsyncMock()
    return c


async def test_delete_uses_correct_kwarg_collection_name(client: QdrantIndexClient) -> None:
    """Regression test: delete() was passing collections_name= (wrong kwarg) causing TypeError."""
    with patch.object(_local, "settings") as mock_settings:
        mock_settings.qdrant_collection = "products"
        await client.delete("prod-1")

    call_kwargs = client._client.delete.call_args.kwargs
    assert "collection_name" in call_kwargs, (
        "delete() must pass collection_name= not collections_name="
    )
    assert "collections_name" not in call_kwargs


async def test_upsert_passes_correct_collection_and_point(client: QdrantIndexClient) -> None:
    with patch.object(_local, "settings") as mock_settings:
        mock_settings.qdrant_collection = "products"
        await client.upsert("prod-1", [0.1, 0.2], {"name": "Keyboard"})

    call_kwargs = client._client.upsert.call_args.kwargs
    assert call_kwargs["collection_name"] == "products"
    points = call_kwargs["points"]
    assert len(points) == 1
    assert points[0].id == "prod-1"
    assert points[0].vector == [0.1, 0.2]
    assert points[0].payload == {"name": "Keyboard"}


async def test_ensure_collection_skips_create_if_already_exists(client: QdrantIndexClient) -> None:
    existing = MagicMock()
    existing.name = "products"
    client._client.get_collections.return_value = MagicMock(collections=[existing])

    with patch.object(_local, "settings") as mock_settings:
        mock_settings.qdrant_collection = "products"
        mock_settings.vector_dimensions = 768
        await client.ensure_collection()

    client._client.create_collection.assert_not_called()
