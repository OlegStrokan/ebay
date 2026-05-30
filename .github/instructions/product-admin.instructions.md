---
applyTo: "ProductAdmin/**"
description: "Use when working on the Product Admin service — Minimal API with API key auth, gRPC client to Product service, providing admin moderation, product management, and catalog item endpoints."
---

# Product Admin Service

## Overview

Lightweight .NET 8 Minimal API that serves as an admin-facing HTTP gateway to the Product gRPC service. Secured with API key authentication (no JWT). Used by internal admin tools for product moderation, direct product management, and catalog item CRUD.

## Architecture

- **Single project** (`ProductAdmin/`): Minimal API, no layered architecture
- **Auth/** — `ApiKeyMiddleware` validates `X-Admin-Api-Key` header against config
- **Endpoints/** — Grouped endpoint classes mapped in Program.cs
- **Protos/** — `product.proto` + `common.proto` (admin-relevant RPCs only)

## Tech Stack

- .NET 8, Minimal API (no controllers)
- gRPC client (`Grpc.Net.Client`) to Product service
- API key auth via custom middleware
- No database — stateless proxy

## Endpoint Groups

### Product Moderation (`/products/pending`, `/products/{id}/approve`, `/products/{id}/reject`)
- `GET /products/pending` — lists products awaiting review (paged)
- `POST /products/{id}/approve` — approves a pending product
- `POST /products/{id}/reject` — rejects with reason

### Product Admin (`/products/{id}`, `/products/{id}/activate`, etc.)
- `PUT /products/{id}` — update product details
- `DELETE /products/{id}` — delete product
- `POST /products/{id}/activate` — activate
- `POST /products/{id}/deactivate` — deactivate
- `PUT /products/{id}/stock` — update stock

### Catalog Items (`/catalog-items/...`)
- CRUD for catalog items (admin-only aggregate in Product service)

## Authentication

- **API Key middleware**: Reads `X-Admin-Api-Key` header, compares to `AdminApiKey` in config
- Returns 401 if missing/invalid
- No JWT, no user context — all requests are admin-level

## Configuration (`appsettings.json`)

- `AdminApiKey` — the shared secret for API key auth
- `ProductServiceUrl` — gRPC endpoint of the Product service (e.g., `http://localhost:5001`)

## Code Patterns

- Endpoints return `Results.Ok()` / `Results.NotFound()` / `Results.BadRequest()` based on gRPC response status
- gRPC client registered via `AddGrpcClient<ProductService.ProductServiceClient>()` in DI
- `DecimalValue` helper for money conversion (units + nanos)
- Static endpoint mapping classes with `MapGroup()` pattern

## Key Rules

- This service has NO database — it's a pure proxy to the Product gRPC service
- All mutations go through gRPC — never bypass to talk directly to Product's DB
- API key must never be logged or returned in responses
- Keep endpoint methods thin — map HTTP request to gRPC call, map gRPC response to HTTP result
