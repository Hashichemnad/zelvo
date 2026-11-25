import { isBrandingImage, proxyBrandingImage } from "@/lib/branding-image";

/**
 * A tenant's published branding image (task F1) for the browser: logos, favicon and the sign-in
 * image, which Keycloak's sign-in pages also show. Published images are public, so no session is
 * read here; draft images have their own route (`draft`). The tenant is in the path so the
 * sign-in pages on Keycloak's address can name it.
 */
export async function GET(_request: Request, { params }: { params: Promise<{ tenant: string; slot: string }> }) {
  const { tenant, slot } = await params;
  if (!isBrandingImage(tenant, slot)) return new Response(null, { status: 404 });
  return proxyBrandingImage(`/api/v1/branding/images/${slot}`, "", tenant, "public, max-age=300");
}
