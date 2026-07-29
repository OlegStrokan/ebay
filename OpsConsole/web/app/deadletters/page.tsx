import Link from "next/link";
import { getDeadLetters } from "@/lib/opsConsole";
import { DeadLetterRequeueButton } from "./DeadLetterRequeueButton";

export const dynamic = "force-dynamic";

const TAKE = 25;

type SearchParams = {
  skip?: string;
};

export default async function DeadLettersPage({
  searchParams,
}: {
  searchParams: Promise<SearchParams>;
}) {
  const resolvedSearchParams = await searchParams;
  const skip = Number(resolvedSearchParams.skip ?? 0) || 0;

  const messages = await getDeadLetters({ skip, take: TAKE });

  // The backend doesn't return a total count for dead letters (unbounded by
  // design — see admin_ops.proto), so "next" is a heuristic: a full page
  // suggests there may be more.
  const hasPrev = skip > 0;
  const hasNext = messages.length === TAKE;

  return (
    <main>
      <p>
        <Link href="/">&larr; Sagas</Link>
      </p>
      <h1>Dead letters</h1>
      <p className="count">
        Showing unresolved messages {skip + 1}–{skip + messages.length}
      </p>

      <table>
        <thead>
          <tr>
            <th>Type</th>
            <th>Aggregate Id</th>
            <th>Failure reason</th>
            <th>Payload</th>
            <th>Retries</th>
            <th>Moved to DLQ</th>
            <th>Action</th>
          </tr>
        </thead>
        <tbody>
          {messages.map((m) => (
            <tr key={m.id}>
              <td>{m.type}</td>
              <td>{m.aggregateId}</td>
              <td>{m.failureReason}</td>
              <td>
                {m.payload ? (
                  <details>
                    <summary>View</summary>
                    <pre className="payload">{m.payload}</pre>
                  </details>
                ) : (
                  "—"
                )}
              </td>
              <td>{m.retryCount}</td>
              <td>{new Date(m.movedToDeadLetterAt).toLocaleString()}</td>
              <td>
                <DeadLetterRequeueButton messageId={m.id} />
              </td>
            </tr>
          ))}
          {messages.length === 0 && (
            <tr>
              <td colSpan={7}>No dead-letter messages.</td>
            </tr>
          )}
        </tbody>
      </table>

      <nav className="pagination">
        {hasPrev && <Link href={`/deadletters?skip=${Math.max(skip - TAKE, 0)}`}>Previous</Link>}
        {hasNext && <Link href={`/deadletters?skip=${skip + TAKE}`}>Next</Link>}
      </nav>
    </main>
  );
}
