import { proxyBrandingImage } from "@/lib/branding-image";
import { appIconNames, type AppIconName } from "@/lib/pwa/manifest";

/**
 * Task J1: a size of a tenant's published app icon for the installed web app (manifest icons and
 * the iOS home-screen icon), made by the API from the tenant's square PNG. Public, like the other
 * published branding images; the tenant is in the path so the address names it.
 */
export async function GET(_request: Request, { params }: { params: Promise<{ tenant: string; name: string }> }) {
  const { tenant, name } = await params;
  if (!/^[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?$/.test(tenant) || !appIconNames.includes(name as AppIconName)) {
    return new Response(null, { status: 404 });
  }
  return proxyBrandingImage(`/api/v1/branding/app-icons/${name}`, "", tenant, "public, max-age=300");
}
