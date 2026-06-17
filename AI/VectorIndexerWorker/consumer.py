import structlog
import json
import asyncio
from confluent_kafka import Consumer, Message, KafkaError
from indexer import Indexer
from config import settings

log = structlog.get_logger()

def make_consumer() -> Consumer:
    return Consumer({
        "bootstrap.servers": settings.kafka_bootstrap_server,
        "group.id": settings.kafka_group_id,
        "auto.offset.reset": "latest",
        "enable.auto.commit": False,
    })

async def process_event(msg: Message, indexer: Indexer) -> None:
    headers = {k.decode() if isinstance(k, bytes) else k: v for k, v in (msg.headers() or [])}
    raw_event_type = headers.get("event-type") or headers.get("EventType") or b""
    if isinstance(raw_event_type, bytes):
        event_type = raw_event_type.decode()
    else:
        event_type = str(raw_event_type)
    wrapper = json.loads(msg.value())
    payload = wrapper["Payload"]

    match event_type:
        case "ProductCreatedEvent" | "ProductUpdatedEvent":
            await indexer.upsert(payload)
        case "ProductStockUpdatedEvent":
            await indexer.update_stock(payload)
        case "ProductDeletedEvent":
            product_id_obj = payload.get("ProductId", {})
            product_id = product_id_obj.get("Value") if isinstance(product_id_obj, dict) else product_id_obj
            await indexer.delete(product_id)
        case "CatalogItemCreatedEvent" | "CatalogItemUpdatedEvent":
            await indexer.upsert_catalog_item(payload)
        case "CatalogItemListingSummaryUpdatedEvent":
            await indexer.update_listing_summary(payload)
        case _:
            log.warning("unknown_event_type", event_type=event_type)

async def run_consumer(indexer: Indexer) -> None:
    consumer = make_consumer()
    consumer.subscribe(settings.kafka_topics)
    log.info("consumer_started", topics=settings.kafka_topics)

    loop = asyncio.get_event_loop()
    try:
        while True:
            # pool() is blocking so we run in thread pool to not block the event loop
            msg = await loop.run_in_executor(None, lambda: consumer.poll(timeout=1.0))
            if msg is None:
                continue
            if msg.error():
                if msg.error().code() == KafkaError._PARTITION_EOF:
                    continue
                log.error("kafka_error", error=str(msg.error()))
                continue
            try:
                await process_event(msg, indexer)
                consumer.commit(message=msg, asynchronous=False)
            except Exception:
                log.exception("event_processing_failed", offset=msg.offset())
    finally:
        consumer.close()