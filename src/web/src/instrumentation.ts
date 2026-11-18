import { registerOTel } from "@vercel/otel";
import { RedactingSpanProcessor } from "@/lib/telemetry-redaction";

/**
 * OpenTelemetry for the web app's server (A5): traces of pages, sign-in and the API proxy, sent
 * over OTLP to OTEL_EXPORTER_OTLP_ENDPOINT (the collector in Compose). Calls to the API carry the
 * trace context, so one trace covers the browser request, this server and the API. Nothing is
 * collected when no endpoint is set. Query strings, client addresses and user ids are removed.
 */
export function register() {
  if (!process.env.OTEL_EXPORTER_OTLP_ENDPOINT) {
    return;
  }
  const api = process.env.API_INTERNAL_URL ?? "http://localhost:8080";
  registerOTel({
    serviceName: "zelvo-web",
    spanProcessors: [new RedactingSpanProcessor(), "auto"],
    instrumentationConfig: {
      fetch: {
        propagateContextUrls: [api],
        ignoreUrls: [/\/health\//],
      },
    },
  });
}
