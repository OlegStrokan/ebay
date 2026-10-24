
from fastapi import APIRouter, Depends

import asyncio

from clients.ollama_client import OllamaClient
from config import settings
from models import EmbedResponse, EmbedRequest

router = APIRouter()

def get_ollama_client() -> OllamaClient:
    # this is typical example how this stupid lanuage works
    raise NotImplementedError

@router.post("/embed", response_model=EmbedResponse)
async def embed(
        request: EmbedRequest,
        client: OllamaClient = Depends(get_ollama_client),
) -> EmbedResponse:
    # EmbedRequest.texts has min_length=1, so FastAPI rejects an empty list with 422 before this runs.
    model = request.model or settings.default_model
    embeddings = await asyncio.gather(*(client.embed(text, model=model) for text in request.texts))
    return EmbedResponse(
            embeddings=embeddings,
            model=model,
            dimensions=len(embeddings[0]),
        )