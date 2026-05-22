from elasticsearch import AsyncElasticsearch
from models import ParsedQuery, ScoredResult

class ElasticsearchClient:
    def __init__(self, url: str, index: str) -> None:
        self._es = AsyncElasticsearch(url)
        self._index =index

    async def search(self, parsed: ParsedQuery, top_k: int) -> list[ScoredResult]:
        query: dict = {
            "bool": {
                "must": [
                    {
                        "multi_match": {
                            "query": " ".join(parsed.keywords),
                            "fields": ["name^3", "description", "category^2"],
                            "type": "best_fields",
                        }
                    }
                ],
                "filter": [
                    {"term": {"productType": "catalog_item"}},
                ],
            }
        }

        if parsed.filters.price_max:
            query["bool"]["filter"].append({ "range": { "minPrice": { "lte": parsed.filters.price_max}}})

        if parsed.filters.price_min:
            query["bool"]["filter"].append({"range": {"minPrice": {"gte": parsed.filters.price_min}}})

        if parsed.filters.condition:
            query["bool"]["filter"].append({"term": {"bestCondition": parsed.filters.condition}})

        result = await self._es.search(index=self._index, query=query, size=top_k)
        return [
            ScoredResult(product_id=h["_source"]["productId"], score=h["_score"])
            for h in result["hits"]["hits"]
        ]

    async def aclose(self) -> None:
        await self._es.close()