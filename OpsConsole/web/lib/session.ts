import "server-only";
import { cookies } from "next/headers";
import {
  ACCESS_COOKIE,
  DEFAULT_ACCESS_MAX_AGE_SECONDS,
  REFRESH_COOKIE,
  REFRESH_MAX_AGE_SECONDS,
} from "./sessionCookies";
import { decodeJwtPayload } from "./jwt";

// Server-only session helpers. The operator's JWT (obtained via /login, which
// proxies to the Gateway's existing /api/v1/auth/login) is stored in an
// httpOnly cookie so it never reaches client-side JS — same principle as
// OPS_CONSOLE_ADMIN_API_KEY in lib/opsConsole.ts.
//
// A refresh token (also httpOnly) is stored alongside it so middleware.ts can
// silently mint a new access token when the short-lived one expires, instead
// of forcing a full re-login every time the access-token cookie's maxAge
// elapses.
const cookieOptions = {
  httpOnly: true,
  secure: process.env.NODE_ENV === "production",
  sameSite: "lax" as const,
  path: "/",
};

export async function getSessionToken(): Promise<string | undefined> {
  const store = await cookies();
  return store.get(ACCESS_COOKIE)?.value;
}

export async function getRefreshToken(): Promise<string | undefined> {
  const store = await cookies();
  return store.get(REFRESH_COOKIE)?.value;
}

export async function setSessionTokens(
  accessToken: string,
  expiresInSeconds: number | undefined,
  refreshToken?: string
): Promise<void> {
  const store = await cookies();
  store.set(ACCESS_COOKIE, accessToken, {
    ...cookieOptions,
    maxAge: expiresInSeconds ?? DEFAULT_ACCESS_MAX_AGE_SECONDS,
  });

  if (refreshToken) {
    store.set(REFRESH_COOKIE, refreshToken, {
      ...cookieOptions,
      maxAge: REFRESH_MAX_AGE_SECONDS,
    });
  }
}

export async function clearSessionTokens(): Promise<void> {
  const store = await cookies();
  store.delete(ACCESS_COOKIE);
  store.delete(REFRESH_COOKIE);
}

// Presentation only (session indicator in the header) — NOT a trust boundary.
// The access token's signature/expiry is verified server-side by OpsConsole's
// own [Authorize] policies on every API call; this just reads the "email"
// claim back out for display without needing a JWT library.
export async function getSessionEmail(): Promise<string | null> {
  const token = await getSessionToken();
  if (!token) return null;

  const payload = decodeJwtPayload<{ email?: string }>(token);
  return typeof payload?.email === "string" ? payload.email : null;
}
