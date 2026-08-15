import { NextRequest, NextResponse } from "next/server";
import {
  ACCESS_COOKIE,
  DEFAULT_ACCESS_MAX_AGE_SECONDS,
  REFRESH_COOKIE,
  REFRESH_MAX_AGE_SECONDS,
} from "@/lib/sessionCookies";
import { isJwtExpired } from "@/lib/jwt";

const GATEWAY_URL = process.env.GATEWAY_API_URL ?? "http://localhost:5081";

const cookieOptions = {
  httpOnly: true,
  secure: process.env.NODE_ENV === "production",
  sameSite: "lax" as const,
  path: "/",
};

export async function middleware(request: NextRequest) {
  const accessToken = request.cookies.get(ACCESS_COOKIE)?.value;
  if (accessToken && !isJwtExpired(accessToken)) {
    return NextResponse.next();
  }

  const refreshToken = request.cookies.get(REFRESH_COOKIE)?.value;
  if (refreshToken) {
    const refreshed = await tryRefresh(refreshToken);
    if (refreshed) {
      const response = NextResponse.next();
      response.cookies.set(ACCESS_COOKIE, refreshed.accessToken, {
        ...cookieOptions,
        maxAge: refreshed.expiresIn ?? DEFAULT_ACCESS_MAX_AGE_SECONDS,
      });
      response.cookies.set(REFRESH_COOKIE, refreshed.refreshToken, {
        ...cookieOptions,
        maxAge: REFRESH_MAX_AGE_SECONDS,
      });
      return response;
    }
  }

  const loginUrl = new URL("/login", request.url);
  loginUrl.searchParams.set("from", request.nextUrl.pathname + request.nextUrl.search);
  loginUrl.searchParams.set("reason", "expired");

  const response = NextResponse.redirect(loginUrl);
  response.cookies.delete(ACCESS_COOKIE);
  response.cookies.delete(REFRESH_COOKIE);
  return response;
}

async function tryRefresh(
  refreshToken: string
): Promise<{ accessToken: string; refreshToken: string; expiresIn?: number } | null> {
  try {
    const response = await fetch(`${GATEWAY_URL}/api/v1/auth/refresh`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ refreshToken }),
      cache: "no-store",
    });

    if (!response.ok) return null;

    const payload = await response.json();
    const accessToken: string | undefined = payload?.data?.accessToken;
    const newRefreshToken: string | undefined = payload?.data?.refreshToken;
    const expiresIn: number | undefined = payload?.data?.expiresIn;

    if (!accessToken || !newRefreshToken) return null;

    return { accessToken, refreshToken: newRefreshToken, expiresIn };
  } catch {
    return null;
  }
}

export const config = {
  matcher: ["/((?!login|api/session|_next/static|_next/image|favicon.ico).*)"],
};
