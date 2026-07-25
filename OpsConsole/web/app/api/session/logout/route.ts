import { clearSessionToken } from "@/lib/session";

export async function POST() {
  await clearSessionToken();
  return Response.json({ ok: true });
}
