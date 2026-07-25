import { getSessionToken } from "@/lib/session";

const BASE_URL = process.env.OPS_CONSOLE_API_URL ?? "http://localhost:5300";
const API_KEY = process.env.OPS_CONSOLE_ADMIN_API_KEY ?? "";

export async function POST(
  _request: Request,
  { params }: { params: Promise<{ id: string }> }
) {
  const { id } = await params;
  const token = await getSessionToken();

  if (!token) {
    return Response.json({ error: "Not logged in." }, { status: 401 });
  }

  const response = await fetch(
    `${BASE_URL}/api/sagas/${encodeURIComponent(id)}/compensate`,
    {
      method: "POST",
      headers: {
        "X-Admin-Api-Key": API_KEY,
        Authorization: `Bearer ${token}`,
      },
      cache: "no-store",
    }
  );

  const body = await response.json().catch(() => ({}));
  return Response.json(body, { status: response.status });
}
