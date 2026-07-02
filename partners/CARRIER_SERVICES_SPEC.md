# Fake Carrier Services — DPD & PPL

Fake implementations of two parcel carriers used by the Order service's `ShippingGatewayRouter`.
They are the shipping equivalent of `my-stripe`: no real parcels move, every outcome is
deterministic based on the request content, and both services can be driven to any saga
branch purely by what you put in the `orderId` or postal code.

---

## Language & Tech Stack

**Go 1.22, stdlib HTTP only, SQLite for persistence.**

Same as `my-stripe` (no external HTTP framework, no ORM) with one addition: SQLite via
`modernc.org/sqlite` (pure Go, no CGO, single `.db` file). The reason for persistence is
covered below.

```
partners/
  my-stripe/       ← in-memory, already exists
  my-dpd/          ← SQLite-backed, to be created
  my-ppl/          ← SQLite-backed, to be created
```

Dependencies (both services):

```
modernc.org/sqlite   — pure-Go SQLite driver, no CGO needed
```

No other external deps. Configuration, webhook delivery, and signal handling follow
the exact same patterns as `my-stripe`.

---

## Why Persistent (unlike my-stripe)

`my-stripe` is stateless across restarts because a payment intent settles in seconds.
These carriers model physical delivery — the return saga's `AwaitReturnShipmentStep` parks
the saga and waits for a webhook that fires after a configurable delay (seconds in tests,
hours/days in staging). If the fake service restarts during that window, all registered
webhooks and their pending timers would be lost. SQLite prevents that without adding any
infra dependency.

A single embedded `.db` file per service. Schema is minimal — three tables each:
`shipments`, `webhook_registrations`, `webhook_events`.

---

## DPD — Synchronous, Reliable, HMAC-signed

### Business Model Recap

DPD owns its depot network end-to-end. Every call gets an immediate authoritative answer.
Cancellation is always possible until a driver physically scans the label. Webhooks are
signed with HMAC-SHA256 (same scheme as `my-stripe`).

### Endpoints

| Method | Path | Description |
|--------|------|-------------|
| `POST` | `v1/shipment` | Create outbound shipment |
| `DELETE` | `v1/shipment/{id}` | Cancel shipment (always idempotent) |
| `GET` | `v1/shipment/{id}/status` | Poll shipment status |
| `POST` | `v1/webhook` | Register webhook for a shipment |
| `POST` | `v1/shipment/return` | Create return shipment |
| `DELETE` | `v1/shipment/return/{id}` | Cancel return shipment (always idempotent) |

### Algorithm — Create Shipment

```
POST v1/shipment  { orderId, recipient, parcels }

1. Run DecideOutcome(orderId, postalCode)
2. outcome == server_error  → 500  (simulates DPD API outage)
3. outcome == invalid_address → 400 (triggers InvalidAddressException in adapter)
4. Generate shipment_id ("dpd-shp-{uuid}"), tracking_number ("dpd-trk-{uuid}")
5. Persist shipment row: status = "created", finalize_at = now + ShipmentFinalizeDelay
6. Return 201 { shipmentId, trackingNumber }
```

The worker ticks every `WorkerInterval`. When `finalize_at` elapses it:
- Advances status: `created → in_transit → delivered`  (two ticks, configurable gap)
- On final transition fires all registered webhooks for that shipment

### Algorithm — Cancel Shipment

```
DELETE v1/shipment/{id}

1. Look up shipment
2. Not found → 204 (idempotent, adapter treats 404 as success too)
3. status == "in_transit" or "delivered" → 204 (DPD is always idempotent on cancel;
   the adapter treats any 2xx as success)
4. Mark status = "cancelled"
5. Return 204
```

DPD never returns an error on cancel. This is the "happy compensation" path.

### Algorithm — Register Webhook

```
POST v1/webhook  { shipmentId, callbackUrl, events[] }

1. Persist webhook_registration row
2. Return 200 { registered: true }
```

When the worker fires the webhook it builds the event body, signs it with HMAC-SHA256
using the same `Sign(secret, body, timestamp)` function from `my-stripe/internal/webhook/signer.go`
(copy or share), and sets `Stripe-Signature` → `t={ts},v1={hmac}` header. The Gateway
already validates this header for shipping webhooks.

### Algorithm — Return Shipment

```
POST v1/shipment/return  { orderId, customerId, parcels }

1. DecideReturnOutcome(orderId) — same magic token rules
2. Generate return_shipment_id ("dpd-ret-{uuid}"), return_tracking_number
3. Persist with status = "awaiting_pickup", finalize_at = now + ReturnFinalizeDelay
4. Return 201 { returnShipmentId, returnTrackingNumber, expectedPickupDate }
```

Worker fires single `return.delivered` webhook when timer elapses.

### Deterministic Outcomes

| Signal | Outcome |
|--------|---------|
| `orderId` contains `"fail"` | `400` invalid address (saga compensation) |
| `orderId` contains `"lost"` | shipment created, worker never delivers (saga timeout test) |
| `orderId` contains `"slow"` | `ShipmentFinalizeDelay` multiplied ×10 |
| `orderId` contains `"error5xx"` | `500` on create (saga retries step) |
| postal code ends in `01` | `400` invalid address |
| postal code ends in `05` | `500` server error |
| anything else | `201` success, normal delivery |

Same pattern for return: `orderId` contains `"returnfail"` → `400` on return create.

---

## PPL — Async Booking, Progressive Events, Plain-Secret Webhook, Cancel-Blocking

### Business Model Recap

PPL assigns routes through partner depots. The booking is a two-phase process: you submit
a request and poll until the depot accepts it. Once a parcel is scanned into the network
cancellation is refused. Returns require the customer to visit a ParcelShop; PPL fires
progressive status events as the parcel moves through their hubs.

### Endpoints

| Method | Path | Description |
|--------|------|-------------|
| `POST` | `api/v1/parcels` | Submit booking request → `202` + `referenceId` |
| `GET` | `api/v1/parcels/{referenceId}` | Poll booking status until `accepted` |
| `POST` | `api/v1/parcels/{id}/cancel` | Cancel — `409` if already `in_transit` or later |
| `GET` | `api/v1/parcels/tracking/{trackingNumber}` | Poll shipment status |
| `POST` | `api/v1/webhooks` | Register webhook for a shipment |
| `POST` | `api/v1/parcels/returns` | Create return → returns `qrToken` |
| `POST` | `api/v1/parcels/returns/{id}/cancel` | Cancel return |

### Algorithm — Create Shipment (Two-Phase)

```
POST api/v1/parcels  { orderId, address, packages }

1. DecideOutcome(orderId, postalCode)
2. outcome == server_error  → 500
3. outcome == invalid_address → 422 (triggers InvalidAddressException in adapter)
4. Generate reference_id ("ppl-ref-{uuid}")
5. Persist booking row: status = "pending",
                        accept_at = now + BookingAcceptDelay,
                        outcome = decided_outcome  (stored for later)
6. Return 202 { referenceId }
```

```
GET api/v1/parcels/{referenceId}

1. Look up booking by reference_id
2. Not found → 404
3. now < accept_at → return { referenceId, status: "pending" }
4. now >= accept_at AND outcome == poll_fail → return { referenceId, status: "rejected",
                                                         reason: "depot capacity exceeded" }
   (HTTP 200 with status field, adapter must check the field and throw)
5. now >= accept_at AND outcome == success →
      if parcel_id not yet assigned: generate parcel_id ("ppl-shp-{uuid}"),
                                     tracking_number ("ppl-trk-{uuid}"),
                                     update booking status = "accepted"
      return { referenceId, status: "accepted", parcelId, trackingNumber }
```

The **adapter** (`PplShippingAdapter.CreateShipmentAsync`) must poll this endpoint in a
retry loop with backoff. Magic token `"pollreject"` in `orderId` causes the booking to
settle as `"rejected"` instead of `"accepted"`, putting the saga into the
`InvalidAddressException` compensation path.

### Algorithm — Cancel Shipment

```
POST api/v1/parcels/{id}/cancel

1. Look up shipment
2. Not found → 404
3. status ∈ { "in_transit", "out_for_delivery", "delivered" } → 409
   body: { error: "cannot_cancel_in_transit", message: "Parcel is already in the PPL network" }
4. Mark status = "cancelled"
5. Return 200
```

This is the critical difference from DPD. The adapter already propagates the
`HttpRequestException` for non-2xx responses. The `CreateShipmentStep.CompensateAsync`
in the saga catches it, logs it as a non-retryable compensation failure, and calls
`incidentReporter.CreateInterventionTicketAsync` — the saga still completes compensation
for the other steps (inventory release, payment refund) but the shipment compensation
is marked as requiring manual intervention.

### Algorithm — Register Webhook

```
POST api/v1/webhooks  { shipmentId, callbackUrl, events[] }

1. Persist webhook_registration
2. Return 200 { registered: true }
```

PPL auth on outbound delivery: instead of HMAC, PPL sends a plain
`X-PPL-Webhook-Secret: {secret}` header. The Gateway's webhook handler verifies this
header value against `WebhookSecurity:PplSharedSecret`. The body is not signed.

PPL fires **three** webhook calls per delivery, in order, spaced by `EventSpacing` config:

1. `parcel.in_transit`  — parcel scanned at first hub
2. `parcel.out_for_delivery` — parcel on delivery vehicle
3. `parcel.delivered`  — delivered to recipient

The Gateway publishes `ReturnShipmentDeliveredEvent` to Kafka **only** on `parcel.delivered`.
The `AwaitReturnShipmentStep` only resumes on that event. Events 1 and 2 are validated,
logged, and discarded by the Gateway.

### Algorithm — Return Shipment

```
POST api/v1/parcels/returns  { orderId, customerId, packages }

1. DecideReturnOutcome(orderId)
2. Generate return_shipment_id ("ppl-ret-{uuid}"), qr_token ("ppl-qr-{uuid}")
3. Persist with status = "awaiting_scan",
              qr_scanned_at = NULL,
              scan_window_closes_at = now + QrScanWindowDuration (configurable; default 30s in tests)
4. Return 201 { returnShipmentId, returnTrackingNumber, expectedPickupDate, qrToken }
```

The worker does **not** fire the return webhook based on a fixed timer. Instead, it only
fires after the QR scan window has elapsed (simulating the customer eventually visiting a
ParcelShop). `QrScanWindowDuration` defaults to a short value in tests (`RETURN_QR_SCAN_WINDOW=5s`)
and a long value in staging (`2h`). This is what makes the `SagaWatchdogService` conflict
from `CODE_REVIEW.md §2.6` reproducible: set `RETURN_QR_SCAN_WINDOW` longer than the
watchdog's stuck threshold.

Progressive event sequence for return:

1. `return.in_transit` — QR scanned at ParcelShop, parcel moving
2. `return.out_for_delivery` — last mile to warehouse
3. `return.delivered` — arrived at merchant warehouse

Gateway fires Kafka only on `return.delivered`.

### Deterministic Outcomes

| Signal | Outcome |
|--------|---------|
| `orderId` contains `"fail"` | `422` invalid address on create |
| `orderId` contains `"pollreject"` | booking settles as `"rejected"` (poll outcome) |
| `orderId` contains `"cancelblock"` | parcel immediately advances to `in_transit` on create, so cancel → `409` |
| `orderId` contains `"error5xx"` | `500` on create |
| `orderId` contains `"returnfail"` | `422` on return create |
| `orderId` contains `"slowpoll"` | `BookingAcceptDelay` multiplied ×5 (adapter times out) |
| postal code ends in `01` | `422` invalid address |
| postal code ends in `05` | `500` server error |
| postal code ends in `09` | booking settles as `"rejected"` |
| anything else | normal two-phase success |

---

## Shared Internal Structure (both services)

```
my-dpd/  (or my-ppl/)
  cmd/
    main.go              — wiring: config, db, server, worker, graceful shutdown
  internal/
    config/
      config.go          — env-based config (same pattern as my-stripe)
    domain/
      domain.go          — DecideOutcome, magic token rules, ID generation
    store/
      store.go           — SQLite schema init + CRUD; mutex for concurrent access
      schema.sql         — embedded via go:embed
    api/
      server.go          — http.ServeMux wiring
      handlers.go        — request/response logic
      dto.go             — request/response structs
    webhook/
      worker.go          — ticker loop: advance states, fire due webhooks
      signer.go          — HMAC signing (DPD only); plain-secret header (PPL only)
  Dockerfile
  docker-compose.yml
```

### Config (representative — DPD)

| Env var | Default | Meaning |
|---------|---------|---------|
| `PORT` | `8091` | HTTP listen port |
| `API_KEY` | `dpd_sandbox_key` | Bearer token the Order service sends |
| `WEBHOOK_SECRET` | `dev_dpd_secret` | HMAC secret for outbound webhook signing |
| `DB_PATH` | `./dpd.db` | SQLite file path |
| `WORKER_INTERVAL` | `1s` | Background loop tick |
| `SHIPMENT_FINALIZE_DELAY` | `5s` | Time from create to first status advance |
| `RETURN_FINALIZE_DELAY` | `10s` | Time from return create to `return.delivered` |

PPL adds:

| Env var | Default | Meaning |
|---------|---------|---------|
| `PORT` | `8092` | HTTP listen port |
| `BOOKING_ACCEPT_DELAY` | `3s` | How long the `pending` poll phase lasts |
| `EVENT_SPACING` | `2s` | Gap between progressive webhook events |
| `RETURN_QR_SCAN_WINDOW` | `5s` | Delay before return webhook chain starts |

---

## Integration with the Saga

### Order service config changes needed

`DpdApiOptions` and `PplApiOptions` in `appsettings.json` already exist. Point them at the
fake services:

```json
"Dpd": { "BaseUrl": "http://localhost:8091/", "ApiKey": "dpd_sandbox_key" },
"Ppl": { "BaseUrl": "http://localhost:8092/", "ApiKey": "ppl_sandbox_key" }
```

### Gateway config changes needed

Two new keys in `WebhookSecurity`:

```json
"WebhookSecurity": {
  "ShippingSharedSecret": "dev_dpd_secret",
  "PplSharedSecret":      "dev_ppl_secret"
}
```

The Gateway webhook handler at `POST /api/v1/webhooks/shipping/returns/delivered` must
branch on a carrier identifier in the payload (or a separate route per carrier) to choose
between HMAC validation (DPD) and plain-header validation (PPL).

### PplShippingAdapter changes needed

`CreateShipmentAsync` must be extended to poll after the initial `202`:

```csharp
// pseudocode
var ref = await PostParcelAsync(request);       // → referenceId
for (int attempt = 0; attempt < MaxPolls; attempt++)
{
    await Task.Delay(PollInterval, ct);
    var result = await GetParcelAsync(ref, ct); // → status + ids
    if (result.Status == "accepted") return new ShipmentResultDto(result.ParcelId, result.TrackingNumber);
    if (result.Status == "rejected") throw new InvalidAddressException(result.Reason);
}
throw new TimeoutException("PPL booking did not accept within the polling window");
```

`MaxPolls` and `PollInterval` come from `PplApiOptions`.

---

## How to Test Each Saga Branch

| Saga branch to test | Magic value to use |
|---|---|
| Happy path (DPD) | any normal `orderId` with DPD carrier |
| Happy path (PPL) | any normal `orderId` with PPL carrier |
| Invalid address → compensation | `orderId = "fail-order-..."` |
| PPL poll timeout → saga retries step | `orderId = "slowpoll-..."` + short `MaxPolls` in test |
| PPL booking rejected → `InvalidAddressException` | `orderId = "pollreject-..."` |
| DPD cancel succeeds during compensation | any DPD shipment, cancel before `SHIPMENT_FINALIZE_DELAY` |
| PPL cancel blocked → incident ticket | `orderId = "cancelblock-..."`, then trigger compensation |
| Return saga happy path (DPD) | any normal return, wait for `RETURN_FINALIZE_DELAY` |
| Return saga happy path (PPL) | any normal return, wait for `RETURN_QR_SCAN_WINDOW` |
| PPL intermediate events discarded by Gateway | any PPL return; verify only `return.delivered` resumes saga |
| Watchdog vs AwaitReturnShipment conflict | PPL return with `RETURN_QR_SCAN_WINDOW` > watchdog `_stuckThreshold` |
| DPD parcel lost → saga timeout | `orderId = "lost-..."` with DPD; webhook never fires |
