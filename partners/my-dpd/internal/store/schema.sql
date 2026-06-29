-- DPD fake carrier persistence. Times are stored as INTEGER unix-millis so NULL can
-- unambiguously mean "no timer scheduled" (used by the "lost" magic token).

CREATE TABLE IF NOT EXISTS shipments (
    id                   TEXT PRIMARY KEY,
    kind                 TEXT    NOT NULL,           -- 'outbound' | 'return'
    order_id             TEXT    NOT NULL,
    customer_id          TEXT,
    tracking_number      TEXT    NOT NULL,
    postal_code          TEXT,
    status               TEXT    NOT NULL,           -- created | in_transit | delivered | cancelled | awaiting_pickup
    finalize_at          INTEGER,                    -- next status-advance time (NULL = never advances)
    expected_pickup_date INTEGER,
    created_at           INTEGER NOT NULL,
    updated_at           INTEGER NOT NULL
);

CREATE TABLE IF NOT EXISTS webhook_registrations (
    id           TEXT PRIMARY KEY,
    shipment_id  TEXT    NOT NULL,
    callback_url TEXT    NOT NULL,
    events       TEXT    NOT NULL,                   -- JSON array of subscribed event types
    created_at   INTEGER NOT NULL
);

CREATE TABLE IF NOT EXISTS webhook_events (
    id              TEXT PRIMARY KEY,
    shipment_id     TEXT    NOT NULL,
    event_type      TEXT    NOT NULL,
    callback_url    TEXT    NOT NULL,
    body            BLOB    NOT NULL,
    attempts        INTEGER NOT NULL DEFAULT 0,
    next_attempt_at INTEGER NOT NULL,
    delivered       INTEGER NOT NULL DEFAULT 0,
    dead            INTEGER NOT NULL DEFAULT 0,
    created_at      INTEGER NOT NULL
);

CREATE INDEX IF NOT EXISTS idx_shipments_finalize ON shipments (finalize_at);
CREATE INDEX IF NOT EXISTS idx_webhook_reg_shipment ON webhook_registrations (shipment_id);
CREATE INDEX IF NOT EXISTS idx_webhook_events_pending ON webhook_events (delivered, dead, next_attempt_at);
