import httpx
from models import ParsedQuery
class EmbeddingClient:
    def __init__(self, base_url: str) -> None:
        self._http = httpx.AsyncClient(base_url=base_url, timeout=10.0)

    async def embed(self, text: str) -> list[float]:
        response = await self._http.post("/embed", json={"texts": [text]})
        response.raise_for_status()
        return response.json()["embeddings"][0]

    async def aclose(self) -> None:
        await self._http.aclose()