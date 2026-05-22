from qdrant_client import AsyncQdrantClient
from qdrant_client.models import (
    Filter,
    FieldCondition,
    MatchValue,
    MatchExcept,
    Range,
    HasIdCondition,
)
from models import Filters, ScoredResult

# Domain rule: maps excluded attribute names to (field_key, excluded_values).
# Extend this map when new product attributes need to be filterable.
_ATTRIBUTE_EXCLUSION_MAP: dict[str, tuple[str, list[str]]] = {
    "numpad": ("layout", ["fullsize"]),  # "no numpad" → exclude fullsize layout keyboards
}

class QdrantSearchClient:
    def __init__(self, url: str, collection: str) -> None:
        self._client = AsyncQdrantClient(url=url, check_compatibility=False)
        self._collection = collection

    async def search(
            self,
            vector: list[float],
            filters: Filters,
            top_k: int,
    ) -> list[ScoredResult]:
        must_conditions = [
            FieldCondition(key="product_type", match=MatchValue(value="catalog_item")),
            FieldCondition(key="status", match=MatchValue(value="active")),
            FieldCondition(key="has_active_listings", match=MatchValue(value=True)),
        ]

        if filters.price_max is not None:
            must_conditions.append(
                FieldCondition(key="min_price", range=Range(lte=filters.price_max))
            )

        if filters.price_min is not None:
            must_conditions.append(
                FieldCondition(key="min_price", range=Range(gte=filters.price_min))
            )

        if filters.color:
            must_conditions.append(
                FieldCondition(key="color", match=MatchValue(value=filters.color.lower()))
            )

        if filters.condition:
            must_conditions.append(
                FieldCondition(key="best_condition", match=MatchValue(value=filters.condition))
            )

        for excluded in filters.attributes_excluded:
            if excluded in _ATTRIBUTE_EXCLUSION_MAP:
                field_key, excluded_values = _ATTRIBUTE_EXCLUSION_MAP[excluded]
                must_conditions.append(
                    FieldCondition(key=field_key, match=MatchExcept(except_=excluded_values))
                )

        results = await self._client.search(
            collection_name=self._collection,
            query_vector=vector,
            query_filter=Filter(must=must_conditions),
            limit=top_k,
            with_payload=True,
        )

        return [ScoredResult(product_id=str(r.id), score=r.score) for r in results]

    async def find_similar(
            self,
            catalog_item_id: str,
            limit: int,
            category: str | None = None,
            condition: str | None = None,
    ) -> list[ScoredResult]:
        # fetch the source item's vector by payload filter
        scroll_results, _ = await self._client.scroll(
            collection_name=self._collection,
            scroll_filter=Filter(must=[
                FieldCondition(key="product_id", match=MatchValue(value=catalog_item_id)),
            ]),
            limit=1,
            with_vectors=True,
        )

        if not scroll_results:
            return []

        source_point = scroll_results[0]
        source_vector = source_point.vector

        # build filters: active catalog items with listings, exclude source
        must_conditions = [
            FieldCondition(key="product_type", match=MatchValue(value="catalog_item")),
            FieldCondition(key="status", match=MatchValue(value="active")),
            FieldCondition(key="has_active_listings", match=MatchValue(value=True)),
        ]

        if category:
            must_conditions.append(
                FieldCondition(key="category", match=MatchValue(value=category))
            )

        if condition:
            must_conditions.append(
                FieldCondition(key="best_condition", match=MatchValue(value=condition))
            )

        must_not_conditions = [
            HasIdCondition(has_id=[source_point.id]),
        ]

        results = await self._client.search(
            collection_name=self._collection,
            query_vector=source_vector,
            query_filter=Filter(must=must_conditions, must_not=must_not_conditions),
            limit=limit,
            with_payload=True,
        )

        return [ScoredResult(product_id=str(r.payload.get("product_id", r.id)), score=r.score) for r in results]


