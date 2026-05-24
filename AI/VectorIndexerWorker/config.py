from pydantic_settings import BaseSettings, SettingsConfigDict


class Settings(BaseSettings):
    model_config = SettingsConfigDict(env_prefix="INDEXER_")

    kafka_bootstrap_server: str = "localhost:9093"
    kafka_group_id: str = "vector-indexer-worker"
    kafka_topics: list[str] = ["product.events"]

    embedding_grpc_url: str = "localhost:50052"
    embedding_model: str = "nomic-embed-text"

    qdrant_url: str = "http://localhost:6333"
    qdrant_collection: str = "products"
    vector_dimensions: int = 768

settings = Settings()