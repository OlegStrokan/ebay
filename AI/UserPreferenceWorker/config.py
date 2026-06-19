from pydantic_settings import BaseSettings, SettingsConfigDict


class Settings(BaseSettings):
    model_config = SettingsConfigDict(env_prefix="USER_PREF_")

    kafka_bootstrap_server: str = "localhost:9093"
    kafka_group_id: str = "user-preference-worker"
    kafka_topics: list[str] = ["user.events"]

    redis_url: str = "redis://localhost:6379/0"

    # preference computation
    max_interactions_per_user: int = 200
    top_categories: int = 10
    top_brands: int = 10
    decay_half_life_days: float = 14.0
    # Minimum seconds between full profile recomputations per user.
    # Burst events within this window skip the O(N) recompute; the profile
    # catches up on the next event after the window expires.
    profile_recompute_debounce_seconds: int = 5

    # vector preference (for future Option A)
    qdrant_url: str = "http://localhost:6333"
    qdrant_collection: str = "products"
    preference_vector_top_n: int = 20


settings = Settings()
