"use client";

import { useState } from "react";
import { useRouter } from "next/navigation";

export function SagaMutationActions({
  sagaId,
  status,
}: {
  sagaId: string;
  status: string;
}) {
  const router = useRouter();
  const [pending, setPending] = useState(false);
  const [message, setMessage] = useState<string | null>(null);

  const canCompensate = ["Running", "WaitingForEvent", "TimedOut"].includes(status);
  const canRetryCompensation = status === "FailedToCompensate";

  async function trigger(action: "compensate" | "retry-compensation") {
    setPending(true);
    setMessage(null);

    const response = await fetch(`/api/sagas/${sagaId}/${action}`, { method: "POST" });
    const body = await response.json().catch(() => ({}));

    setPending(false);
    setMessage(
      response.status === 401
        ? "Not signed in. Go to /login first."
        : response.status === 403
          ? "Signed in, but you don't have the Admin/SuperAdmin role."
          : body.message ?? body.error ?? (response.ok ? "Done." : "Action failed.")
    );

    router.refresh();
  }

  if (!canCompensate && !canRetryCompensation) return null;

  return (
    <div className="filters" style={{ marginTop: 16 }}>
      {canCompensate && (
        <button
          disabled={pending}
          onClick={() =>
            confirm("Force-compensate this saga now? This cannot be undone.") &&
            trigger("compensate")
          }
        >
          Force compensate
        </button>
      )}
      {canRetryCompensation && (
        <button disabled={pending} onClick={() => trigger("retry-compensation")}>
          Retry compensation
        </button>
      )}
      {message && <p className="count">{message}</p>}
    </div>
  );
}
