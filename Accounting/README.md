# Accounting Service

Standalone, event-driven double-entry ledger service — the single append-only source of monetary
truth. Payment owns *state* ("this intent is `Succeeded`"); Accounting owns *money* ("this much
moved, on these accounts, at this time").

## Why it is a separate service

- **Money truth is one artifact, not a reconstruction.** "Was this order refunded?" used to mean
  joining Payment rows, saga context and callbacks and hoping they agreed.
- **An aggregate invariant catches what per-entity workers cannot.** Payment's and Order's
  reconciliation converge one stuck row at a time and never compare totals — which is why a
  double-refund made of two individually-balanced postings looked correct to all of them.
- **FX and tax need their own reference data** (rate feeds, jurisdiction tables) and read models.
  Bolting those onto Payment's hot write DB would be the wrong bounded context.
- **The saga never blocks on it.** Payment writes the money-event in the same transaction as the
  mutation; the ledger is downstream of that outbox. If Accounting is down, events buffer and drain.

## Layout

- `Api` / `Application` / `Domain` / `Infrastructure` / `Protos` + four unit-test projects.
- Own Postgres (`accounting-postgres`, database `accounting_db`) with EF Core **migrations**
  (applied on startup via `Database.MigrateAsync()`, not `EnsureCreated`).
- Two gRPC surfaces: [`accounting.proto`](Accounting/Protos/protos/accounting.proto) for Order
  (`RecordRefund`, `ReverseRevenue`, `CancelReversal`) and
  [`admin_ops.proto`](Accounting/Protos/protos/admin_ops.proto) for OpsConsole.
- gRPC health checks + k8s manifests (`k8s/accounting-service.yaml`, `k8s/accounting-postgres.yaml`).

## Domain model (primitive ledger)

Every posting is a balanced `LedgerTransaction` of `LedgerEntry` legs where `Σdebits = Σcredits`
per currency (enforced in the aggregate). Postings are append-only; corrections are new reversing
transactions. `transaction_ref` is a UNIQUE natural key making every post idempotent under
at-least-once delivery.

| gRPC op | Debit | Credit |
|---|---|---|
| `RecordRefund` | `refunds_payable` | `customer_captured` |
| `ReverseRevenue` | `merchant_revenue` | `refunds_payable` |
| `CancelReversal` | reversing entry (swaps the original reversal's legs) | |

`RecordRefund` has **no caller in Order** — the refund cash leg belongs to Payment, which emits
`RefundIssuedEvent`. The rpc stays as a backfill and manual-correction surface only. Both paths derive
`refund:{refundId}` from the same Payment refund id, so they converge on one posting if both ever run.

`transaction_ref` is derived from the caller's business identifier, never from the amount:
`refund:{refundId}`, `reversal:{returnRequestId}`, `cancel-reversal:{reversalId}`. An order can be
returned more than once, so `ReverseRevenue` requires `return_request_id` — keying on
`(order_id, amount, currency)` made a second return of the same amount look like a retry and
silently dropped it from the ledger.

## Money-event ingestion (Phase 2)

`MoneyEventsConsumerService` reads `Kafka:MoneyEventsTopic` (`payment.money-events`) and turns each
Payment money-event into one balanced posting.

| Event | `transaction_ref` | Debit | Credit |
|---|---|---|---|
| `PaymentAuthorizedEvent` | `authorize:{paymentId}` | `customer_authorized` | `authorization_hold` |
| `PaymentVoidedEvent` | `void:{paymentId}` | `authorization_hold` | `customer_authorized` |
| `PaymentCapturedEvent` | `capture:{paymentId}` | `customer_captured` (gross − fee) + `gateway_fees` | `merchant_revenue` (gross − tax) + `tax_payable` |
| `RefundIssuedEvent` | `refund:{refundId}` | `refunds_payable` | `customer_captured` |

Idempotency is two-layered. `processed_events` has the Kafka `event_id` as its **primary key**, and
`ledger_transactions.transaction_ref` is UNIQUE. The posting and the processed marker commit in a
single `SaveChangesAsync`, so an event can never be marked done without its posting.

The consumer commits offsets manually. A transient failure seeks back to the same offset; after
`MoneyEventConsumer:MaxOffsetRetries` it commits past the message rather than stall the partition for
every other payment, and logs `Critical` — the resulting drift is what the Phase 3 reconciliation
worker exists to catch. Unparseable or rejected messages are treated as poison and skipped.

Config: `Kafka:BootstrapServers`, `Kafka:MoneyEventsTopic`, and `MoneyEventConsumer:*`
(`Enabled`, `ConsumerGroupId`, `MaxOffsetRetries`, `RetryDelaySeconds`).

## Reconciliation (Phase 3)

`ReconcileLedgerWorker` runs `ReconcileLedgerCommand` on a schedule and pages Telegram on any finding.
Three checks, all aggregated in SQL:

1. **Per currency** — `Σdebits = Σcredits`.
2. **Per transaction** — each posting balanced. The factories cannot produce an unbalanced one, so a
   hit here means a direct DB write or a migration slip.
3. **Per order and currency** — `customer_captured` credits exceeding debits, i.e. more refunded than
   captured.

Check 3 exists because checks 1 and 2 **cannot** catch a double refund: two refunds are each
individually balanced, so the totals still tie out. It is grouped by order rather than payment,
because the gRPC `RecordRefund` path posts without a payment id.

The handler also reports money events the consumer committed past without posting, and consumer lag
above `LedgerReconciliation:MaxAcceptableLag`.

Alerting is Accounting's own: `IIncidentReporter` / `TelegramIncidentReporter` with a separate bot and
chat under `IncidentReporter:Telegram`, pointed at a finance channel. It never calls Order's reporter.
Leave `Enabled` false and drift is logged `Critical` but not delivered.

Config: `LedgerReconciliation:*` (`Enabled`, `IntervalMinutes`, `StartupDelaySeconds`, `MaxFindings`,
`MaxAcceptableLag`).

## FX + reporting layer (Phase 5)

`ReportingProjectionWorker` converts every primitive entry into `Reporting:ReportingCurrency` and
writes `ledger_reporting_entries`. The projection is **derived and rebuildable**: nothing in it ever
feeds back into `ledger_entries`.

Rules:

- **One rate per transaction**, taken from `fx_rates` as effective at `occurred_at` (never a future
  rate). Same-currency needs no row and uses 1.0 exactly.
- **Balance survives conversion by construction**, because every leg uses the same rate. Only per-leg
  rounding can break it, and that residue becomes an explicit `fx_gain_loss` leg with a null
  `entry_id`.
- **No rate means no projection.** The transaction stays queued rather than being booked at a guess.

Rate ingestion is `Reporting:SeedRates` in config — the placeholder where a real feed plugs into
`IFxRateRepository`.

Read API (`admin_ops.proto`, internal-only):

| rpc | Purpose |
|---|---|
| `GetTrialBalance` | per-account balances in transaction *and* reporting currency, totals, `is_balanced` |
| `GetOrderMoneyTrail` | every posting for an order, authorize → capture → refund → reversal, in order |
| `GetLedgerHealth` | runs the reconciliation checks **without** paging Telegram |
| `PostAdjustingEntry` | the only mutation: appends a balanced correcting transaction |

`PostAdjustingEntry` requires a `reason` and a `posted_by`, both persisted on `ledger_transactions`.
`adjustment_id` is the idempotency key. There is deliberately no edit or delete rpc — corrections are
new reversing transactions.

**Two callers, two keys.** `ApiKeyAuthInterceptor` picks the expected key by service name:
`AdminAccountingService` accepts `InternalServices:OpsConsoleApiKey`, while the money-posting
`AccountingService` requires `InternalServices:AccountingApiKey`. One shared key would let the console
call `ReverseRevenue`.

**Tax is not populated.** `ForCapture` splits `merchant_revenue` / `tax_payable` whenever `tax > 0`
and the money event carries the field, but nothing upstream computes tax, so it is always zero.

Config: `Reporting:*` (`Enabled`, `ReportingCurrency`, `IntervalMinutes`, `StartupDelaySeconds`,
`BatchSize`, `SeedRates`).

OpsConsole consumes this surface at `/ledger`.

## Run locally

```bash
# shared infra network + this service's Postgres
docker compose -f ../docker-compose.infra.yml up -d
docker compose up -d
dotnet run --project Accounting/Api/Api.csproj
```

Postgres listens on host port **5441** (Order's write DB already owns 5437).

## Migrations

```bash
dotnet ef migrations add <Name> \
  --project Accounting/Infrastructure/Infrastructure.csproj \
  --startup-project Accounting/Api/Api.csproj
```
