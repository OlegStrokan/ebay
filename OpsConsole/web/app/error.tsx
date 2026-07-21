"use client";

export default function Error({
  reset,
}: {
  error: Error & { digest?: string };
  reset: () => void;
}) {
  return (
    <main>
      <h1>Something went wrong</h1>
      <p>
        Could not load data from the Ops Console API. It may be unreachable or
        misconfigured (check <code>OPS_CONSOLE_API_URL</code> and the shared
        API keys).
      </p>
      <button onClick={() => reset()}>Try again</button>
    </main>
  );
}
