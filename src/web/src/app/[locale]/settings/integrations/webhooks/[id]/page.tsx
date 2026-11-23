import { notFound } from "next/navigation";
import { getTranslations, setRequestLocale } from "next-intl/server";
import { WebhookDetailPanel } from "@/components/settings/integrations/WebhookDetailPanel";
import { Link } from "@/i18n/navigation";
import { getJsonAs } from "@/lib/api";
import { getDisplayFormat } from "@/lib/display";
import type { Page, Webhook, WebhookDelivery } from "@/lib/integrations";
import { requireSignedIn } from "@/lib/signed-in";

const uuid = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

export async function generateMetadata({ params }: PageProps<"/[locale]/settings/integrations/webhooks/[id]">) {
  const { locale } = await params;
  const t = await getTranslations({ locale, namespace: "settings.integrations.tabs" });
  return { title: t("webhooks") };
}

/** One webhook: tester, settings and delivery log. */
export default async function WebhookPage({ params }: PageProps<"/[locale]/settings/integrations/webhooks/[id]">) {
  const { locale, id } = await params;
  setRequestLocale(locale);
  if (!uuid.test(id)) {
    notFound();
  }
  const { session, tenant, user } = await requireSignedIn(locale, `/${locale}/settings/integrations/webhooks/${id}`);
  if (!user?.permissions.includes("integrations.manage")) {
    return null;
  }
  const token = session.data.accessToken;
  const [webhook, deliveries, display, t] = await Promise.all([
    getJsonAs<Webhook>(`/api/v1/integrations/webhooks/${id}`, token, tenant),
    getJsonAs<Page<WebhookDelivery>>(`/api/v1/integrations/webhooks/${id}/deliveries?pageSize=50`, token, tenant),
    getDisplayFormat(),
    getTranslations({ locale, namespace: "settings.integrations.webhook" }),
  ]);
  if (!webhook || !deliveries) {
    notFound();
  }
  return (
    <div className="space-y-4">
      <Link href="/settings/integrations/webhooks" className="font-semibold text-brand underline">
        {t("back")}
      </Link>
      <h3 className="text-xl font-semibold">{webhook.name}</h3>
      <WebhookDetailPanel initial={webhook} initialDeliveries={deliveries} csrfToken={session.data.csrfToken} display={{ locale, ...display }} />
    </div>
  );
}
