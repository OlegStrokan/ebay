# Inter-Service Authentication

## When is a shared secret needed?

Not all inter-service calls need one — only calls where the **callee needs to trust the caller's identity**.
Most calls in this codebase don't need it at all.

### In this codebase specifically

```
Auth → User.GetUserByEmail        ✅ needs secret (no JWT exists yet)
Auth → User.VerifyCredentials     ✅ same reason

Gateway → Order.StartB2BOrder     ❌ no secret needed
Gateway → Product.CreateProduct   ❌ no secret needed
Order → Inventory.Reserve         ❌ no secret needed
```

The Gateway-to-service calls are fine without it because the **Gateway already validated the user's JWT**
before forwarding. The downstream service trusts the Gateway implicitly — they're all inside the same
private network (Kubernetes cluster). An outsider can't reach `order-service:5001` directly.

`GetUserByEmail` is special because it's called **before** any JWT exists, from inside the cluster.

---

## Configuration — environment variable

```
# Auth service
InternalServices__ApiKey=some-long-random-secret

# User service
InternalServices__ApiKey=some-long-random-secret   ← same value
```

.NET maps `__` → `:`, so `InternalServices__ApiKey` resolves to `configuration["InternalServices:ApiKey"]`.
Both services read it from their environment.

In Kubernetes you'd put it in a `Secret` and mount it as an env var on both pods — one secret object,
two consumers.

---

## General pattern for this architecture

| Scenario | Auth mechanism |
|---|---|
| Human → Gateway | JWT Bearer token |
| Gateway → internal service | Nothing (private network trust) |
| Service → service, pre-JWT bootstrap | Shared secret in env |
| Service → service at scale (prod) | mTLS / service mesh (future) |

The shared secret is only needed for the bootstrap case. Everything else relies on network-level isolation.
