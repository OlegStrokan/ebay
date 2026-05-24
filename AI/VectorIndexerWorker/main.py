import asyncio
import structlog
from grpc_embedding_client import GrpcEmbeddingClient
from qdrant_index_client import QdrantIndexClient
from indexer import Indexer
from consumer import run_consumer
from config import settings

log = structlog.get_logger()

async def main() -> None:
    embedding = GrpcEmbeddingClient(
        grpc_url=settings.embedding_grpc_url,
        default_model=settings.embedding_model,
    )
    qdrant = QdrantIndexClient()
    await qdrant.ensure_collection()

    indexer = Indexer(embedding=embedding, qdrant=qdrant)
    log.info("vector_indexer_worker_starting")
    try:
        await run_consumer(indexer)
    finally:
        await embedding.aclose()

if __name__ == "__main__":
    asyncio.run(main())