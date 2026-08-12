# User Preference Worker

consumes user behavioral events from Kafka, builds per-user preference profiles, stores them in Redis.

this is the "memory" part of personalization. it remembers what users do - view, click, buy - and turns that into a preference profile that search can read later to rerank results.

no AI, no vectors, no fancy stuff. just event aggregation with weighted scoring and time decay.

## how it works

frontend tracks user actions -> Gateway publishes to `user.events` Kafka topic -> this worker consumes and aggregates

events tracked:
- **product viewed** — base weight 1.0, boosted up to 2x if user stared at it for >10 seconds
- **product clicked** (from search results) — weight 2.0, boosted 1.5x if user scrolled past top 5 to click it (stronger intent)
- **purchase completed** — weight 5.0, strongest signal
- **search bounced** — user searched and left without clicking. negative signal, tracked but not used for reranking yet

every interaction is stored in a Redis list per user (capped at 200). on each new event, the full profile gets recomputed with exponential time decay (14-day half-life, so recent actions matter way more than stuff from 3 weeks ago).

## preference profile

the output is a JSON object per user stored in Redis:
- **top categories** — weighted scores (e.g., `{"Electronics": 12.5, "Audio": 8.3}`)
- **top brands** — same idea
- **price range** — p25/p75 percentiles of prices the user interacted with (their "comfort zone")
- **condition weights** — New vs Used vs Refurbished preference
- **interaction count**

profiles expire after 30 days of inactivity.

## what uses this

not wired yet, but the plan: AiSearchService reads the Redis profile at search time and uses it to rerank results. so two users searching "headphones" get different ordering based on their history.

## Redis keys

- `user:{user_id}:interactions` — list of last N interactions (JSON objects)
- `user:{user_id}:preference_profile` — computed profile (JSON, 30-day TTL)

## health

Minimal `/health` + `/ready` HTTP server (stdlib only, no web framework) on `health_port` (default 8080). `/ready` checks Kafka broker reachability via `AdminClient.list_topics`; `/health` is a plain liveness check.