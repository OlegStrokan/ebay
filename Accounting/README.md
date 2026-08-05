# Accounting Service

Standalone, event-driven double-entry ledger service — the single append-only source of monetary
truth. See [`LEDGER_IMPLEMENTATION_PLAN.md`](../LEDGER_IMPLEMENTATION_PLAN.md).

**Phase 0 (bootstrap) is implemented here:**

- Solution mirroring the other services (`Api` / `Application` / `Domain` / `Infrastructure` /
  `Protos` + unit tests).
- Own Postgres (`accounting-postgres`, database `accounting_db`) with EF Core **migrations**
  (applied on startup via `Database.MigrateAsync()`, not `EnsureCreated`).
- gRPC server behind the existing [`accounting.proto`](Accounting/Protos/protos/accounting.proto)
  contract (`RecordRefund`, `ReverseRevenue`, `CancelReversal`) so the Order return saga's
  `AccountingGateway` finally has a real server.
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

Later phases add the Kafka money-event consumer, reconciliation worker, FX/tax reporting, and the
OpsConsole read views.

## Run locally

```bash
# shared infra network + this service's Postgres
docker compose -f ../docker-compose.infra.yml up -d
docker compose up -d
dotnet run --project Accounting/Api/Api.csproj
```

## Migrations

```bash
dotnet ef migrations add <Name> \
  --project Accounting/Infrastructure/Infrastructure.csproj \
  --startup-project Accounting/Api/Api.csproj
```
