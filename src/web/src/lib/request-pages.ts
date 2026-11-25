import type { Payment } from "./payments";
import "server-only";
import { getJsonAs } from "./api";
import { getDisplayFormat } from "./display";
import type { DateDisplayOptions } from "./format";
import type { TicketFormView } from "./forms/types";
import type { IncidentSettings } from "./incidents";
import type { PublishedService, ServiceRequest, ServiceRequestAttachment } from "./catalog";
import type { ApprovalProcessView } from "./workflows";

/** Everything a service request page shows (task C2), loaded in parallel through the public API. */
export async function loadRequest(id: string, accessToken: string, tenant: string, locale: string) {
  const get = <T,>(path: string) => getJsonAs<T>(path, accessToken, tenant);
  const [request, settings, attachments, form, format, approvals, payments] = await Promise.all([
    get<ServiceRequest>(`/api/v1/service-requests/${encodeURIComponent(id)}`),
    get<IncidentSettings>("/api/v1/settings/incidents"),
    get<ServiceRequestAttachment[]>(`/api/v1/service-requests/${encodeURIComponent(id)}/attachments`),
    get<TicketFormView>(`/api/v1/service-requests/${encodeURIComponent(id)}/form`),
    getDisplayFormat(),
    get<ApprovalProcessView[]>(`/api/v1/service-requests/${encodeURIComponent(id)}/approvals`),
    // Task E8: the request's fee.
    get<Payment[]>(`/api/v1/service-requests/${encodeURIComponent(id)}/payments`),
  ]);
  // Names of the service's documents, for the files sent with them.
  const service = request?.service ? await get<PublishedService>(`/api/v1/catalog/published/${request.service.id}`) : null;
  const display: DateDisplayOptions = { locale, ...format };
  const documentNames = Object.fromEntries((service?.documents ?? []).map((d) => [d.key, (locale === "ar" ? d.name.ar : d.name.en) ?? d.name.en ?? d.name.ar ?? d.key]));
  return { request, settings, attachments: attachments ?? [], form: form?.form ? form : null, display, documentNames, approvals: approvals ?? [], payments: payments ?? [] };
}
