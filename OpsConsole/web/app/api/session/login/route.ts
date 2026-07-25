import { setSessionToken } from "@/lib/session";

// Proxies to the Gateway's existing /api/v1/auth/login so operators log in
// with their normal account (must hold the Admin/SuperAdmin role to actually
// use any mutating action — enforced server-side by OpsConsole's OpsAdmin
// policy, not by this route). The access token is stored httpOnly server-side
// and never returned to client JS.
const GATEWAY_URL = process.env.GATEWAY_API_URL ?? "http://localhost:5081";

export async function POST(request: Request) {
  const body = await request.json().catch(() => null);
  const email = typeof body?.email === "string" ? body.email : "";
  const password = typeof body?.password === "string" ? body.password : "";

  if (!email || !password) {
    return Response.json({ error: "Email and password are required." }, { status: 400 });
  }

  const response = await fetch(`${GATEWAY_URL}/api/v1/auth/login`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ email, password }),
    cache: "no-store",
  });

  if (!response.ok) {
    return Response.json({ error: "Invalid credentials." }, { status: 401 });
  }

  const payload = await response.json();
  const accessToken: string | undefined = payload?.data?.accessToken;

  if (!accessToken) {
    return Response.json({ error: "Login succeeded but no access token was returned." }, { status: 502 });
  }

  await setSessionToken(accessToken);
  return Response.json({ ok: true });
}
