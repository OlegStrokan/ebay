import httpx
import pytest
from fastapi import FastAPI
from unittest.mock import AsyncMock

from routes.embed import get_ollama_client, router


@pytest.fixture
def app() -> tuple[FastAPI, AsyncMock]:
    mock_client = AsyncMock()
    mock_client.embed.return_value = [0.1, 0.2, 0.3]
    test_app = FastAPI()
    test_app.include_router(router)
    test_app.dependency_overrides[get_ollama_client] = lambda: mock_client
    return test_app, mock_client


async def test_empty_texts_returns_422(app: tuple) -> None:
    test_app, _ = app
    async with httpx.AsyncClient(
        transport=httpx.ASGITransport(app=test_app), base_url="http://test"
    ) as ac:
        response = await ac.post("/embed", json={"texts": []})
    # EmbedRequest.texts has min_length=1: FastAPI validation error.
    assert response.status_code == 422


async def test_single_text_returns_correct_response(app: tuple) -> None:
    test_app, _ = app
    async with httpx.AsyncClient(
        transport=httpx.ASGITransport(app=test_app), base_url="http://test"
    ) as ac:
        response = await ac.post("/embed", json={"texts": ["hello"]})

    assert response.status_code == 200
    data = response.json()
    assert data["embeddings"] == [[0.1, 0.2, 0.3]]
    assert data["dimensions"] == 3
    assert data["model"] == "nomic-embed-text"


async def test_multiple_texts_calls_client_once_per_text(app: tuple) -> None:
    test_app, mock_client = app
    mock_client.embed.side_effect = [[0.1, 0.2], [0.3, 0.4]]

    async with httpx.AsyncClient(
        transport=httpx.ASGITransport(app=test_app), base_url="http://test"
    ) as ac:
        response = await ac.post("/embed", json={"texts": ["foo", "bar"]})

    assert response.status_code == 200
    data = response.json()
    assert data["embeddings"] == [[0.1, 0.2], [0.3, 0.4]]
    assert data["dimensions"] == 2
    assert mock_client.embed.call_count == 2


async def test_model_override_forwarded_to_client(app: tuple) -> None:
    test_app, mock_client = app
    async with httpx.AsyncClient(
        transport=httpx.ASGITransport(app=test_app), base_url="http://test"
    ) as ac:
        await ac.post("/embed", json={"texts": ["hi"], "model": "custom-model"})

    mock_client.embed.assert_called_once_with("hi", model="custom-model")
