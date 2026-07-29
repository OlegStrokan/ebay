"use client";

import { useState } from "react";
import { useRouter } from "next/navigation";

export function AccountBar({ email }: { email: string }) {
  const router = useRouter();
  const [pending, setPending] = useState(false);

  async function signOut() {
    setPending(true);
    await fetch("/api/session/logout", { method: "POST" });
    router.push("/login");
    router.refresh();
  }

  return (
    <div className="account-bar">
      <span>Signed in as {email}</span>
      <button onClick={signOut} disabled={pending}>
        {pending ? "Signing out…" : "Sign out"}
      </button>
    </div>
  );
}
