import Link from "next/link";
import { notFound } from "next/navigation";
import { getSaga, getSagaCorrelation, getSagaEvents } from "@/lib/opsConsole";
import { SagaMutationActions } from "./SagaMutationActions";

export const dynamic = "force-dynamic";

export default async function SagaDetailPage({
  params,
}: {
  params: Promise<{ id: string }>;
}) {
  const { id } = await params;
  const saga = await getSaga(id);
  if (!saga) {
    notFound();
  }

  const steps = await getSagaEvents(id);
  const correlation = await getSagaCorrelation(id);

  return (
    <main>
      <Link href="/">&larr; Back to sagas</Link>
      <h1>Saga {saga.id}</h1>

      <dl className="detail-grid">
        <dt>Correlation Id</dt>
        <dd>{saga.correlationId}</dd>
        <dt>Type</dt>
        <dd>{saga.sagaType}</dd>
        <dt>Status</dt>
        <dd>
          <span className={`status status-${saga.status}`}>{saga.status}</span>
        </dd>
        <dt>Current step</dt>
        <dd>{saga.currentStep}</dd>
        <dt>Created</dt>
        <dd>{new Date(saga.createdAt).toLocaleString()}</dd>
        <dt>Updated</dt>
        <dd>{new Date(saga.updatedAt).toLocaleString()}</dd>
      </dl>

      <SagaMutationActions sagaId={saga.id} status={saga.status} />

      <h2>Related data</h2>
      <dl className="detail-grid">
        <dt>Tracking Id</dt>
        <dd>{correlation?.orderTrackingId || "—"}</dd>
      </dl>

      <h3>Payments</h3>
      {correlation && correlation.payments.length > 0 ? (
        <table>
          <thead>
            <tr>
              <th>Payment Id</th>
              <th>Status</th>
              <th>Amount</th>
              <th>Refunded</th>
              <th>Provider intent</th>
              <th>Created</th>
            </tr>
          </thead>
          <tbody>
            {correlation.payments.map((p) => (
              <tr key={p.paymentId}>
                <td>{p.paymentId}</td>
                <td>
                  <span className={`status status-${p.status}`}>{p.status}</span>
                </td>
                <td>
                  {p.amount} {p.currency}
                </td>
                <td>{p.totalRefundedAmount}</td>
                <td>{p.providerPaymentIntentId || "—"}</td>
                <td>{new Date(p.createdAt).toLocaleString()}</td>
              </tr>
            ))}
          </tbody>
        </table>
      ) : (
        <p className="count">No payments found for this order (or Payment service unreachable).</p>
      )}

      <h3>Inventory reservation</h3>
      {correlation?.reservation ? (
        <>
          <dl className="detail-grid">
            <dt>Reservation Id</dt>
            <dd>{correlation.reservation.reservationId}</dd>
            <dt>Status</dt>
            <dd>
              <span className={`status status-${correlation.reservation.status}`}>
                {correlation.reservation.status}
              </span>
            </dd>
            <dt>Updated</dt>
            <dd>{new Date(correlation.reservation.updatedAt).toLocaleString()}</dd>
          </dl>
          <ul>
            {correlation.reservation.items.map((i) => (
              <li key={i.productId}>
                {i.productId} × {i.quantity}
              </li>
            ))}
          </ul>
        </>
      ) : (
        <p className="count">No reservation found for this order (or Inventory service unreachable).</p>
      )}

      <h2>Timeline</h2>
      <ol className="timeline">
        {steps.map((step, i) => (
          <li key={`${step.stepName}-${i}`} className="step">
            <div className="step-header">
              <strong>{step.stepName}</strong>
              <span className={`status status-${step.status}`}>{step.status}</span>
            </div>
            <div className="step-meta">
              Started {new Date(step.startedAt).toLocaleString()}
              {step.completedAt && ` · Completed ${new Date(step.completedAt).toLocaleString()}`}
              {step.durationMs > 0 && ` · ${step.durationMs}ms`}
            </div>
            {step.errorMessage && <div className="step-error">{step.errorMessage}</div>}
            {step.request && (
              <details className="step-meta">
                <summary>Request</summary>
                <pre className="payload">{step.request}</pre>
              </details>
            )}
            {step.response && (
              <details className="step-meta">
                <summary>Response</summary>
                <pre className="payload">{step.response}</pre>
              </details>
            )}
          </li>
        ))}
        {steps.length === 0 && <li>No step history recorded.</li>}
      </ol>
    </main>
  );
}
