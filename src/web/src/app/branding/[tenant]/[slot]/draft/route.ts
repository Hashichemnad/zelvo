import type { NextRequest } from "next/server";
import { authConfigFor } from "@/lib/auth/config";
import { getSession } from "@/lib/auth/session";
import { isBrandingImage, proxyBrandingImage } from "@/lib/branding-image";

/**
 * A draft branding image (task F1) for the live preview in Settings → Branding: only with a
 * session of the same tenant; the API checks `branding.manage`.
 */
export async function GET(request: NextRequest, { params }: { params: Promise<{ tenant: string; slot: string }> }) {
  const { tenant, slot } = await params;
  if (!isBrandingImage(tenant, slot)) return new Response(null, { status: 404 });
  const config = await authConfigFor(request);
  if (config.tenant !== tenant) return new Response(null, { status: 404 });
  const session = await getSession(config).catch(() => null);
  if (!session) return new Response(null, { status: 404 });
  return proxyBrandingImage(`/api/v1/branding/images/${slot}?draft=true`, session.data.accessToken, tenant, "private, no-store");
}
