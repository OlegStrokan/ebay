---
applyTo: "partners/my-ppl/**"
description: "Use when working on the fake PPL carrier service — a deterministic Go + SQLite simulator of the PPL parcel API: two-phase async booking with polling, cancel-blocking once in transit, progressive plain-secret delivery webhooks, and QR-scan-gated returns driving the Order return saga."
---

# Fake PPL Carrier Service (my-ppl)

## Overview

Deterministic fake of the PPL parcel carrier, consumed by the Order service's `ShippingGatewayRouter` / `PplShippingAdapter`. Shipping equivalent of `partners/my-stripe`: no real parcels move, every outcome is a pure function of the request (`orderId` magic tokens + postal-code suffix). Spec: `partners/CARRIER_SERVICES_SPEC.md`.

PPL models a carrier that routes through partner depots: booking is **two-phase** (submit, then poll until accepted), cancellation is **refused once the parcel is scanned into the network**, returns require a customer ParcelShop scan, and PPL fires **progressive** status events. Auth on outbound webhooks is a **plain shared-secret header**, not HMAC — the key contrast with `my-dpd`.

## Architecture

- **cmd/main.go** — wiring: config → store → server → worker → graceful shutdown
- **internal/config/config.go** — env-based config (`getEnv`/`getEnvInt`/`getEnvDuration`)
- **internal/domain/domain.go** — `DecideCreateOutcome` / `DecidePollOutcome` / `DecideReturnOutcome`, `IsCancelBlock`, `IsSlowPoll`, `NewID(prefix)`
- **internal/store/store.go** + **schema.sql** (`//go:embed`) — SQLite persistence, `AdvanceDueEvents(now, spacing)` → `[]Transition`, `nextStage(kind, current)`
- **internal/api/** — `server.go` (ServeMux + Bearer auth/recover/log), `handlers.go`, `dto.go`
- **internal/webhook/** — `worker.go` (ticker loop, enqueues all progressive events), `signer.go` (`ApplyAuth` sets the plain header)

## Tech Stack

- **Go 1.22**, stdlib HTTP only — `http.ServeMux` with method+path patterns and `{id}`/`{referenceId}`/`{trackingNumber}` path values. No framework, no ORM.
- **modernc.org/sqlite v1.33.1** — pure-Go SQLite (CGO off, driver `"sqlite"`).
- Persistence exists because the return saga parks for a webhook that fires only after the QR scan window elapses; a restart must not drop registered webhooks or pending timers.

## Endpoints (Bearer `API_KEY`)

| Method | Path | Description |
|--------|------|-------------|
| POST | `api/v1/parcels` | Submit booking → `202 { referenceId, status:"pending" }` |
| GET | `api/v1/parcels/{referenceId}` | Poll booking until `accepted`/`rejected` |
| POST | `api/v1/parcels/{id}/cancel` | Cancel — `409` once `in_transit`+ |
| GET | `api/v1/parcels/tracking/{trackingNumber}` | Poll shipment status |
| POST | `api/v1/webhooks` | Register webhook `{ shipmentId, callbackUrl, events[] }` |
| POST | `api/v1/parcels/returns` | Create return → `201 { returnShipmentId, returnTrackingNumber, expectedPickupDate, qrToken }` |
| POST | `api/v1/parcels/returns/{id}/cancel` | Cancel return |
| GET | `/healthz` | `{ carrier:"ppl", status:"ok", test_mode:true }` |

## Behavior

- **Two-phase booking** — `POST api/v1/parcels` persists `status=pending`, `accept_at=now+BOOKING_ACCEPT_DELAY`, and the decided outcome; returns `202 + referenceId`. The adapter then polls `GET api/v1/parcels/{referenceId}`: before `accept_at` → `pending`; after → `accepted` (assigns `parcelId`/`trackingNumber`) or `rejected` (HTTP 200 with `status:"rejected"`, `reason` — the adapter must inspect the field and throw).
- **Cancel-blocking** — `409 { error:"cannot_cancel_in_transit", ... }` once `in_transit`/`out_for_delivery`/`delivered`. The adapter propagates the non-2xx; the saga's `CreateShipmentStep.CompensateAsync` logs it as a non-retryable compensation failure and raises an intervention ticket while still compensating the other steps.
- **Progressive events** spaced by `EVENT_SPACING`: outbound `parcel.in_transit → parcel.out_for_delivery → parcel.delivered`; return `return.in_transit → return.out_for_delivery → return.delivered`.
- **Worker sends ALL progressive events to every registration** (no event filter), so the Gateway receives and 202-discards the intermediates and resumes the saga only on the terminal `*.delivered`. (DPD, by contrast, only sends subscribed events.)
- **Returns are QR-gated** — created `awaiting_scan` with `next_event_at = now + RETURN_QR_SCAN_WINDOW`; the chain only starts after that window (simulating the customer visiting a ParcelShop). Setting the window longer than the saga watchdog's stuck threshold reproduces the `CODE_REVIEW.md §2.6` conflict.
- Error body shape: `{ error, message }` (PPL), **not** DPD's `{ error_code, error_message }`.

## Webhook Auth (plain secret)

- `signer.ApplyAuth` sets header `X-PPL-Webhook-Secret: {secret}`. The body is **not** signed.
- Envelope: `{ id, object:"event", type, carrier:"ppl", test_mode, data }`. The `carrier:"ppl"` tag routes the Gateway to plain-secret validation against `WebhookSecurity:PplSharedSecret`.

## IDs

`ppl-ref-`, `ppl-shp-`, `ppl-trk-`, `ppl-ret-`, `ppl-rtrk-`, `ppl-qr-`, `ppl-evt-`, `ppl-whk-` + uuidv4.

## Deterministic Outcomes

| Signal | Outcome |
|--------|---------|
| `orderId` contains `error5xx` | `500` on create |
| `orderId` contains `fail` (not `returnfail`) | `422` invalid_address → `InvalidAddressException` |
| `orderId` contains `pollreject` | booking settles `rejected` on poll |
| `orderId` contains `cancelblock` | advances to `in_transit` on accept, so cancel → `409` |
| `orderId` contains `slowpoll` | `BOOKING_ACCEPT_DELAY` ×5 (adapter poll window times out) |
| `orderId` contains `returnfail` | `422` on **return create** only |
| postal code ends `01` | `422` invalid_address |
| postal code ends `05` | `500` server error |
| postal code ends `09` | booking settles `rejected` on poll |
| anything else | normal two-phase success |

> `orderId` is a `Guid` in the real adapter signatures, so `orderId` magic tokens are only reachable when calling the HTTP API directly. For end-to-end saga control through the adapter, use the **postal-code suffixes** (`01`/`05`/`09`).

## Persistence Conventions

- Timestamps as INTEGER unix-**millis**; `NULL` `next_event_at` = no timer scheduled.
- `db.SetMaxOpenConns(1)` + `sync.Mutex`, `PRAGMA busy_timeout=5000`.
- Tables: `shipments` (PK = reference_id/return_shipment_id), `webhook_registrations`, `webhook_events`. Indexes on `next_event_at`, `parcel_id`, `tracking_number`.

## Config (env)

`PORT`=8092, `API_KEY`=ppl_sandbox_key, `WEBHOOK_SECRET`=dev_ppl_secret, `DB_PATH`=./ppl.db, `WORKER_INTERVAL`=1s, `BOOKING_ACCEPT_DELAY`=3s, `EVENT_SPACING`=2s, `RETURN_QR_SCAN_WINDOW`=5s, `WEBHOOK_MAX_ATTEMPTS`=8, `WEBHOOK_BASE_DELAY`=2s. Docker uses `host.docker.internal` for callback URLs.

## Key Rules

- **Determinism is the contract** — outcomes must stay pure functions of the request.
- **Two-phase is intentional** — never collapse booking into a single synchronous response; the adapter depends on the `202` → poll → `accepted`/`rejected` shape.
- **Send every progressive event** — the "Gateway discards intermediates" behavior depends on the worker NOT filtering by the registration's `events[]`. Do not re-add an event filter.
- **Plain secret, not HMAC** — keep `X-PPL-Webhook-Secret`; do not sign the body.
- **GOTCHA — duplicate `package` line:** after editing any Go file, a language-server quick-fix sometimes prepends a second `package X` line, breaking the build (`expected declaration, found 'package'`). Always run `gofmt -l . && go build ./...` after edits and delete the duplicate first line if a file shows two `^package ` lines (hit `store.go` and `signer.go` repeatedly).
