from config import settings
from qdrant_client import AsyncQdrantClient
from qdrant_client.models import (
    Distance,
    VectorParams,
    PointStruct,
    Filter,
    FieldCondition,
    MatchValue,
)

class QdrantIndexClient:
    def __init__(self) -> None:
        self._client = AsyncQdrantClient(url=settings.qdrant_url)

    async def ensure_collection(self) -> None:
        collections = await self._client.get_collections()
        names = [c.name for c in collections.collections]
        if settings.qdrant_collection not in names:
            await self._client.create_collection(
                collection_name=settings.qdrant_collection,
                vectors_config=VectorParams(
                    size=settings.vector_dimensions,
                    distance=Distance.COSINE,
                ),
            )

    async def upsert(self, product_id: str, vector: list[float], payload: dict) -> None:
        await self._client.upsert(
            collection_name=settings.qdrant_collection,
            points=[PointStruct(id=product_id, vector=vector, payload=payload)],
        )

    async def delete(self, product_id: str) -> None:
        from qdrant_client.models import PointIdsList
        await self._client.delete(
            collection_name=settings.qdrant_collection,
            points_selector=PointIdsList(points=[product_id]),
        )

    async def update_payload(self, product_id: str, patch: dict) -> None:
        await self._client.set_payload(
            collection_name=settings.qdrant_collection,
            payload=patch,
            points=Filter(
                must=[FieldCondition(key="product_id", match=MatchValue(value=product_id))]
            ),
        )