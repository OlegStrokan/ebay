# Product Moderation and Auto-Publish Backlog

This document splits the product moderation work into backlog-ready issues.

## 1. Add moderation state to product creation and update flows (DONE)

**Goal**
Introduce an explicit moderation lifecycle for user-created products so the system can distinguish draft content, pending review, approved products, and rejected products.

**Scope**
- Add `ProductStatus` values for `Draft`, `PendingApproval`, `Approved`, and `Rejected`.
- Make `CategoryId` optional on product creation.
- Keep `Draft` as a real seller save-later state.
- Require a category to be assigned before approval.
- Keep moderation state separate from availability or stock state.

**Acceptance Criteria**
- A product can be created without a category when the seller does not know the right classification.
- A product cannot be approved until a category has been assigned.
- The product status transitions are explicit and validated.
- Existing price and stock behavior is not broken by the moderation changes.

**Notes**
- This is the foundation for the rest of the workflow.
- Do not store `categoryName` in the write model.
- Do not add `CatalogItemId` to the product model; that relationship belongs on the listing side, not the unique product side.

## 2. Auto-approve products when confidence is high enough

**Goal**
Automatically approve low-risk products when the system has enough evidence that the product identity is correct.

**Scope**
- Auto-approve when GTIN matching or ML/heuristics confidence is above the configured threshold.
- Otherwise place the product into `PendingApproval`.
- Persist the reason and confidence used for the decision.

**Acceptance Criteria**
- Linked catalog-item products bypass manual approval.
- High-confidence GTIN matches can auto-approve.
- Low-confidence products remain pending.
- Moderators can inspect why a product was auto-approved or left pending.

**Notes**
- Keep the confidence threshold configurable.
- If the ML or heuristic service is unavailable, default to the safe path.

## 3. Introduce staged change requests for identity updates (PARTIALLY DONE)

**Goal**
Allow sellers to update price and stock immediately, while identity changes require re-approval without replacing the live approved listing.

**Scope**
- Treat `title`, `description`, `images`, `category`, and `GTIN` as identity fields.
- Create a staged change-request flow for identity field updates.
- Keep the current approved version visible until a moderator accepts the staged change.
- Apply `price` and `stock` updates immediately.

**Acceptance Criteria**
- Editing identity fields creates a pending change request instead of mutating the live approved product.
- Approving the change request promotes the new version to live.
- Rejecting the change request leaves the current approved version unchanged.
- Price and stock changes remain immediate.

**Notes**
- This should preserve storefront stability while still allowing moderation.


**Implmented**
- Have a fallback on every field except price and stock change - working version with limitation

## 4. Add a TrustedSeller fast path

**Goal**
Reduce friction for trusted sellers by allowing a faster approval path for low-risk changes.

**Scope**
- Add a `TrustedSeller` flag or equivalent trust score.
- Allow trusted sellers to auto-approve under a lower confidence threshold.
- Optionally bypass manual review for low-risk updates where policy permits.
- Keep the trusted-seller rules explicit and auditable.

**Acceptance Criteria**
- Trusted sellers receive faster approval than standard sellers.
- The fast path does not silently approve high-risk identity changes when policy requires review.
- The trust state is visible to moderation logic and testable in isolation.

**Notes**
- This is a policy layer, not a replacement for moderation.

## 5. Keep category names read-only and resolve them on the query side

**Goal**
Avoid duplicating category display data in the write model while still exposing user-friendly names in responses.

**Scope**
- Keep `CategoryId` as the stored reference.
- Resolve `categoryName` only in read models / API responses.
- Do not require category names in create or update commands.

**Acceptance Criteria**
- Write requests accept category identifiers only.
- Read responses can include `categoryName` when needed.
- Category display text is not persisted in product or catalog-item write state.

**Notes**
- This keeps the source of truth in the category reference data.

## Recommended implementation order

1. Add the moderation state with `Draft` and nullable `CategoryId`.
2. Enforce category assignment before approval.
3. Wire the auto-approval decision path for high-confidence GTIN matches and ML/heuristics.
4. Add staged change requests for identity updates.
5. Add the `TrustedSeller` fast path and policy thresholds.
6. Keep category names on the read side only and update API responses.

## Suggested review checkpoints

- Verify the domain model still supports the current product and listing workflows.
- Add tests for create without category, category assignment before approval, approve, reject, staged update, and trusted-seller behavior.
- Confirm that price and stock updates remain immediate for approved products.