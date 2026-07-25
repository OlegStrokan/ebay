"use client";

import { useState } from "react";
import { useRouter } from "next/navigation";

export function DeadLetterRequeueButton({ messageId }: { messageId: string }) {
  const router = useRouter();
  const [pending, setPending] = useState(false);
  const [message, setMessage] = useState<string | null>(null);

  async function requeue() {
    if (!confirm("Requeue this message for redelivery?")) return;

    setPending(true);
    setMessage(null);

    const response = await fetch(`/api/deadletters/${messageId}/requeue`, { method: "POST" });
    const body = await response.json().catch(() => ({}));

    setPending(false);
    setMessage(
      response.status === 401
        ? "Not signed in."
        : response.status === 403
          ? "Missing Admin/SuperAdmin role."
          : (body.message ?? body.error ?? (response.ok ? "Done." : "Failed."))
    );

    router.refresh();
  }

  return (
    <>
      <button disabled={pending} onClick={requeue}>
        Requeue
      </button>
      {message && <div className="step-meta">{message}</div>}
    </>
  );
}
