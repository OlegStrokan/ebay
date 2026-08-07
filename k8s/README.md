# Kubernetes manifests

## Secrets

No secret value is committed to this repository. `kustomization.yaml` references two files
that do not exist in a fresh clone:

| File | Contents | Committed? |
| --- | --- | --- |
| `secrets.template.yaml` | substitution tokens only | yes — source of truth for the *shape* of every Secret |
| `secrets.generated.yaml` | every Secret, incl. Auth's RS256 private key | no — gitignored, mode `0600` |
| `jwt-public-config.generated.yaml` | the matching RS256 **public** key (ConfigMap `jwt-public-config`) | no — regenerated with the keypair so the two can never drift |

### Deploy

```bash
./scripts/generate-k8s-secrets.sh
kubectl apply -k k8s/
```

If `kustomize` reports `secrets.generated.yaml` missing, you skipped the first step. That
failure is deliberate — it is what stops a placeholder credential from reaching a cluster.

### Values the generator cannot invent

Anything shared with an external party must be passed in, otherwise a fresh random value is
written and the peer will reject every request signed with it:

```bash
STRIPE_SECRET_KEY=sk_live_... \
STRIPE_WEBHOOK_SECRET=whsec_... \
EMAIL_SMTP_USERNAME=... EMAIL_SMTP_PASSWORD=... \
SHIPPING_API_KEY=... DPD_WEBHOOK_SECRET=... PPL_WEBHOOK_SECRET=... \
./scripts/generate-k8s-secrets.sh --force
```

Everything else — Postgres passwords, the RS256 keypair, the OpsConsole admin and internal
API keys, `OrderCallback__SharedSecret` — is generated locally and never leaves the machine.

### Rotation

`--force` re-runs the whole generation, so it rotates **every** credential at once,
including the JWT signing key. Every token minted before the rotation stops validating, and
the Postgres passwords in the new Secrets only take effect on a database that has been
`ALTER ROLE`d to match. Roll the Postgres StatefulSets or re-provision their volumes.

The script prints the SHA-256 of the new public key; compare it against
`jwt-public-config` in the cluster to confirm the rollout landed.

### Encrypting secrets at rest

`secrets.generated.yaml` is intended to be applied and discarded, which is fine for a single
operator but does not survive GitOps. Pick one before more than one person deploys:

- **Sealed Secrets** — `./scripts/generate-k8s-secrets.sh --seal` writes `sealed-secrets.yaml`,
  encrypted to the cluster's controller key and therefore safe to commit. Swap
  `secrets.generated.yaml` for `sealed-secrets.yaml` in `kustomization.yaml`.
- **External Secrets Operator** — keep the values in AWS Secrets Manager / Vault and replace
  each `Secret` with an `ExternalSecret`. Preferred for the EKS setup in `infra/`, since
  IRSA already provides the identity.
- **SOPS** (+ `ksops` or Flux) — encrypt `secrets.generated.yaml` in place with a KMS key.

### Key handling rules

- Auth is the **only** holder of `Jwt__PrivateKeyBase64`. Gateway and OpsConsole receive the
  public half via `jwt-public-config` and cannot mint tokens.
- `InternalServices__OpsConsoleApiKey` must be identical across `order-service-secret`,
  `payment-service-secret`, `inventory-service-secret`, and `ops-console-service-secret`;
  the template renders one generated value into all four.
- `AdminApiKey` (`ops-console-service-secret`) must equal `OPS_CONSOLE_ADMIN_API_KEY`
  (`ops-console-web-secret`); likewise rendered from a single value.
- Never paste a rendered value back into `secrets.template.yaml`.

> A private RS256 key was committed to this repo in the past. It is unrecoverable from git
> history without a rewrite, so it must be treated as public forever. The generator produces
> a new keypair; do not reintroduce the old one anywhere.
