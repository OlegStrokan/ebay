from __future__ import annotations

import json
import asyncio
import structlog
from typing import TYPE_CHECKING
from confluent_kafka import Consumer, Message, KafkaError
from aggregator import PreferenceAggregator
from models import (
    EventType,
    ProductViewedEvent,
    ProductClickedEvent,
    PurchaseCompletedEvent,
)
from config import settings

if TYPE_CHECKING:
    from cooccurrence import CoOccurrenceTracker

log = structlog.get_logger()


def make_consumer() -> Consumer:
    return Consumer({
        "bootstrap.servers": settings.kafka_bootstrap_server,
        "group.id": settings.kafka_group_id,
        "auto.offset.reset": "latest",
        "enable.auto.commit": False,
    })


async def process_event(
    msg: Message, aggregator: PreferenceAggregator, cooccurrence: CoOccurrenceTracker | None = None,
) -> None:
    headers = {k.decode() if isinstance(k, bytes) else k: v for k, v in (msg.headers() or [])}
    raw_event_type = headers.get("event-type") or headers.get("EventType") or b""
    if isinstance(raw_event_type, bytes):
        event_type = raw_event_type.decode()
    else:
        event_type = str(raw_event_type)

    payload = json.loads(msg.value())

    match event_type:
        case EventType.PRODUCT_VIEWED:
            event = ProductViewedEvent(**payload)
            await aggregator.record_view(event)

        case EventType.PRODUCT_CLICKED:
            event = ProductClickedEvent(**payload)
            await aggregator.record_click(event)

        case EventType.PURCHASE_COMPLETED:
            event = PurchaseCompletedEvent(**payload)
            await aggregator.record_purchase(event)
            if cooccurrence:
                await cooccurrence.record_purchase(event.user_id, event.catalog_item_id)

        case EventType.SEARCH_BOUNCED:
            # negative signal - tracked but not yet used for reranking @todo
            log.debug("search_bounced", user_id=payload.get("user_id"))

        case _:
            log.warning("unknown_event_type", event_type=event_type)


async def run_consumer(aggregator: PreferenceAggregator, cooccurrence: CoOccurrenceTracker | None = None) -> None:
    consumer = make_consumer()
    consumer.subscribe(settings.kafka_topics)
    log.info("consumer_started", topics=settings.kafka_topics)

    loop = asyncio.get_event_loop()
    try:
        while True:
            msg = await loop.run_in_executor(None, lambda: consumer.poll(timeout=1.0))
            if msg is None:
                continue
            if msg.error():
                if msg.error().code() == KafkaError._PARTITION_EOF:
                    continue
                log.error("kafka_error", error=str(msg.error()))
                continue
            try:
                await process_event(msg, aggregator, cooccurrence)
                consumer.commit(message=msg, asynchronous=False)
            except Exception:
                log.exception("event_processing_failed", offset=msg.offset())
    finally:
        consumer.close()
