---
applyTo: "Auth/**"
description: "Use when working on the Auth Service — gRPC authentication service handling login, registration, JWT tokens, password reset, and email verification via the User service."
---

# Auth Service

## Overview

gRPC authentication service that handles login, registration, token management (JWT access + refresh tokens), password reset, and email verification. Delegates user identity storage to the User service via gRPC gateway.

## Architecture (Clean Architecture)

- **Api/** — gRPC entry point (`AuthGrpcService`), maps domain results to gRPC responses
- **Application/** — Use cases: Login, Register, RefreshToken, ResetPassword, ValidateToken, RevokeToken, VerifyEmail, RequestPasswordReset
- **Domain/** — Entities (RefreshTokenEntity, PasswordResetTokenEntity, EmailVerificationTokenEntity), Gateways (`IUserGateway`), Repositories
- **Infrastructure/** — DbContext, Repository implementations, JWT service, UserGateway (gRPC to User), Helpers
- **Protos/** — gRPC service definitions

## Tech Stack & Conventions

- .NET 8, gRPC (Grpc.AspNetCore)
- PostgreSQL + EF Core 8
- JWT: System.IdentityModel.Tokens.Jwt
- Password hashing: BCrypt.Net-Next
- ID generation: ULID
- Messaging: Kafka producer (publishes email events to Email service)
- Logging: structured via ILogger
- Primary constructor DI (C# 12)

## Code Patterns

- **Single consolidated `AuthGrpcService`** — all RPC methods in one service class, mapped once to avoid gRPC endpoint ambiguity
- **Gateway pattern** for User service integration — `IUserGateway` abstracts gRPC calls to User service
- **Repository pattern** for token persistence (RefreshToken, PasswordResetToken, EmailVerificationToken)
- **Use case per folder** — each operation gets its own folder with handler + response DTO
- **Custom `DomainException`** mapped to gRPC status codes at the API layer
- Auth does NOT store user credentials — `VerifyCredentials` is delegated to User service via gRPC
- `GetUserByEmail` is identity lookup only (no password hash returned)

## Configuration

- env/appsettings: PostgreSQL connection, User service gRPC URL, JWT settings (PrivateKeyBase64 — the RS256 RSA signing key; Issuer, Audience, AccessTokenExpirationMinutes), Kafka bootstrap servers. Auth is the **only** holder of the private key; verifiers (Gateway, OpsConsole) get the public key from the `jwt-public-config` ConfigMap. The deployed keypair is generated out-of-band by `scripts/generate-k8s-secrets.sh` and never committed — only `appsettings.Development.json` carries a throwaway local keypair.

## Testing

- **Unit**: xUnit + NSubstitute
- **Integration**: xUnit + Testcontainers.PostgreSql
- **E2E**: Full gRPC tests against running service
- Test project structure mirrors source layers

## Key Rules

- Never store or return password hashes across service boundaries — credential verification stays in User service
- All token entities use ULID for IDs
- Refresh tokens must be revocable (stored in DB, not stateless)
- Email events published to Kafka for async delivery by Email service
- Auth depends on User service gRPC compatibility RPCs: `VerifyCredentials`, `VerifyUserEmail`, `UpdateUserPassword`
