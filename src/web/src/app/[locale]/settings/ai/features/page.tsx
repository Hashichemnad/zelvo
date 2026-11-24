import { getTranslations, setRequestLocale } from "next-intl/server";
import { AiFeaturesPanel } from "@/components/settings/ai/AiFeaturesPanel";
import type { AiFeature, AiProvider } from "@/lib/ai";
import type { TriageAccuracy } from "@/lib/ai-features";
import { getJsonAs } from "@/lib/api";
import { requireSignedIn } from "@/lib/signed-in";

export async function generateMetadata({ params }: PageProps<"/[locale]/settings/ai/features">) {
  const { locale } = await params;
  const t = await getTranslations({ locale, namespace: "settings.ai.tabs" });
  return { title: t("features") };
}

/** Settings → AI → Features: provider and model per feature. */
export default async function AiFeaturesPage({ params }: PageProps<"/[locale]/settings/ai/features">) {
  const { locale } = await params;
  setRequestLocale(locale);
  const { session, tenant, user } = await requireSignedIn(locale, `/${locale}/settings/ai/features`);
  if (!user?.permissions.includes("ai.manage")) {
    return null;
  }
  const token = session.data.accessToken;
  const [features, providers, triage, accuracy, t] = await Promise.all([
    getJsonAs<AiFeature[]>("/api/v1/ai/features", token, tenant),
    getJsonAs<AiProvider[]>("/api/v1/ai/providers", token, tenant),
    getJsonAs<{ autoApply: boolean }>("/api/v1/ai/triage/settings", token, tenant),
    getJsonAs<TriageAccuracy>("/api/v1/ai/triage/accuracy", token, tenant),
    getTranslations({ locale, namespace: "settings" }),
  ]);
  if (!features || !providers) {
    return <p role="alert">{t("unavailable")}</p>;
  }
  return (
    <AiFeaturesPanel initial={features} providers={providers} csrfToken={session.data.csrfToken}
      triage={{ autoApply: triage?.autoApply ?? false, accuracy }} />
  );
}
