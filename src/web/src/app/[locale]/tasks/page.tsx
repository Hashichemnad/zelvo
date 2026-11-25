import { getTranslations, setRequestLocale } from "next-intl/server";
import { MyTasks } from "@/components/catalog/RequestTasks";
import { getJsonAs } from "@/lib/api";
import type { RequestTask } from "@/lib/catalog";
import { getDisplayFormat } from "@/lib/display";
import { requireSignedIn } from "@/lib/signed-in";

interface TaskPage {
  items: RequestTask[];
}

export async function generateMetadata({ params }: PageProps<"/[locale]/tasks">) {
  const { locale } = await params;
  const t = await getTranslations({ locale, namespace: "catalog.tasks" });
  return { title: t("myTitle") };
}

/** My tasks (task C4): fulfilment tasks of service requests for the signed-in agent and their groups. */
export default async function TasksPage({ params }: PageProps<"/[locale]/tasks">) {
  const { locale } = await params;
  setRequestLocale(locale);
  const { session, tenant, user } = await requireSignedIn(locale, `/${locale}/tasks`);
  const t = await getTranslations({ locale, namespace: "catalog.tasks" });
  if (!user?.permissions.includes("requests.read")) {
    return <p role="alert">{t(user ? "noAccess" : "unavailable")}</p>;
  }
  const get = <T,>(path: string) => getJsonAs<T>(path, session.data.accessToken, tenant);
  const [mine, groups, format] = await Promise.all([
    get<TaskPage>("/api/v1/request-tasks?assignee=me&pageSize=100"),
    get<TaskPage>("/api/v1/request-tasks?group=mine&pageSize=100"),
    getDisplayFormat(),
  ]);
  return (
    <div className="space-y-6">
      <h1 className="text-3xl font-bold text-brand">{t("myTitle")}</h1>
      {!mine || !groups ? (
        <p role="alert">{t("unavailable")}</p>
      ) : (
        <MyTasks mine={mine.items} groups={groups.items} display={{ locale, ...format }} csrfToken={session.data.csrfToken} />
      )}
    </div>
  );
}
