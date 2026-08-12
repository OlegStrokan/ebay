# Vector Indexer Worker

Consumes product events from Kafka → builds text corpus → calls EmbeddingService via gRPC -> upserts into Qdrant.

- one gRPC stream to EmbeddingService, long-running async kafka consumer, plus a minimal `/health` + `/ready` HTTP server for k8s probes (`/ready` checks Kafka broker reachability)
- `auto.offset.reset: latest` - won't replay history on (re)start, only new events
- manual commit after processing — at-least-once delivery, not exactly-once
- stream to EmbeddingService opens on first event and stays open for the worker's lifetime. if EmbeddingService restarts, worker reconnects automatically once and retries

## Topics

Explicit list, no wildcard:
- `product.created` → build corpus -> embed via gRPC -> upsert vector + payload to Qdrant
- `product.updated` → same path as created, just overwrites the existing vector
- `product.deleted` → delete from Qdrant, no embedding needed

Event type comes from the **Kafka message header** (`EventType`), not the topic name

```
"name | description | category | key: value | key: value"
```

That string gets embedded and stored in Qdrant with a payload (price, color, brand, etc) for filtering

essentially it's consume kafka message like this

```
{
  "product_id": "prod-42",
  "name": "Logitech MX Keys",
  "description": "Wireless keyboard with backlit keys",
  "category": "Keyboards",
  "price": 109.99,
  "currency": "USD",
  "stock_quality": 50,
  "attributes": [
    { "key": "color", "value": "graphite" },
    { "key": "brand", "value": "logitech" },
    { "key": "layout", "value": "full-size" }
  ]
}
```

and return this
```
Logitech MX Keys | Wireless keyboard with backlit keys | Keyboards | color: graphite | brand: logitech | layout: full-size
```