import json
import time
import math
import structlog
import redis.asyncio as redis
from models import (
    UserPreferenceProfile,
    ProductViewedEvent,
    ProductClickedEvent,
    PurchaseCompletedEvent,
)
from config import settings

log = structlog.get_logger()

# interaction weight by event type
_WEIGHTS = {
    "view": 1.0,
    "click": 2.0,
    "purchase": 5.0,
}

# Redis key patterns
_INTERACTIONS_KEY = "user:{user_id}:interactions"
_PROFILE_KEY = "user:{user_id}:preference_profile"
_RECOMPUTE_GUARD_KEY = "user:{user_id}:recompute_guard"


class PreferenceAggregator:
    def __init__(self, redis_client: redis.Redis) -> None:
        self._redis = redis_client
        self._decay_half_life_seconds = settings.decay_half_life_days * 86400

    async def record_view(self, event: ProductViewedEvent) -> None:
        weight = _WEIGHTS["view"]
        # supercharge it for longer views (>10s gets up to 2x weight)
        if event.duration_ms > 10_000:
            weight *= min(2.0, event.duration_ms / 10_000)

        await self._record_interaction(
            user_id=event.user_id,
            event_type="view",
            weight=weight,
            category=event.category,
            brand=event.brand,
            price=event.price,
            condition=event.condition,
        )

    async def record_click(self, event: ProductClickedEvent) -> None:
        weight = _WEIGHTS["click"]
        # clicks from higher ranks (worse position) suggest stronger intent
        if event.rank > 5:
            weight *= 1.5

        await self._record_interaction(
            user_id=event.user_id,
            event_type="click",
            weight=weight,
            category=event.category,
            brand=event.brand,
            price=event.price,
            condition=event.condition,
        )

    async def record_purchase(self, event: PurchaseCompletedEvent) -> None:
        await self._record_interaction(
            user_id=event.user_id,
            event_type="purchase",
            weight=_WEIGHTS["purchase"],
            category=event.category,
            brand=event.brand,
            price=event.price,
            condition=event.condition,
        )

    async def _record_interaction(
        self,
        user_id: str,
        event_type: str,
        weight: float,
        category: str | None,
        brand: str | None,
        price: float | None,
        condition: str | None,
    ) -> None:
        interaction = {
            "type": event_type,
            "weight": weight,
            "category": category,
            "brand": brand,
            "price": price,
            "condition": condition,
            "timestamp": time.time(),
        }

        key = _INTERACTIONS_KEY.format(user_id=user_id)
        await self._redis.lpush(key, json.dumps(interaction))
        await self._redis.ltrim(key, 0, settings.max_interactions_per_user - 1)

        # Throttle full recomputation to once per debounce window per user.
        # SET NX EX is atomic: returns True only for the first caller in the window.
        # Burst events skip the O(N) recompute; the profile is at most
        # `profile_recompute_debounce_seconds` stale, which is acceptable given
        # the 14-day decay half-life.
        guard_key = _RECOMPUTE_GUARD_KEY.format(user_id=user_id)
        should_recompute = await self._redis.set(
            guard_key, "1", nx=True, ex=settings.profile_recompute_debounce_seconds
        )
        if should_recompute:
            await self._recompute_profile(user_id)

    async def _recompute_profile(self, user_id: str) -> None:
        key = _INTERACTIONS_KEY.format(user_id=user_id)
        raw_interactions = await self._redis.lrange(key, 0, -1)

        if not raw_interactions:
            return

        now = time.time()
        category_scores: dict[str, float] = {}
        brand_scores: dict[str, float] = {}
        condition_scores: dict[str, float] = {}
        prices: list[float] = []

        for raw in raw_interactions:
            interaction = json.loads(raw)
            age_seconds = now - interaction["timestamp"]
            decay = math.exp(-0.693 * age_seconds / self._decay_half_life_seconds)
            effective_weight = interaction["weight"] * decay

            if interaction.get("category"):
                cat = interaction["category"]
                category_scores[cat] = category_scores.get(cat, 0.0) + effective_weight

            if interaction.get("brand"):
                br = interaction["brand"]
                brand_scores[br] = brand_scores.get(br, 0.0) + effective_weight

            if interaction.get("condition"):
                cond = interaction["condition"]
                condition_scores[cond] = condition_scores.get(cond, 0.0) + effective_weight

            if interaction.get("price") is not None:
                prices.append(interaction["price"])

        # keep top N categories and brands
        top_categories = dict(
            sorted(category_scores.items(), key=lambda x: x[1], reverse=True)[
                : settings.top_categories
            ]
        )
        top_brands = dict(
            sorted(brand_scores.items(), key=lambda x: x[1], reverse=True)[
                : settings.top_brands
            ]
        )

        # compute price percentiles
        price_p25 = None
        price_p75 = None
        if prices:
            sorted_prices = sorted(prices)
            n = len(sorted_prices)
            price_p25 = sorted_prices[max(0, int(n * 0.25))]
            price_p75 = sorted_prices[min(n - 1, int(n * 0.75))]

        profile = UserPreferenceProfile(
            user_id=user_id,
            categories=top_categories,
            brands=top_brands,
            price_p25=price_p25,
            price_p75=price_p75,
            condition_weights=condition_scores,
            interaction_count=len(raw_interactions),
        )

        profile_key = _PROFILE_KEY.format(user_id=user_id)
        await self._redis.set(profile_key, profile.model_dump_json())
        # expire profiles after 30 days of inactivity
        await self._redis.expire(profile_key, 30 * 86400)

        log.debug(
            "profile_recomputed",
            user_id=user_id,
            categories=len(top_categories),
            brands=len(top_brands),
            interactions=len(raw_interactions),
        )

    async def get_profile(self, user_id: str) -> UserPreferenceProfile | None:
        profile_key = _PROFILE_KEY.format(user_id=user_id)
        raw = await self._redis.get(profile_key)
        if raw is None:
            return None
        return UserPreferenceProfile.model_validate_json(raw)
