import "server-only";
import { cookies } from "next/headers";

// Server-only session helpers. The operator's JWT (obtained via /login, which
// proxies to the Gateway's existing /api/v1/auth/login) is stored in an
// httpOnly cookie so it never reaches client-side JS — same principle as
// OPS_CONSOLE_ADMIN_API_KEY in lib/opsConsole.ts.
const SESSION_COOKIE = "ops_console_session";

export async function getSessionToken(): Promise<string | undefined> {
  const store = await cookies();
  return store.get(SESSION_COOKIE)?.value;
}

export async function setSessionToken(token: string): Promise<void> {
  const store = await cookies();
  store.set(SESSION_COOKIE, token, {
    httpOnly: true,
    secure: process.env.NODE_ENV === "production",
    sameSite: "lax",
    path: "/",
    maxAge: 60 * 30, // 30 minutes, matches typical short-lived access tokens
  });
}

export async function clearSessionToken(): Promise<void> {
  const store = await cookies();
  store.delete(SESSION_COOKIE);
}
