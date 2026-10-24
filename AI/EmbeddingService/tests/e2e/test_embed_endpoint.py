import httpx
import pytest
import respx


async def test_health_returns_200(client: httpx.AsyncClient) -> None:
    response = await client.get("/health")
    assert response.status_code == 200
    assert response.json() == {"status": "ok"}


async def test_ready_returns_200(client: httpx.AsyncClient) -> None:
    response = await client.get("/ready")
    assert response.status_code == 200
    assert response.json() == {"status": "ok"}


async def test_embed_returns_correct_response_shape(client: httpx.AsyncClient) -> None:
    response = await client.post("/embed", json={"texts": ["keyboard"]})

    assert response.status_code == 200
    data = response.json()
    assert "embeddings" in data
    assert "dimensions" in data
    assert "model" in data
    assert isinstance(data["embeddings"][0], list)
    assert data["dimensions"] == len(data["embeddings"][0])


async def test_embed_returns_422_for_empty_texts(client: httpx.AsyncClient) -> None:
    response = await client.post("/embed", json={"texts": []})
    # EmbedRequest.texts has min_length=1: FastAPI validation error.
    assert response.status_code == 422


async def test_embed_is_idempotent_for_same_input(client: httpx.AsyncClient) -> None:
    response1 = await client.post("/embed", json={"texts": ["keyboard"]})
    response2 = await client.post("/embed", json={"texts": ["keyboard"]})

    assert response1.status_code == 200
    assert response2.status_code == 200
    assert response1.json()["embeddings"] == response2.json()["embeddings"]


async def test_embed_ollama_error_returns_500(ollama_mock) -> None:
    ollama_mock.post("/api/embeddings").mock(return_value=httpx.Response(503))

    from asgi_lifespan import LifespanManager
    from main import app

    async with LifespanManager(app) as manager:
        # By default ASGITransport re-raises app exceptions into the test.
        # A real server (uvicorn) turns an unhandled exception into a 500 response.
        async with httpx.AsyncClient(
            transport=httpx.ASGITransport(app=manager.app, raise_app_exceptions=False),
            base_url="http://test",
        ) as ac:
            response = await ac.post("/embed", json={"texts": ["test"]})

    assert response.status_code == 500
