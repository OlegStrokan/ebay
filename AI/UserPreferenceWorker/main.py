import asyncio
import structlog
import redis.asyncio as redis
from aggregator import PreferenceAggregator
from cooccurrence import CoOccurrenceTracker
from consumer import run_consumer
from health import start_health_server
from config import settings

log = structlog.get_logger()


async def main() -> None:
    redis_client = redis.from_url(settings.redis_url, decode_responses=True)

    aggregator = PreferenceAggregator(redis_client=redis_client)
    cooccurrence = CoOccurrenceTracker(redis_client=redis_client)

    health_server = start_health_server(settings.kafka_bootstrap_server, settings.health_port)
    log.info("user_preference_worker_starting")
    try:
        await run_consumer(aggregator, cooccurrence)
    finally:
        health_server.shutdown()
        await redis_client.aclose()


if __name__ == "__main__":
    asyncio.run(main())
