# Ops Console

Internal admin dashboard for operators: monitor sagas, inspect dead-lettered messages, and trigger recovery actions (force-compensate a stuck saga, retry a failed compensation, requeue a poison message) across Order, Payment, and Inventory. Never routed through the Gateway, never exposed to end users.

Two parts, deployed independently:

- **`OpsConsole/`** — .NET 8 Minimal API. Proxies to Order's `AdminOpsService`, Payment's `AdminPaymentService`, and Inventory's `AdminInventoryService` gRPC surfaces. No database.
- **`OpsConsole/web/`** — Next.js 16 (App Router) operator UI. The only part operators open in a browser; the backend is never called from client-side JS.

## Authentication (three separate secrets — don't confuse them)

| Secret | Between | Config key |
|---|---|---|
| `X-Admin-Api-Key` | Next.js backend → .NET backend | `AdminApiKey` / `OPS_CONSOLE_ADMIN_API_KEY` |
| Operator JWT | Browser session → .NET backend | `Jwt:SecretKey` + `Jwt:Audience` (same signing key as Auth/Gateway) |
| `x-internal-api-key` | .NET backend → Order/Payment/Inventory | `InternalServices:OpsConsoleApiKey` |

Reads require the `OpsViewer` policy (Admin/SuperAdmin/OpsViewer role); mutations (compensate, retry-compensation, requeue) require `OpsAdmin` (Admin/SuperAdmin only). If `InternalServices:OpsConsoleApiKey` doesn't match on both sides, every admin RPC to Order/Payment/Inventory fails closed with `PermissionDenied` — it never falls back to "allow".

## Running locally

**Backend:**

```bash
cd OpsConsole
dotnet run
```

Listens on `http://localhost:5300` (see `Properties/launchSettings.json`). Reads config from `appsettings.Development.json`, which already points at the other services' default local ports (Order `5224`, Payment `5080`, Inventory `5074`).

**Frontend:**

```bash
cd OpsConsole/web
cp .env.local.example .env.local   # first time only
npm install
npm run dev
```

Listens on `http://localhost:3000`. Requires the backend running (`OPS_CONSOLE_API_URL`) and the Gateway running (`GATEWAY_API_URL`, used for login/token refresh).

Sign in with a normal operator account via `/login` — it proxies to the Gateway's `/api/v1/auth/login`. Viewing data requires the `OpsViewer` policy; mutating actions additionally require `Admin`/`SuperAdmin`.

## Configuration

Backend (`appsettings.json` / env):

- `AdminApiKey` — validates `X-Admin-Api-Key` from the frontend.
- `OrderServiceUrl`, `PaymentServiceUrl`, `InventoryServiceUrl` — gRPC channel addresses for the three admin services.
- `InternalServices:OpsConsoleApiKey` — outbound gRPC auth; must match the same key configured on Order/Payment/Inventory.
- `Jwt:SecretKey`, `Jwt:Audience` — must exactly match Auth's signing key. The app throws at startup if `Jwt:SecretKey` is empty outside Development.

Frontend (`.env.local`, never `NEXT_PUBLIC_`-prefixed — these must stay server-only):

- `OPS_CONSOLE_API_URL` — backend base URL.
- `OPS_CONSOLE_ADMIN_API_KEY` — sent as `X-Admin-Api-Key`; must match the backend's `AdminApiKey`.
- `GATEWAY_API_URL` — used for login and silent session refresh.

## Deployment

- `OpsConsole/Dockerfile` — backend image (`mcr.microsoft.com/dotnet/aspnet:8.0` runtime, non-root, listens on `8080`).
- `OpsConsole/web/Dockerfile` — frontend image, built with Next's `output: "standalone"` (see `next.config.mjs`), non-root, listens on `3000`.
- `OpsConsole/docker-compose.yml` — local compose template (both services have no database of their own, so there's nothing else to spin up besides the shared infra in `../docker-compose.infra.yml`).
- `k8s/ops-console-service.yaml` / `k8s/ops-console-web.yaml` — Deployment + Service manifests. The backend is `ClusterIP` (internal-only, only ever called by the frontend's Server Components); the web app is `LoadBalancer` (the actual thing operators reach).
- Secrets live in `k8s/secrets.yaml` (`ops-console-service-secret`, `ops-console-web-secret`) — replace the `change-me-*` placeholder values before deploying anywhere real, and make sure `InternalServices__OpsConsoleApiKey` matches across `ops-console-service-secret` and the `order-service-secret` / `payment-service-secret` / `inventory-service-secret` entries.
- CI: `.github/workflows/opsconsole-pipeline.yml` builds/type-checks both halves and publishes both images to GHCR on `main`.

## Health checks

`GET /health/live` and `GET /health/ready` on the backend are exempt from the `X-Admin-Api-Key` gate (kubelet probes can't carry it). The frontend has no dedicated health route — its k8s probes hit `/login`, which is excluded from `middleware.ts`'s auth gate and always renders without touching the backend.
