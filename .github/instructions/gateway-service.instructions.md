---
applyTo: "Gateway/**"
description: "Use when working on the Gateway — REST-to-gRPC API gateway with Minimal API endpoints, JWT auth, Swagger, rate limiting, typed gRPC client factory for all backend services, user event publishing, and shipping webhook ingress translated to order saga events."
---

# Gateway Service

## Overview

REST API gateway that translates HTTP requests to gRPC calls against backend services. Single entry point for all external clients. Uses ASP.NET Core Minimal APIs with JWT Bearer authentication and Swagger documentation.

Gateway also receives external shipping provider callbacks for return deliveries and publishes normalized saga continuation events to Kafka (`ReturnShipmentDeliveredEvent` to `order.events`).

## Architecture (API Gateway Pattern)

- **Endpoints/** — Minimal API route groups per domain: `ProductEndpoints`, `SearchEndpoints`, `OrderEndpoints`, `PaymentEndpoints`, `InventoryEndpoints`, `AuthEndpoints`, `UserEndpoints`, `RoleEndpoints`, `B2BOrderEndpoints`, `RecurringOrderEndpoints`, `UserEventEndpoints`, `ShippingWebhookEndpoints`
- **Contracts/** — Immutable C# records organized by domain (REST DTOs), including `UserEvents/` for behavioral tracking
- **Services/** — `KafkaUserEventPublisher` (publishes user behavioral events to `user.events`) and `KafkaOrderSagaEventPublisher` (publishes shipping webhook continuation events to `order.events`)
- **Mappers/** — `DecimalValueMapper` and proto ↔ DTO conversions
- **Extensions/** — `ServiceCollectionExtensions` (gRPC client registration), `EndpointRouteBuilderExtensions` (health endpoints)
- **Middleware/** — `GrpcExceptionHandler` maps `RpcException` StatusCode to HTTP ProblemDetails
- **Protos/** — 11 proto files generating gRPC client stubs

## Tech Stack & Conventions

- .NET 8, ASP.NET Core Minimal APIs
- gRPC: Grpc.Net.ClientFactory (typed client pool)
- Kafka: Confluent.Kafka (user event publishing and shipping webhook continuation event publishing)
- Auth: JWT Bearer (Microsoft.AspNetCore.Authentication.JwtBearer)
- Rate limiting: ASP.NET Core built-in `AddRateLimiter` / `SlidingWindowLimiter` (two named policies)
- API docs: Swashbuckle/Swagger (dev only)
- Observability: OpenTelemetry
- Nullable reference types enabled globally

## Code Patterns

- **Minimal API groups**: `app.MapGroup("/api/v1/products").WithTags("Products")` with chained `.MapGet()`, `.MapPost()`, etc.
- **Proto aliases**: `using GrpcXxx = Protos.Xxx` to avoid name collisions between contract DTOs and generated proto types
- **Immutable records**: All request/response DTOs are C# records
- **gRPC client factory**: Centralized in `AddGrpcClients()` — 10 typed clients from config URLs
- **Error mapping**: `GrpcExceptionHandler` converts gRPC status codes to HTTP status codes with ProblemDetails
- **DecimalValue mapping**: `units` (int64) + `nanos` (int32) ↔ decimal — shared mapper handles both `Protos.Common.DecimalValue` and `Protos.Product.DecimalValue`
- **SSE streaming**: `/api/v1/search/stream` sends progressive results as Server-Sent Events (keyword phase → merged)
- **OpenAPI metadata**: `WithName()`, `WithTags()`, `WithOpenApi()` on every endpoint
- **Rate limiting**: `RequireRateLimiting("auth-strict")` on `/login` and `/password-reset/request`; `RequireRateLimiting("search")` on the whole search group. Policies are `SlidingWindowLimiter` keyed by remote IP. Registered once in `Program.cs` via `AddRateLimiter`. Rejected requests receive `429 Too Many Requests`. Never add a new sensitive endpoint without attaching an appropriate policy.
- **Shipping webhook ingress**: `/api/v1/webhooks/shipping/returns/delivered` accepts carrier callback payload, validates required fields (and optional shared-secret header), and publishes `ReturnShipmentDeliveredEvent` envelope to Kafka saga topic.

## Authentication

- JWT Bearer token validation
- Dev: local JWT secret key
- Prod: Authority URL (external token server)
- `RequireAuthorization()` on non-public endpoints

## REST Surface

~52 endpoints across 12 domain files + 2 health endpoints:
- Auth (8), Users (6), Roles (5), Products (3), Orders (5), B2B Orders (5), Recurring Orders (6), Payments (2), Inventory (2), Search (4: search, similar, stream, frequently-bought-together), User Events (4: view, click, purchase, search-bounce), Health (2)
- Shipping Webhooks (1: return delivered)

## Configuration

- `GrpcServices` section: `AuthUrl`, `UserUrl`, `ProductUrl`, `OrderUrl`, `PaymentUrl`, `InventoryUrl`, `SearchUrl`
- `Kafka` section: `BootstrapServers`, `UserEventsTopic`, `SagaTopic`
- `WebhookSecurity` section: `ShippingSharedSecret` (optional header check for shipping callbacks)
- JWT: Authority, SecretKey (dev)
- Health: `/health/live`, `/health/ready`
- Rate limiting policies are defined in code (no config knobs); adjust `PermitLimit` / `Window` values in `Program.cs` if limits need tuning

## Key Rules

- Gateway is mapping-only — no business logic, no database, no domain models
- All backend communication is gRPC — never call backend services over HTTP (exception: Kafka for user event fire-and-forget publishing)
- Shipping webhook ingress is the controlled public callback entrypoint — do not expose Order internals for carrier callbacks
- Proto files must stay in sync with backend service proto definitions
- `DecimalValue` conversion is shared — always use the mapper, don't inline nanos math
- `RpcException` must be caught and translated — never leak gRPC errors to REST clients
- No test projects exist — the service is thin mapping; test backend services instead
- Any endpoint that accepts credentials, triggers emails, or is computationally expensive must have a rate limiting policy attached
