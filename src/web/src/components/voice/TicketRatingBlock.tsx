import "server-only";
import { getJsonAs } from "@/lib/api";
import type { TicketRating, VoiceSettings } from "@/lib/voice";
import { TicketHappiness } from "./VoiceViews";

/**
 * The happiness meter on a requester's resolved incident or service request (GOV-2, task C6): shown
 * while they may rate it, and their rating afterwards.
 */
export async function TicketRatingBlock({ ticketId, accessToken, tenant, csrfToken }: { ticketId: string; accessToken: string; tenant: string; csrfToken: string }) {
  const [rating, settings] = await Promise.all([
    getJsonAs<TicketRating>(`/api/v1/tickets/${encodeURIComponent(ticketId)}/happiness`, accessToken, tenant),
    getJsonAs<VoiceSettings>("/api/v1/settings/customer-voice", accessToken, tenant),
  ]);
  if (!rating || !settings?.happiness.enabled || !settings.happiness.askOnClosure || (!rating.canRate && !rating.rating)) {
    return null;
  }
  return <TicketHappiness ticketId={ticketId} settings={settings.happiness} rating={rating.rating} csrfToken={csrfToken} />;
}
