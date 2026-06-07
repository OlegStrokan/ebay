# Payment Service

use-cases:
- process/capture payment request with strong idempotency
- handle async provider updates from webhook events
- reconcile stale pending payments/refunds when webhook is delayed or missed
- process refunds (also with idempotency)
- expose read/query use-cases for payment status lookup
- queue manual callbacks if needed for operation recovery

- process payment used for b2b orders, subscriptions and internal stuff
- capture payment used for actully "pay" already initialzated payment by frontend in case of b2c orders
- cancel reservation - for b2c if order creation fails after frontend reserve money for order

essentially it's used by order service:
- order saga calls payment
- if payment is succeeded saga continues immediately


main point if stripe returns immediate Success, the request path is:
ProcessPaymentCommandHandler calls Stripe, state is updated, one db commit, immediate response back
same for RefundPaymentCommandHandler

if stripe return immediately failed state => we send failed synchronously

for pending and requireAction it's also return to order service, but magic happens later:
- OrderCallbackDeliveryWorker: reads pending callback records from outbox, publishes to kafka
- PendingPaymentsReconciliationWorker: periodicly runs reconciliation for stale pending payment/refunds

dual-path finalization:

supporting stripe-webhoock and handle it in HandleStripeWebhookCommandHandler, BUT if webhook
delayed/missed, worker pools stale pending record and finalized from Stripe status api

BUT: as fallback if webhook is delayed/missing we have ReconcilePendingPaymentsCommandHandler
do reconciliation (polling worker) - self healing path

webhook - default way, push path
reconciliation - pull fallback path

HandleStripeWebhookCommandHandler and ReconcilePendingPaymentsCommandHandler enqueue outbox rows,
kafka publishing done by bg worker


clean schema:
Sync final: request -> Stripe final status (Succeeded or Failed) -> save -> immediate response
Async final: request -> Stripe Pending/RequiresAction -> immediate response -> later webhook or reconciliation finalizes -> enqueue outbox row -> delivery worker publishes Kafka event -> Order saga resumes 


this service dont have proper exact one kafka semantic. We use "Effectively-once" for business outcomes which is good fit for payment systems.
stack is: at-least-once + idempotency + db unique constrains + outbox + reconciliation


small note about refund compensation with order:
- refund response now has status semantics (Succeeded or Pending), not just "ok/failed"
- if refund is Pending, order compensation can continue, because provider accepted it async
- if refund call is temporary unavailable/timeout, Order service persist retry record to db
- then Order bg worker (CompensationRefundRetryWorker) polls and retries with backoff
- if retries exhausted -> critical incident for manual ops intervention
- if Order compensation has `Uncertain` status with known PaymentId, it persists deferred verification and later calls `RefundWithStatusAsync`
- if Order compensation is `Uncertain` without PaymentId but with ProviderPaymentIntentId, it calls `CancelAuthorization` gRPC and Payment forwards cancel to provider (Stripe/fake/mock)