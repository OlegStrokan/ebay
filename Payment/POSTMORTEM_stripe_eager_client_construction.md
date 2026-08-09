# Postmortem — Payment: eager `StripeClient` construction defeated the not-configured guard

**Service:** Payment
**Component:** `StripePaymentProvider`
**Tracker:** BUG-011 — *"real Stripe provider throws instead of degrading when the key is missing"*
**Found:** 2026-08-08, while making the Payment test suites pass
**Symptoms:** 4 failing unit tests asserting graceful degradation; in production, every payment RPC
throwing `ArgumentException` out of the handler instead of returning a clean failure

---

## Background — the intended behaviour

`StripePaymentProvider` is written to **degrade gracefully** when no Stripe secret key is
configured. Every public method opens with the same guard:

```csharp
private bool HasSecretKey() => !string.IsNullOrWhiteSpace(_stripeOptions.SecretKey);
```

```csharp
public async Task<ProcessPaymentProviderResult> ProcessPaymentAsync(/* … */)
{
    if (!HasSecretKey())
    {
        return new ProcessPaymentProviderResult(
            Status: ProviderProcessPaymentStatus.Failed,
            ProviderPaymentIntentId: null,
            ClientSecret: null,
            ErrorCode: "stripe_secret_not_configured",
            ErrorMessage: "Stripe secret key is not configured.");
    }
    // …
}
```

That guard is present in six places. The design intent is unambiguous: a missing key is a
*configuration* problem that should surface as a typed, mappable failure result — not as an
exception escaping the gRPC handler.

An empty key is a real, shipped configuration. [`k8s/secrets.template.yaml`](../k8s/secrets.template.yaml)
renders `Stripe__SecretKey: ""` whenever `STRIPE_SECRET_KEY` is not supplied to the generator, which
is the normal state while `Stripe__ProviderType` is `Fake`.

---

## The bug — the guard could never run

The Stripe client was built in a **field initializer**:

```csharp
private readonly IStripeClient _stripeClient = new StripeClient(stripeOptions.Value.SecretKey);
```

C# compiles field initializers into the constructor, before the constructor body. Stripe's SDK
rejects an empty or whitespace key from `StripeClient`'s own constructor. So:

```
DI resolves IStripePaymentProvider
  → StripePaymentProvider ctor runs
    → field initializer runs
      → new StripeClient("")  →  throws ArgumentException
        → the object never comes into existence
```

The failure happens at **construction**, so no method body ever executes. `HasSecretKey()` — six
correctly written guards whose entire purpose is this exact scenario — was unreachable code. A
defensive mechanism that was implemented properly was dead on arrival because of *when* an unrelated
line ran.

This is worth stating precisely, because the framing matters: the key was not "not initialised yet",
arriving later. The key is **legitimately absent**, and stays absent. The defect is that the
provider committed to needing it at construction time, when the whole point of the guard was to
tolerate its absence at call time.

### Why the tests were the right side of the argument

Four unit tests asserted that the real provider degrades on a missing key. They were failing, and it
would have been easy to "fix" them by asserting the throw instead. That would have been wrong twice
over — it would have encoded the broken behaviour, and it would have contradicted the six guards
already in the production file. The tests described the intended design; the constructor
contradicted it.

---

## Fix — defer construction to first use

```csharp
// StripeClient's ctor rejects an empty key, so building it eagerly made every method
// throw instead of degrading through its HasSecretKey guard.
private readonly Lazy<IStripeClient> _stripeClient =
    new(() => new StripeClient(stripeOptions.Value.SecretKey));

private IStripeClient Client => _stripeClient.Value;
```

`Lazy<T>` stores the factory and runs it on the first read of `.Value`, caching the result. It is
thread-safe by default (`LazyThreadSafetyMode.ExecutionAndPublication`), which matters here because
the provider is resolved concurrently by gRPC request threads.

The remaining changes are mechanical: six call sites moved from the field to the property, e.g.

```diff
-    var paymentIntentService = new PaymentIntentService(_stripeClient);
+    var paymentIntentService = new PaymentIntentService(Client);
```

The resulting order of events:

```
DI resolves IStripePaymentProvider  →  ctor succeeds, no Stripe call yet
  → ProcessPaymentAsync
    → HasSecretKey() == false  →  returns "stripe_secret_not_configured"   ← never touches Client
```

and when a key *is* configured:

```
    → HasSecretKey() == true
      → first read of Client  →  new StripeClient("sk_live_…")  →  cached for the process
```

`new StripeClient(...)` now only ever runs on a path that has already established a key exists, so
it cannot throw for this reason.

### Note on the one deliberate throw

`CancelAuthorizationAsync` intentionally throws rather than returning a result object:

```csharp
if (!HasSecretKey())
    throw new InvalidOperationException("Stripe secret key is not configured.");
```

That is unchanged and correct — it has no result type to express a soft failure, and it is called
from a compensation path where silence would be worse than an exception. The point of the fix is
that this is now a *chosen* exception with a clear message, rather than an `ArgumentException`
leaking out of the Stripe SDK's constructor.

---

## Wider lesson

Constructor-time work is not free: it decides what your object *requires in order to exist*. A
dependency that the type is designed to operate without must not be acquired in a field initializer
or constructor body, or every runtime tolerance you wrote for its absence becomes unreachable.

The same shape is worth watching for elsewhere in this repo — any `private readonly X _x = new
X(config.Something)` where `Something` is optional by design.

---

## Verification

- The 4 graceful-degradation unit tests pass.
- Payment solution: 410 tests passing, integration and E2E included.

## Related

- [`Product/POSTMORTEM_outbox_claim_deadlock.md`](../Product/POSTMORTEM_outbox_claim_deadlock.md) —
  the other production defect found in the same pass.
