import type { NextRequest } from "next/server";
import { apiFetch } from "@/lib/api";
import { authConfigFor } from "@/lib/auth/config";
import { getSession } from "@/lib/auth/session";
import { brandingSlots, type BrandingSlot } from "@/lib/branding";

/**
 * A tenant's branding image (task F1) for the browser: logos, favicon and the sign-in image,
 * which Keycloak's sign-in pages also show. Published images are public; draft images (the live
 * preview) only with the administrator's session. The tenant is in the path so the sign-in pages
 * on Keycloak's address can name it.
 */
export async function GET(request: NextRequest, { params }: { params: Promise<{ tenant: string; slot: string }> }) {
  const { tenant, slot } = await params;
  if (!/^[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?$/.test(tenant) || !brandingSlots.includes(slot as BrandingSlot)) {
    return new Response(null, { status: 404 });
  }

  const draft = request.nextUrl.searchParams.get("draft") === "true";
  let token = "";
  if (draft) {
    const config = await authConfigFor(request);
    const session = config.tenant === tenant ? await getSession(config).catch(() => null) : null;
    if (!session) return new Response(null, { status: 404 });
    token = session.data.accessToken;
  }

  const response = await apiFetch(`/api/v1/branding/images/${slot}${draft ? "?draft=true" : ""}`, token, tenant);
  if (!response.ok) return new Response(null, { status: 404 });
  return new Response(response.body, {
    status: 200,
    headers: {
      "Content-Type": response.headers.get("content-type") ?? "application/octet-stream",
      "Cache-Control": draft ? "private, no-store" : "public, max-age=300",
      "X-Content-Type-Options": "nosniff",
      "Content-Security-Policy": "default-src 'none'",
    },
  });
}
