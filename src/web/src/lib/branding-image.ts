import { apiFetch } from "@/lib/api";
import { brandingSlots, type BrandingSlot } from "@/lib/branding";

/** Whether the tenant and slot of a branding image address are well formed (task F1). */
export function isBrandingImage(tenant: string, slot: string): slot is BrandingSlot {
  return /^[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?$/.test(tenant) && brandingSlots.includes(slot as BrandingSlot);
}

/** Fetches a branding image from the API and passes it on with safe headers. */
export async function proxyBrandingImage(apiPath: string, token: string, tenant: string, cacheControl: string): Promise<Response> {
  const response = await apiFetch(apiPath, token, tenant);
  if (!response.ok) return new Response(null, { status: 404 });
  return new Response(response.body, {
    status: 200,
    headers: {
      "Content-Type": response.headers.get("content-type") ?? "application/octet-stream",
      "Cache-Control": cacheControl,
      "X-Content-Type-Options": "nosniff",
      "Content-Security-Policy": "default-src 'none'",
    },
  });
}
