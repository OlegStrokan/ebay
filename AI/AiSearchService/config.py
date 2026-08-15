from pydantic_settings import BaseSettings, SettingsConfigDict

class Settings(BaseSettings):
    model_config = SettingsConfigDict(env_prefix="AI_SEARCH_")

    embedding_service_url: str = "http://localhost:8001"

    ollama_base_url: str = "http://localhost:11434"
    llm_model: str = "phi3:mini"
    llm_temperature: float = 0.1
    llm_num_predict: int = 256
    llm_ollama_timeout: float = 5.0

    qdrant_url: str = "http://localhost:6333"
    qdrant_collection: str = "products"

    es_url: str = "http://localhost:9200"
    es_index: str = "products"

    grpc_port: int = 50051
    http_port: int = 8003

    llm_timeout_seconds: float = 1.5
    top_k: int = 50
    rrf_k: int = 60

    # SearchStream is single-flight per connection; this only bounds how far a slow
    # client can lag behind a fast producer before backpressure kicks in.
    stream_queue_maxsize: int = 32

    redis_url: str = "redis://localhost:6379"
    reranking_enabled: bool = True

settings = Settings()