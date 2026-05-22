import asyncio
import structlog
from fastapi import FastAPI
from contextlib import asynccontextmanager
from typing import AsyncIterator

import redis.asyncio as aioredis
from clients.embedding_client import EmbeddingClient
from clients.llm_query_client import LLMQueryClient
from clients.qdrant_client import QdrantSearchClient
from clients.es_client import ElasticsearchClient
from grpc_server import AiSearchServicer, serve
from config import settings

log = structlog.get_logger()

@asynccontextmanager
async def lifespan(app: FastAPI) -> AsyncIterator[None]:
    embedding = EmbeddingClient(base_url=settings.embedding_service_url)
    llm = LLMQueryClient(
        base_url=settings.ollama_base_url,
        model=settings.llm_model,
        temperature=settings.llm_temperature,
        num_predict=settings.llm_num_predict,
        timeout=settings.llm_ollama_timeout,
    )
    qdrant = QdrantSearchClient(url=settings.qdrant_url, collection=settings.qdrant_collection)
    es = ElasticsearchClient(url=settings.es_url, index=settings.es_index)

    redis_client = aioredis.from_url(settings.redis_url, decode_responses=True) if settings.reranking_enabled else None

    servicer = AiSearchServicer(
        llm, embedding, qdrant, es,
        redis=redis_client,
        qdrant_collection=settings.qdrant_collection,
    )
    grpc_task = asyncio.create_task(serve(servicer))

    app.state.embedding = embedding
    app.state.llm = llm
    app.state.qdrant = qdrant
    app.state.es = es

    log.info("ai_search_service_started")
    yield

    grpc_task.cancel()
    await embedding.aclose()
    await llm.aclose()
    await es.aclose()
    if redis_client:
        await redis_client.aclose()

app = FastAPI(title="AISearchService", lifespan=lifespan)

@app.get("/health")
async def health() -> dict[str, str]:
    return {"status": "ok"}

@app.get("/ready")
async def ready() -> dict[str, str]:
    return {"status": "ok"}

if __name__ == "__main__":
    import uvicorn
    uvicorn.run(app, host="0.0.0.0", port=settings.http_port)