import "server-only";

// Server-only client for the OpsConsole backend (Minimal API in ../../Program.cs).
// Only ever called from Server Components, so OPS_CONSOLE_ADMIN_API_KEY never
// reaches the browser bundle. Do NOT prefix these env vars with NEXT_PUBLIC_.
const BASE_URL = process.env.OPS_CONSOLE_API_URL ?? "http://localhost:5300";
const API_KEY = process.env.OPS_CONSOLE_ADMIN_API_KEY ?? "";

export type SagaSummary = {
  id: string;
  correlationId: string;
  sagaType: string;
  status: string;
  currentStep: string;
  createdAt: string;
  updatedAt: string;
};

export type GetSagasResult = {
  sagas: SagaSummary[];
  totalCount: number;
};

export type SagaDetail = {
  found: boolean;
  id: string;
  correlationId: string;
  sagaType: string;
  status: string;
  currentStep: string;
  createdAt: string;
  updatedAt: string;
};

export type SagaStepEvent = {
  stepName: string;
  status: string;
  errorMessage: string;
  startedAt: string;
  completedAt: string;
  durationMs: number;
};

export type SagaFilters = {
  status?: string;
  sagaType?: string;
  search?: string;
  skip?: number;
  take?: number;
};

async function opsConsoleFetch<T>(path: string): Promise<T> {
  const response = await fetch(`${BASE_URL}${path}`, {
    headers: { "X-Admin-Api-Key": API_KEY },
    cache: "no-store",
  });

  if (!response.ok) {
    throw new Error(`OpsConsole API request failed (${response.status}): ${path}`);
  }

  return (await response.json()) as T;
}

export async function getSagas(filters: SagaFilters): Promise<GetSagasResult> {
  const params = new URLSearchParams();
  if (filters.status) params.set("status", filters.status);
  if (filters.sagaType) params.set("sagaType", filters.sagaType);
  if (filters.search) params.set("search", filters.search);
  params.set("skip", String(filters.skip ?? 0));
  params.set("take", String(filters.take ?? 25));

  const data = await opsConsoleFetch<Partial<GetSagasResult>>(`/api/sagas?${params.toString()}`);
  return { sagas: data.sagas ?? [], totalCount: data.totalCount ?? 0 };
}

export async function getSaga(id: string): Promise<SagaDetail | null> {
  const response = await fetch(`${BASE_URL}/api/sagas/${encodeURIComponent(id)}`, {
    headers: { "X-Admin-Api-Key": API_KEY },
    cache: "no-store",
  });

  if (response.status === 404) return null;
  if (!response.ok) {
    throw new Error(`OpsConsole API request failed (${response.status}): /api/sagas/${id}`);
  }

  return (await response.json()) as SagaDetail;
}

export async function getSagaEvents(id: string): Promise<SagaStepEvent[]> {
  const data = await opsConsoleFetch<{ steps?: SagaStepEvent[] }>(
    `/api/sagas/${encodeURIComponent(id)}/events`
  );
  return data.steps ?? [];
}

export type DeadLetterSummary = {
  id: string;
  type: string;
  aggregateId: string;
  failureReason: string;
  retryCount: number;
  movedToDeadLetterAt: string;
};

export type DeadLetterFilters = {
  skip?: number;
  take?: number;
};

export async function getDeadLetters(
  filters: DeadLetterFilters
): Promise<DeadLetterSummary[]> {
  const params = new URLSearchParams();
  params.set("skip", String(filters.skip ?? 0));
  params.set("take", String(filters.take ?? 50));

  const data = await opsConsoleFetch<{ messages?: DeadLetterSummary[] }>(
    `/api/deadletters?${params.toString()}`
  );
  return data.messages ?? [];
}
