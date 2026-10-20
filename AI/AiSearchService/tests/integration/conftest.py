import time

import httpx
import pytest
from elasticsearch import AsyncElasticsearch
from testcontainers.core.container import DockerContainer
from qdrant_client import AsyncQdrantClient
from qdrant_client.models import Distance, PointStruct, VectorParams


# ---------------------------------------------------------------------------
# Container readiness
# ---------------------------------------------------------------------------

def _start_and_get_url(container: DockerContainer, port: int, ready_path: str, timeout: float = 120.0) -> str:
    """Start the container, then wait for the port mapping and for HTTP 200 on ready_path.

    On Docker Desktop the port mapping can appear some time after the container starts.
    testcontainers then raises ConnectionError, so poll for it.
    """
    container.start()
    deadline = time.monotonic() + timeout
    url = None
    while time.monotonic() < deadline:
        try:
            if url is None:
                url = f"http://{container.get_container_host_ip()}:{container.get_exposed_port(port)}"
            if httpx.get(f"{url}{ready_path}", timeout=2.0).status_code == 200:
                return url
        except (ConnectionError, httpx.HTTPError):
            pass
        time.sleep(0.5)
    container.stop()
    raise TimeoutError(f"{container.image} was not ready after {timeout}s")


# ---------------------------------------------------------------------------
# Elasticsearch container — security disabled for tests
# ---------------------------------------------------------------------------

@pytest.fixture(scope="session")
def es_url():
    # Same server version as docker-compose.infra.yml and k8s/elasticsearch.yaml.
    container = (
        DockerContainer("docker.elastic.co/elasticsearch/elasticsearch:8.13.4")
        .with_exposed_ports(9200)
        .with_env("xpack.security.enabled", "false")
        .with_env("discovery.type", "single-node")
        .with_env("ES_JAVA_OPTS", "-Xms512m -Xmx512m")
    )
    url = _start_and_get_url(container, 9200, "/_cluster/health?wait_for_status=yellow&timeout=1s")
    try:
        yield url
    finally:
        container.stop()


# ---------------------------------------------------------------------------
# Qdrant container
# ---------------------------------------------------------------------------

@pytest.fixture(scope="session")
def qdrant_url():
    container = DockerContainer("qdrant/qdrant:v1.17.1").with_exposed_ports(6333)
    url = _start_and_get_url(container, 6333, "/readyz")
    try:
        yield url
    finally:
        container.stop()


# ---------------------------------------------------------------------------
# Seed helpers
# ---------------------------------------------------------------------------

VECTOR_SIZE = 4  # small vectors for speed


async def seed_es_index(url: str, index: str, docs: list[dict]) -> None:
    async with AsyncElasticsearch(url) as es:
        if not await es.indices.exists(index=index):
            await es.indices.create(index=index)
        for doc in docs:
            await es.index(index=index, id=doc["id"], document=doc)
        await es.indices.refresh(index=index)


async def drop_es_index(url: str, index: str) -> None:
    async with AsyncElasticsearch(url) as es:
        if await es.indices.exists(index=index):
            await es.indices.delete(index=index)


async def seed_qdrant_collection(url: str, collection: str, points: list[PointStruct]) -> None:
    client = AsyncQdrantClient(url=url)
    try:
        collections = await client.get_collections()
        names = [c.name for c in collections.collections]
        if collection not in names:
            await client.create_collection(
                collection_name=collection,
                vectors_config=VectorParams(size=VECTOR_SIZE, distance=Distance.COSINE),
            )
        await client.upsert(collection_name=collection, points=points)
    finally:
        await client.close()


async def drop_qdrant_collection(url: str, collection: str) -> None:
    client = AsyncQdrantClient(url=url)
    try:
        collections = await client.get_collections()
        names = [c.name for c in collections.collections]
        if collection in names:
            await client.delete_collection(collection_name=collection)
    finally:
        await client.close()
