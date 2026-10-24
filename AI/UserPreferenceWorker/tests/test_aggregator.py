import asyncio
import time
import json
import pytest
import fakeredis.aioredis

from aggregator import PreferenceAggregator
from models import (
    ProductViewedEvent,
    ProductClickedEvent,
    PurchaseCompletedEvent,
    UserPreferenceProfile,
)
from config import settings


@pytest.fixture
async def redis_client():
    client = fakeredis.aioredis.FakeRedis(decode_responses=True)
    yield client
    await client.aclose()


@pytest.fixture
async def aggregator(redis_client):
    agg = PreferenceAggregator(redis_client=redis_client)
    yield agg
    await agg.aclose()


async def test_record_view_stores_interaction(aggregator, redis_client):
    event = ProductViewedEvent(
        user_id="user-1",
        catalog_item_id="item-1",
        duration_ms=5000,
        source="search",
        category="Electronics",
        brand="Sony",
        price=299.99,
        condition="New",
    )

    await aggregator.record_view(event)

    interactions = await redis_client.lrange("user:user-1:interactions", 0, -1)
    assert len(interactions) == 1

    data = json.loads(interactions[0])
    assert data["type"] == "view"
    assert data["category"] == "Electronics"
    assert data["brand"] == "Sony"


async def test_record_view_long_duration_boosts_weight(aggregator, redis_client):
    event = ProductViewedEvent(
        user_id="user-1",
        catalog_item_id="item-1",
        duration_ms=20000,  # 20s → 2x boost
        category="Electronics",
    )

    await aggregator.record_view(event)

    interactions = await redis_client.lrange("user:user-1:interactions", 0, -1)
    data = json.loads(interactions[0])
    assert data["weight"] == 2.0  # 1.0 * min(2.0, 20000/10000)


async def test_record_click_stores_interaction(aggregator, redis_client):
    event = ProductClickedEvent(
        user_id="user-2",
        catalog_item_id="item-2",
        query_text="wireless headphones",
        rank=3,
        brand="Bose",
    )

    await aggregator.record_click(event)

    interactions = await redis_client.lrange("user:user-2:interactions", 0, -1)
    assert len(interactions) == 1
    data = json.loads(interactions[0])
    assert data["type"] == "click"
    assert data["brand"] == "Bose"


async def test_record_click_high_rank_boosts_weight(aggregator, redis_client):
    event = ProductClickedEvent(
        user_id="user-2",
        catalog_item_id="item-2",
        query_text="headphones",
        rank=10,  # > 5 → 1.5x boost
    )

    await aggregator.record_click(event)

    interactions = await redis_client.lrange("user:user-2:interactions", 0, -1)
    data = json.loads(interactions[0])
    assert data["weight"] == 3.0  # 2.0 * 1.5


async def test_record_purchase_stores_with_high_weight(aggregator, redis_client):
    event = PurchaseCompletedEvent(
        user_id="user-3",
        catalog_item_id="item-3",
        price=150.0,
        category="Audio",
        brand="Sony",
        condition="New",
    )

    await aggregator.record_purchase(event)

    interactions = await redis_client.lrange("user:user-3:interactions", 0, -1)
    data = json.loads(interactions[0])
    assert data["type"] == "purchase"
    assert data["weight"] == 5.0


async def test_profile_computed_after_interaction(aggregator, redis_client):
    await aggregator.record_view(ProductViewedEvent(
        user_id="user-4",
        catalog_item_id="item-1",
        category="Cameras",
        brand="Sony",
        price=1200.0,
        condition="New",
    ))
    await aggregator.record_view(ProductViewedEvent(
        user_id="user-4",
        catalog_item_id="item-2",
        category="Cameras",
        brand="Canon",
        price=900.0,
        condition="New",
    ))
    await aggregator.record_purchase(PurchaseCompletedEvent(
        user_id="user-4",
        catalog_item_id="item-1",
        category="Cameras",
        brand="Sony",
        price=1200.0,
        condition="New",
    ))

    await aggregator.aclose()  # run the trailing recompute of this burst now
    profile = await aggregator.get_profile("user-4")
    assert profile is not None
    assert profile.user_id == "user-4"
    assert "Cameras" in profile.categories
    assert "Sony" in profile.brands
    assert "Canon" in profile.brands
    # Sony should have higher weight (view + purchase = 1+5=6 vs Canon view=1)
    assert profile.brands["Sony"] > profile.brands["Canon"]
    assert profile.interaction_count == 3


async def test_profile_price_percentiles(aggregator, redis_client):
    prices = [100.0, 200.0, 300.0, 400.0, 500.0, 600.0, 700.0, 800.0]
    for i, price in enumerate(prices):
        await aggregator.record_view(ProductViewedEvent(
            user_id="user-5",
            catalog_item_id=f"item-{i}",
            price=price,
        ))

    await aggregator.aclose()  # run the trailing recompute of this burst now
    profile = await aggregator.get_profile("user-5")
    assert profile is not None
    assert profile.price_p25 is not None
    assert profile.price_p75 is not None
    # p25 of [100..800] ≈ 200-300 range, p75 ≈ 600-700 range
    assert profile.price_p25 <= 300.0
    assert profile.price_p75 >= 500.0


async def test_profile_condition_weights(aggregator, redis_client):
    # 3 New views, 1 Used view
    for _ in range(3):
        await aggregator.record_view(ProductViewedEvent(
            user_id="user-6",
            catalog_item_id="item-new",
            condition="New",
        ))
    await aggregator.record_view(ProductViewedEvent(
        user_id="user-6",
        catalog_item_id="item-used",
        condition="Used",
    ))

    await aggregator.aclose()  # run the trailing recompute of this burst now
    profile = await aggregator.get_profile("user-6")
    assert profile is not None
    assert "New" in profile.condition_weights
    assert "Used" in profile.condition_weights
    assert profile.condition_weights["New"] > profile.condition_weights["Used"]


async def test_interactions_capped_at_max(aggregator, redis_client, monkeypatch):
    monkeypatch.setattr(settings, "max_interactions_per_user", 5)

    for i in range(10):
        await aggregator.record_view(ProductViewedEvent(
            user_id="user-7",
            catalog_item_id=f"item-{i}",
        ))

    interactions = await redis_client.lrange("user:user-7:interactions", 0, -1)
    assert len(interactions) == 5


async def test_get_profile_returns_none_for_unknown_user(aggregator):
    profile = await aggregator.get_profile("nonexistent-user")
    assert profile is None


async def test_trailing_recompute_includes_whole_burst(aggregator, monkeypatch):
    # Leading edge: the first event recomputes. Trailing edge: one recompute after the window.
    monkeypatch.setattr(settings, "profile_recompute_debounce_seconds", 1)
    await aggregator.record_view(ProductViewedEvent(user_id="user-8", catalog_item_id="i1", brand="Sony"))
    await aggregator.record_view(ProductViewedEvent(user_id="user-8", catalog_item_id="i2", brand="Canon"))

    profile = await aggregator.get_profile("user-8")
    assert set(profile.brands) == {"Sony"}  # only the leading recompute so far

    await asyncio.sleep(1.3)

    profile = await aggregator.get_profile("user-8")
    assert set(profile.brands) == {"Sony", "Canon"}
    assert profile.interaction_count == 2
