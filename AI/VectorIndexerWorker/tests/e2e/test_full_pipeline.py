import json
from unittest.mock import MagicMock, patch

import httpx
import pytest
import respx
from testcontainers.core.container import DockerContainer

import qdrant_index_client as _local
QdrantIndexClient = _local.QdrantIndexClient

from consumer import process_event
from embedding_client import EmbeddingClient
from indexer import Indexer
from tests.integration.conftest import (
    qdrant_create_collection,
    qdrant_delete_collection,
    qdrant_get_point,
)

COLLECTION = "e2e_products"
FAKE_VECTOR = [0.1, 0.2, 0.3, 0.4]


class _QdrantContainer(DockerContainer):
    def __init__(self) -> None:
        super().__init__("qdrant/qdrant:latest")
        self.with_exposed_ports(6333)

    def get_url(self) -> str:
        return f"http://localhost:{self.get_exposed_port(6333)}"


@pytest.fixture(scope="session")
def qdrant_url_e2e():
    with _QdrantContainer() as container:
        yield container.get_url()


@pytest.fixture(autouse=True)
async def fresh_collection(qdrant_url_e2e: str):
    await qdrant_delete_collection(qdrant_url_e2e, COLLECTION)
    yield
    await qdrant_delete_collection(qdrant_url_e2e, COLLECTION)


@pytest.fixture
def embedding_mock():
    with respx.mock(base_url="http://localhost:8001", assert_all_called=False) as mock:
        mock.post("/embed").mock(
            return_value=httpx.Response(200, json={"embeddings": [FAKE_VECTOR]})
        )
        yield


@pytest.fixture
async def indexer(qdrant_url_e2e: str, embedding_mock) -> Indexer:
    embedding_client = EmbeddingClient()
    qdrant_client = QdrantIndexClient.__new__(QdrantIndexClient)
    qdrant_client._client = _local.AsyncQdrantClient(url=qdrant_url_e2e)

    with patch.object(_local, "settings") as mock_settings:
        mock_settings.qdrant_collection = COLLECTION
        mock_settings.vector_dimensions = 4
        await qdrant_client.ensure_collection()

    return Indexer(embedding_client, qdrant_client)


def _make_msg(event_type: str, payload: dict) -> MagicMock:
    msg = MagicMock()
    msg.headers.return_value = [(b"EventType", event_type.encode())]
    msg.value.return_value = json.dumps(payload).encode()
    return msg


_PRODUCT = {
    "product_id": "e2e-prod-1",
    "name": "Mechanical Keyboard",
    "description": "tactile switches",
    "category": "keyboards",
    "price": 80.0,
    "currency": "USD",
    "stock_quantity": 10,
    "image_urls": [],
    "attributes": [
        {"key": "color", "value": "black"},
        {"key": "brand", "value": "Corsair"},
    ],
}


async def test_create_event_indexes_product_in_qdrant(indexer: Indexer, qdrant_url_e2e: str) -> None:
    msg = _make_msg("ProductCreateEvent", _PRODUCT)
    with patch.object(_local, "settings") as mock_settings:
        mock_settings.qdrant_collection = COLLECTION
        await process_event(msg, indexer)

    point = await qdrant_get_point(qdrant_url_e2e, COLLECTION, "e2e-prod-1")
    assert point is not None
    assert point["payload"]["name"] == "Mechanical Keyboard"
    assert point["payload"]["status"] == "active"
    assert point["payload"]["color"] == "black"
    assert point["payload"]["brand"] == "Corsair"


async def test_update_event_overwrites_existing_product(indexer: Indexer, qdrant_url_e2e: str) -> None:
    create_msg = _make_msg("ProductCreateEvent", _PRODUCT)
    update_payload = {**_PRODUCT, "name": "Updated Keyboard", "stock_quantity": 0}
    update_msg = _make_msg("ProductUpdatedEvent", update_payload)

    with patch.object(_local, "settings") as mock_settings:
        mock_settings.qdrant_collection = COLLECTION
        await process_event(create_msg, indexer)
        await process_event(update_msg, indexer)

    point = await qdrant_get_point(qdrant_url_e2e, COLLECTION, "e2e-prod-1")
    assert point is not None
    assert point["payload"]["name"] == "Updated Keyboard"
    assert point["payload"]["status"] == "out_of_stock"


async def test_delete_event_removes_product_from_qdrant(indexer: Indexer, qdrant_url_e2e: str) -> None:
    create_msg = _make_msg("ProductCreateEvent", _PRODUCT)
    delete_msg = _make_msg("ProductDeletedEvent", {"product_id": "e2e-prod-1"})

    with patch.object(_local, "settings") as mock_settings:
        mock_settings.qdrant_collection = COLLECTION
        await process_event(create_msg, indexer)
        await process_event(delete_msg, indexer)

    point = await qdrant_get_point(qdrant_url_e2e, COLLECTION, "e2e-prod-1")
    assert point is None
