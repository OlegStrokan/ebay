import { clearSessionTokens, getRefreshToken, getSessionToken } from "@/lib/session";

const GATEWAY_URL = process.env.GATEWAY_API_URL ?? "http://localhost:5081";

export async function POST() {
  const accessToken = await getSessionToken();
  const refreshToken = await getRefreshToken();

  // Best-effort: revoke the refresh token server-side so it can't be replayed
  // after logout. Never block clearing the local session on this succeeding.
  if (accessToken && refreshToken) {
    try {
      await fetch(`${GATEWAY_URL}/api/v1/auth/revoke`, {
        method: "POST",
        headers: {
          "Content-Type": "application/json",
          Authorization: `Bearer ${accessToken}`,
        },
        body: JSON.stringify({ refreshToken }),
        cache: "no-store",
      });
    } catch {
      // Gateway unreachable — still clear the local cookies below.
    }
  }

  await clearSessionTokens();
  return Response.json({ ok: true });
}
