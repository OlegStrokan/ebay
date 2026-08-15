export function decodeJwtPayload<T = Record<string, unknown>>(token: string): T | null {
  try {
    const payloadSegment = token.split(".")[1];
    return JSON.parse(Buffer.from(payloadSegment, "base64url").toString("utf8")) as T;
  } catch {
    return null;
  }
}

// a few seconds of skew so a token that expires between this check and the
// backend receiving the forwarded request still triggers a refresh here,
// instead of a 401 there
const EXP_SKEW_SECONDS = 5;

export function isJwtExpired(token: string): boolean {
  const payload = decodeJwtPayload<{ exp?: number }>(token);
  if (!payload?.exp) return true;
  return payload.exp <= Math.floor(Date.now() / 1000) + EXP_SKEW_SECONDS;
}
