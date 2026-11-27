import type { NextRequest } from "next/server";
import { getTranslations } from "next-intl/server";
import { getAppInfo, getBranding } from "@/lib/api";
import { authConfigFor } from "@/lib/auth/config";
import { buildManifest } from "@/lib/pwa/manifest";

/**
 * Task J1 (module 19): the web app manifest of the host the request came to. The host decides the
 * tenant (its own address or a verified custom domain, task F1), so each customer's app installs
 * with its own name, colours and icon. `?lang=ar` gives the Arabic manifest (right to left).
 */
export async function GET(request: NextRequest) {
  const locale = request.nextUrl.searchParams.get("lang") === "ar" ? "ar" : "en";
  const config = await authConfigFor(request);
  const [branding, app, t] = await Promise.all([getBranding(config.tenant), getAppInfo(config.tenant), getTranslations({ locale, namespace: "app" })]);
  const manifest = buildManifest({ locale, tenant: config.tenant, branding, app, description: t("description") });
  return new Response(JSON.stringify(manifest), {
    headers: {
      "Content-Type": "application/manifest+json; charset=utf-8",
      "Cache-Control": "public, max-age=300",
      "X-Content-Type-Options": "nosniff",
    },
  });
}
