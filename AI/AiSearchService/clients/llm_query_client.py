import json
from pathlib import Path

import httpx
import structlog

from models import Filters, ParsedQuery

log = structlog.get_logger()

PROMPT_PATH = Path(__file__).parent.parent / "prompts" / "query_extraction.txt"


def _fallback(query: str) -> ParsedQuery:
    return ParsedQuery(
        semantic_query=query,
        filters=Filters(),
        keywords=query.split(),
        confidence=0.0,
        raw_query=query,
    )


class LLMQueryClient:
    def __init__(
        self,
        base_url: str,
        model: str,
        temperature: float = 0.1,
        num_predict: int = 256,
        timeout: float = 5.0,
    ) -> None:
        self._http = httpx.AsyncClient(base_url=base_url, timeout=timeout)
        self._model = model
        self._temperature = temperature
        self._num_predict = num_predict
        self._prompt_template = PROMPT_PATH.read_text(encoding="utf-8")

    async def parse_query(self, query: str) -> ParsedQuery:
        prompt = self._prompt_template.format(query=query)
        try:
            response = await self._http.post(
                "/api/generate",
                json={
                    "model": self._model,
                    "prompt": prompt,
                    "format": "json",
                    "stream": False,
                    "options": {
                        "temperature": self._temperature,
                        "num_predict": self._num_predict,
                    },
                },
            )
            response.raise_for_status()
            data = json.loads(response.json()["response"])
            return ParsedQuery(
                semantic_query=data["semantic_query"],
                filters=Filters(**data.get("filters", {})),
                keywords=data.get("keywords", []),
                confidence=data.get("confidence", 0.0),
                raw_query=query,
            )
        except Exception:
            log.exception("llm_parse_failed", query=query)
            return _fallback(query)

    async def aclose(self) -> None:
        await self._http.aclose()