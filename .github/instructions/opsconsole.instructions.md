---
applyTo: "OpsConsole/**"
description: "Use when working on the Ops Console — a .NET Minimal API + Next.js operator dashboard for saga monitoring, dead-letter triage, and admin mutations across Order/Payment/Inventory."
---

# Ops Console

## Overview

Two-part internal admin tool for operators to monitor sagas, inspect dead-lettered messages, and trigger recovery actions (force-compensate a stuck saga, retry a failed compensation, requeue a poison message) across Order, Payment, and Inventory. Never routed through the Gateway or exposed to end users.

- **`OpsConsole/`** — .NET 8 Minimal API backend. Proxies to Order's `AdminOpsService`, Payment's `AdminPaymentService`, and Inventory's `AdminInventoryService` gRPC surfaces.
- **`OpsConsole/web/`** — Next.js 16 (App Router) frontend. The only thing operators actually open in a browser.

## Architecture

- **Single flat backend project** (`OpsConsole/OpsConsole.csproj`), no layered Domain/Application/Infrastructure split — this is a stateless proxy, not a service with its own persistence.
- **Auth/** — `ApiKeyMiddleware` gates every request with `X-Admin-Api-Key` (the human/frontend-facing secret). `/health/*` is explicitly exempted so k8s probes don't need a key.
- **Grpc/** — `InternalApiKeyInterceptor` attaches `x-internal-api-key` (a *different* shared secret, `InternalServices:OpsConsoleApiKey`) to every outbound gRPC call to Order/Payment/Inventory's admin services. Don't confuse the two keys — one authenticates the browser→OpsConsole hop, the other authenticates OpsConsole→downstream-service hop.
- **Endpoints/** — grouped Minimal API endpoint classes (`SagaEndpoints`, `DeadLetterEndpoints`, `SagaMutationEndpoints`, `DeadLetterMutationEndpoints`, `SagaCorrelationEndpoints`, `HealthEndpoints`), each a static `Map*Endpoints(this IEndpointRouteBuilder)` extension mapped once in `Program.cs`.
- **Protos/** — client-only copies of `admin_ops.proto` (Order), `payment_admin_ops.proto`, `inventory_admin_ops.proto`. **Order's copy under `Order/Order/Protos/protos/admin_ops.proto` is the source of truth for the saga/DLQ contract** — if you add a field there, mirror it here too, or the gRPC client and server silently drift.

### Frontend (`OpsConsole/web/`)

- Next.js App Router, Server Components do the data fetching (`lib/opsConsole.ts` — `server-only`, never imported by client components).
- **`middleware.ts`** is the page-level auth gate: no session cookie → redirect to `/login`; expired access token but valid refresh token → silently refresh via the Gateway's `/api/v1/auth/refresh` before continuing.
- **`lib/session.ts` / `lib/sessionCookies.ts`** — httpOnly cookie helpers. Two cookies: `ops_console_session` (short-lived access token) and `ops_console_refresh` (30-day refresh token, matches the Auth service's actual `RefreshTokenEntity` TTL). Never expose either to client JS.
- **`app/api/session/*`** — login/logout route handlers that proxy to the Gateway's `/api/v1/auth/login`, `/refresh`, `/revoke`.
- Env vars (`OPS_CONSOLE_API_URL`, `OPS_CONSOLE_ADMIN_API_KEY`, `GATEWAY_API_URL`) are intentionally **not** `NEXT_PUBLIC_`-prefixed — they must never reach the browser bundle.

## Tech Stack

- Backend: .NET 8, Minimal API, `Grpc.Net.ClientFactory` gRPC clients, JWT bearer auth (`Microsoft.AspNetCore.Authentication.JwtBearer`), custom API-key middleware, `RateLimiter` for mutation endpoints.
- Frontend: Next.js 16 (App Router, Turbopack), React 19, TypeScript, no CSS framework (plain `globals.css`).
- No database anywhere in this service — everything is a live proxy/read-through.

## Authentication (two layers, both required for mutations)

1. **`X-Admin-Api-Key`** (`ApiKeyMiddleware`) — shared secret between the Next.js backend and the .NET backend. Gates every request.
2. **Operator JWT** (`AddJwtBearer`, **RS256** — verifies with Auth's **public** key `Jwt:PublicKeyBase64`, the same key Gateway uses; OpsConsole never holds the signing key) — `OpsViewer` policy (Admin/SuperAdmin/OpsViewer roles) required for reads, `OpsAdmin` policy (Admin/SuperAdmin only) required for mutations (compensate, retry-compensation, requeue dead letter).
3. Outbound to Order/Payment/Inventory: `InternalServices:OpsConsoleApiKey` via `InternalApiKeyInterceptor` — a third, separate shared secret. All three secrets are independent; rotating one doesn't require rotating the others, but Order/Payment/Inventory's own `InternalServices:OpsConsoleApiKey` config must always match this service's copy or every admin RPC returns `PermissionDenied` (fails closed, not open).

## Configuration

- `AdminApiKey` — validates `X-Admin-Api-Key` from the frontend.
- `OrderServiceUrl`, `PaymentServiceUrl`, `InventoryServiceUrl` — flat (non-`GrpcServices__*`) gRPC channel addresses, deliberately different naming from the rest of the repo's convention since this service was added later.
- `InternalServices:OpsConsoleApiKey` — outbound gRPC auth to the three admin services.
- `Jwt:PublicKeyBase64`, `Jwt:Issuer`, `Jwt:Audience` — Auth's RSA **public** key / issuer / audience; `Program.cs` throws at startup if empty outside Development. Verify-only — OpsConsole never holds the signing key.
- Frontend: `OPS_CONSOLE_API_URL`, `OPS_CONSOLE_ADMIN_API_KEY`, `GATEWAY_API_URL` in `.env.local` (see `web/.env.local.example`).

## Testing

- **Backend**: `OpsConsole/OpsConsole.UnitTests` — xUnit + NSubstitute, driven through `WebApplicationFactory<Program>` (`TestHelpers/OpsConsoleWebApplicationFactory.cs`). Only the three outbound gRPC clients (Order/Payment/Inventory) are swapped for NSubstitute fakes; `ApiKeyMiddleware`, the real JwtBearer scheme, and the `OpsViewer`/`OpsAdmin` policies all run for real, so a passing test proves the actual auth pipeline.
  - `Program.cs` reads `Jwt:PublicKeyBase64`/`Jwt:Audience`/`Jwt:Issuer` into local variables before `builder.Build()` runs, so `WebApplicationFactory`'s `ConfigureAppConfiguration` can't override them (it only layers in at `Build()` time). Tests mint JWTs with a dev RSA **private** key held in `OpsConsoleWebApplicationFactory` (the host validates them with the public key from `appsettings.Development.json`, mirroring prod) — don't reintroduce a `Jwt:*` override in `ConfigureAppConfiguration`, it will silently have no effect and every authenticated test will 401.
  - `AdminApiKey` and `InternalServices:OpsConsoleApiKey` **are** read live from `IConfiguration` at request time, so overriding those two via `ConfigureAppConfiguration` does work.
  - `OpsConsole.UnitTests` lives inside `OpsConsole/` (unlike sibling services, which keep test projects as source-tree siblings of the main project's own folder) — `OpsConsole.csproj` explicitly excludes it via `<Compile Remove>`/`<Content Remove>`/etc. If you add another nested test project, exclude it the same way or its generated `AssemblyInfo.cs` collides with the main project's during a solution build (`CS0579`).
- **Frontend**: no test project yet. If you add one, check how the sibling Next.js frontends in this repo (if any) are set up first.

## Key Rules

- Never let `AdminApiKey` or `InternalServices:OpsConsoleApiKey` leak into logs or API responses. (The JWT public key is not sensitive; the private key lives only in Auth.)
- Every mutating endpoint must require the `OpsAdmin` policy explicitly — reads only need `OpsViewer`. Don't downgrade a mutation to `OpsViewer` for convenience.
- If you add a field to `admin_ops.proto`, update **both** copies (`Order/Order/Protos/protos/admin_ops.proto` and `OpsConsole/Protos/admin_ops.proto`) in the same change — they're not code-generated from a shared source.
- Frontend server-only env vars must never gain a `NEXT_PUBLIC_` prefix — that would ship them to the browser.
- `middleware.ts`'s matcher must keep excluding `/login` and `/api/session/*` — removing them creates a redirect loop.
- This service has no database; don't add one for convenience — if you need to persist something, it belongs in whichever downstream service owns that data.
