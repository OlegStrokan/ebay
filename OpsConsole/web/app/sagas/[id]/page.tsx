import Link from "next/link";
import { notFound } from "next/navigation";
import { getSaga, getSagaEvents } from "@/lib/opsConsole";
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
          </li>
        ))}
        {steps.length === 0 && <li>No step history recorded.</li>}
      </ol>
    </main>
  );
}
