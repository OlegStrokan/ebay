-- PPL fake carrier persistence. Times are stored as INTEGER unix-millis so NULL can
-- unambiguously mean "no timer scheduled". One shipments table covers both the
-- outbound two-phase booking lifecycle and the return QR-scan lifecycle.

CREATE TABLE IF NOT EXISTS shipments (
    id                   TEXT PRIMARY KEY,    -- reference_id (outbound) | return_shipment_id (return)
    kind                 TEXT    NOT NULL,    -- 'outbound' | 'return'
    order_id             TEXT    NOT NULL,
    customer_id          TEXT,
    postal_code          TEXT,
    parcel_id            TEXT,                -- assigned when an outbound booking is accepted
    tracking_number      TEXT,               -- assigned at accept (outbound) / create (return)
    qr_token             TEXT,               -- return only
    poll_outcome         TEXT,               -- success | poll_fail (decided at create, applied at poll)
    accept_at            INTEGER,            -- when a pending outbound booking resolves
    status               TEXT    NOT NULL,    -- pending|accepted|in_transit|out_for_delivery|delivered|cancelled|rejected|awaiting_scan
    next_event_at        INTEGER,            -- when the worker fires the next progressive event (NULL = none)
    expected_pickup_date INTEGER,
    cancel_block         INTEGER NOT NULL DEFAULT 0,
    created_at           INTEGER NOT NULL,
    updated_at           INTEGER NOT NULL
);

CREATE TABLE IF NOT EXISTS webhook_registrations (
    id           TEXT PRIMARY KEY,
    shipment_id  TEXT    NOT NULL,            -- parcel_id (outbound) | return_shipment_id (return)
    callback_url TEXT    NOT NULL,
    events       TEXT    NOT NULL,            -- JSON array of subscribed event types
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

CREATE INDEX IF NOT EXISTS idx_shipments_next_event ON shipments (next_event_at);
CREATE INDEX IF NOT EXISTS idx_shipments_parcel ON shipments (parcel_id);
CREATE INDEX IF NOT EXISTS idx_shipments_tracking ON shipments (tracking_number);
CREATE INDEX IF NOT EXISTS idx_webhook_reg_shipment ON webhook_registrations (shipment_id);
CREATE INDEX IF NOT EXISTS idx_webhook_events_pending ON webhook_events (delivered, dead, next_attempt_at);
