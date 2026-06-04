---
applyTo: "Product/**"
description: "Use when working on the Product Service — gRPC service with DDD aggregate roots, CQRS via MediatR, outbox pattern for Kafka event publishing, and inventory event consumption."
---

# Product Service

## Overview

gRPC service that owns product, listing, and catalog-item aggregates. Uses Domain-Driven Design with rich aggregate roots, CQRS via MediatR, and the transactional outbox pattern for reliable Kafka event publishing. Consumes inventory events to adjust stock.

## Architecture (Clean Architecture + DDD)

- **Api/** — gRPC entry point (`ProductGrpcService`), Mappers (proto ↔ DTOs)
- **Application/** — Commands and Queries via MediatR, FluentValidation, Consumers (inventory events), DTOs, `ApplicationModule.cs`
- **Domain/** — Aggregate roots (Product, Listing, CatalogItem), Value Objects (ProductId, SellerId, Money, ProductStatus), Domain Events, Interfaces
- **Infrastructure/** — EF Core persistence, Kafka messaging, Outbox processor, Background services, `InfrastructureModule.cs`
- **Protos/** — Separate class library with `product.proto` and `common.proto`

## Tech Stack & Conventions

- .NET 8, gRPC (Grpc.AspNetCore)
- PostgreSQL + EF Core 8 (port 5435)
- CQRS: MediatR 12.x with `ValidationBehavior<,>` pipeline
- Validation: FluentValidation 11.x
- Messaging: Confluent.Kafka (producer + consumer)
- Observability: OpenTelemetry with manual Kafka instrumentation (ActivitySource "ProductService.Kafka")
- Testing: NUnit + NSubstitute
- Module pattern: `AddApplicationServices()`, `AddInfrastructureServices()`

## Domain Model

**Product Aggregate Root** with status state machine:
- Draft → PendingApproval | Deleted
- PendingApproval → Approved | Rejected | Deleted
- Rejected → PendingApproval | Deleted
- Approved → Inactive | OutOfStock | Deleted
- Inactive → Approved | Deleted
- OutOfStock → Approved | Deleted
- Deleted → (terminal, no transitions)

Products are created with a system placeholder category and start as **Draft**. Assigning a real category via update moves Draft/Rejected products to **PendingApproval**. Admin must `Approve()` (→Approved) or `Reject(reason)` (→Rejected). Approval is blocked while placeholder category is assigned.

- **Value Objects**: `ProductId` (Guid), `CategoryId` (includes placeholder helper/constant), `SellerId`, `Money` (decimal + currency), `ProductStatus` (state machine value object), `ProductAttribute` (key-value)
- **ReviewNotes**: Optional string on Product aggregate, set on Reject(), cleared on Approve()

**Domain Events**: `ProductCreatedEvent`, `ProductUpdatedEvent`, `ProductDeletedEvent`, `ProductStatusChangedEvent`, `ProductStockUpdatedEvent`, `ProductApprovedEvent`, `ProductRejectedEvent`

## Code Patterns

- **Result pattern**: Commands return `Result<T>` with `IsSuccess`, `Value`, `Errors` — not exceptions for business failures
- **Factory methods**: `Product.Create()` generates ID and raises domain events
- **Immutable records** for DTOs and domain events
- **Aggregate validation**: Invariants enforced inside aggregate methods (throws if Deleted, validates transitions)
- **Transactional outbox**: `ProductPersistenceService` wraps transaction → save → outbox in one commit
- **OutboxProcessor**: Batch polls unprocessed messages, groups by AggregateId for causal ordering, parallel across groups (MaxParallelism=5)
- **Inventory consumers**: `IInventoryEventConsumer` handlers adjust stock on InventoryConfirmed/Released/Expired
- **Sealed classes**: Used where inheritance not expected

## Kafka Integration

- **Publishes to**: `product.events` topic (via outbox)
- **Consumes from**: `inventory.events` topic (group: `product-service-inventory`)
- **Headers**: `event-type`, `event-id`, `traceparent`
- **EventWrapper**: EventId, EventType, Payload (JsonElement), OccurredOn
- Inventory consumer: 3 retries with exponential backoff + jitter

## Configuration

- PostgreSQL connection, Kafka bootstrap servers
- Outbox: BatchSize=20, MaxRetries=5, PollIntervalMs=2000, MaxParallelism=5
- Processed outbox cleanup: deletes messages older than 7 days
- OpenTelemetry Jaeger endpoint

## Testing

- **Unit**: NUnit + NSubstitute — domain logic, command handlers, aggregate behavior
- **Integration**: Database tests with actual PostgreSQL
- Test structure mirrors source: `Domain.Tests/`, `Application.Tests/`, `Api.Tests/`, `Infrastructure.Tests/`

## gRPC RPCs

- Product CRUD: CreateProduct, UpdateProduct, DeleteProduct, GetProduct, GetProducts, GetProductPrices
- Moderation: ApproveProduct, RejectProduct, GetPendingProducts, GetProductStatus
- Listing & CatalogItem: CRUD operations for seller listings and admin catalog items

## Proto Contract Notes

- Product status is exposed as `ProductStatus` enum in product-facing responses (`ProductDetail`, `CreateProductResponse`, `GetProductStatusResponse`).
- `CreateProductRequest` no longer accepts `category_id` (field 4 is reserved).
- `UpdateProductRequest.category_id` is required and used to move products from placeholder draft flow into moderation.
- Customer-facing product category output should hide the internal placeholder category (empty id/name).

## Key Rules

- Status transitions are enforced by `ProductStatus` state machine — never set status directly
- `UpdateStock(0)` auto-transitions to OutOfStock; `UpdateStock(>0)` reverts to Approved
- `AdjustStock()` is delta-based and clamps to 0 minimum
- All writes go through outbox — never publish to Kafka directly from a command handler
- `DecimalValue` proto uses `units` (int64) + `nanos` (int32) for money — nanos = fractional * 1_000_000_000
- Proto files live in a separate `Protos` class library project
