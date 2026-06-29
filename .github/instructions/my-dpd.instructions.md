---
applyTo: "partners/my-dpd/**"
description: "Use when working on the fake DPD carrier service — a deterministic Go + SQLite simulator of the DPD parcel API: synchronous reliable shipment/return booking, idempotent cancel, and HMAC-signed delivery webhooks driving the Order return saga."
---

# Fake DPD Carrier Service (my-dpd)

## Overview

Deterministic fake of the DPD parcel carrier, consumed by the Order service's `ShippingGatewayRouter` / `DpdShippingAdapter`. Shipping equivalent of `partners/my-stripe`: no real parcels move, every outcome is a pure function of the request (`orderId` magic tokens + postal-code suffix), so saga branches are fully reproducible. Spec: `partners/CARRIER_SERVICES_SPEC.md`.

DPD models a carrier that owns its depot network end-to-end: every call returns an immediate authoritative answer, cancellation is always possible (idempotent), and webhooks are HMAC-signed with the same scheme as `my-stripe`.

## Architecture

- **cmd/main.go** — wiring: config → store → server → worker → graceful shutdown
- **internal/config/config.go** — env-based config (`getEnv`/`getEnvInt`/`getEnvDuration` helpers)
- **internal/domain/domain.go** — `DecideOutcome` magic-token rules + ID generation (`NewID(prefix)` → `prefix-{uuidv4}`)
- **internal/store/store.go** + **schema.sql** (`//go:embed`) — SQLite persistence, CRUD, `AdvanceDueEvents`
- **internal/api/** — `server.go` (ServeMux + auth/recover/log middleware), `handlers.go`, `dto.go`
- **internal/webhook/** — `worker.go` (ticker loop), `signer.go` (HMAC `Sign`)

## Tech Stack

- **Go 1.22**, stdlib HTTP only — `http.ServeMux` with method+path patterns and `{id}` path values. No web framework, no ORM.
- **modernc.org/sqlite v1.33.1** — pure-Go SQLite driver (CGO off, driver name `"sqlite"`). Single embedded `.db` file.
- Persistence exists (unlike in-memory `my-stripe`) because the return saga parks for a webhook that may fire after a long delay; a restart must not lose registered webhooks or pending timers.

## Endpoints (Bearer `API_KEY`)

| Method | Path | Description |
|--------|------|-------------|
| POST | `v1/shipment` | Create outbound shipment → `201 { shipmentId, trackingNumber }` |
| DELETE | `v1/shipment/{id}` | Cancel shipment (always `204`, idempotent) |
| GET | `v1/shipment/{id}/status` | Poll shipment status |
| POST | `v1/webhook` | Register webhook `{ shipmentId, callbackUrl, events[] }` |
| POST | `v1/shipment/return` | Create return → `201 { returnShipmentId, returnTrackingNumber, expectedPickupDate }` |
| DELETE | `v1/shipment/return/{id}` | Cancel return (idempotent) |
| GET | `/healthz` | Liveness `{ carrier:"dpd", status:"ok", test_mode:true }` |

## Behavior

- **Synchronous + authoritative** — create returns the final create-time outcome immediately.
- **Worker** ticks every `WORKER_INTERVAL`: when `finalize_at` elapses, advances `created → in_transit → delivered` (gap `STATUS_ADVANCE_GAP`), then fires registered webhooks on final transition.
- **DPD only sends events the registration subscribed to** (e.g. a return registered for `["return.delivered"]` receives just that). Contrast with PPL, which sends all progressive events.
- **Cancel is always idempotent** — not-found or already-advanced still returns `204`; the adapter treats any `2xx`/`404` as success (the "happy compensation" path).
- Error body shape: `{ error_code, error_message }`.

## Webhook Auth (HMAC)

- Header `Stripe-Signature: t={ts},v1={hmac}` (ts in Unix **seconds**), signed payload `{ts}.{rawBody}`, HMAC-SHA256 keyed by `WEBHOOK_SECRET`.
- Envelope: `{ id, object:"event", type, carrier:"dpd", test_mode, data }`. The `carrier:"dpd"` tag lets the Gateway pick HMAC validation over PPL's plain-secret check.
- Verified by the Gateway against `WebhookSecurity:ShippingSharedSecret`.

## IDs

`dpd-shp-`, `dpd-trk-`, `dpd-ret-`, `dpd-rtrk-`, `dpd-evt-`, `dpd-whk-` + uuidv4.

## Deterministic Outcomes

| Signal | Outcome |
|--------|---------|
| `orderId` contains `error5xx` | `500` on create (saga retries step) |
| `orderId` contains `fail` (not `returnfail`) | `400` invalid_address → `InvalidAddressException` |
| `orderId` contains `lost` | created but worker never delivers (saga timeout test) |
| `orderId` contains `slow` | `SHIPMENT_FINALIZE_DELAY` ×10 |
| `orderId` contains `returnfail` | `400` on **return create** only |
| postal code ends `01` | `400` invalid_address |
| postal code ends `05` | `500` server error |
| anything else | `201` success, normal delivery |

> `orderId` is a `Guid` in the real adapter signatures, so the `orderId` magic tokens are only reachable when driving the HTTP API directly. For end-to-end saga control through the adapter, use the **postal-code suffixes**.

## Persistence Conventions

- Timestamps stored as INTEGER unix-**millis**; `NULL` `finalize_at` = no timer scheduled (the `lost` case).
- `db.SetMaxOpenConns(1)` + a `sync.Mutex`, `PRAGMA busy_timeout=5000`.
- Tables: `shipments`, `webhook_registrations`, `webhook_events`.

## Config (env)

`PORT`=8091, `API_KEY`=dpd_sandbox_key, `WEBHOOK_SECRET`=dev_dpd_secret, `DB_PATH`=./dpd.db, `WORKER_INTERVAL`=1s, `SHIPMENT_FINALIZE_DELAY`=5s, `RETURN_FINALIZE_DELAY`=10s, `STATUS_ADVANCE_GAP`=2s. Docker uses `host.docker.internal` to reach callback URLs.

## Key Rules

- **Determinism is the contract** — outcomes must stay pure functions of the request; never add randomness or wall-clock branching beyond the configured delays.
- **Cancel must never error** — DPD is the reliable carrier; idempotent `204` on every cancel path.
- **Webhook bytes are signed exactly** — the Gateway recomputes HMAC over `{ts}.{rawBody}`; don't reformat the body after signing.
- **GOTCHA — duplicate `package` line:** after editing any Go file, a language-server quick-fix sometimes prepends a second `package X` line, breaking the build (`expected declaration, found 'package'`). Always run `gofmt -l . && go build ./...` after edits and delete the duplicate first line if a file shows two `^package ` lines.
- Keep parity with `my-ppl` patterns where shared (config, signer, graceful shutdown); only diverge where the carrier model genuinely differs.
