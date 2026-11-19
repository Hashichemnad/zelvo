import { SamplingDecision, type ReadableSpan, type Sampler, type SamplingResult, type SpanProcessor } from "@opentelemetry/sdk-trace-base";

/** Attributes that are dropped from every span: they identify the person. */
const dropped = new Set(["http.client_ip", "client.address", "http.referer", "enduser.id", "user.id", "user.email"]);

/** Attributes that hold addresses; their query string (search terms, sign-in codes) is removed. */
const addresses = new Set(["http.url", "url.full", "http.target", "url.query", "next.rsc", "http.referer"]);

/** Removes the query string (and fragment) from an address, keeping its path. */
export function withoutQuery(value: string): string {
  const cut = value.search(/[?#]/);
  return cut === -1 ? value : value.slice(0, cut);
}

/** Redacts a span's attributes in place: no query strings, no client addresses or user ids. */
export function redactAttributes(attributes: Record<string, unknown>): void {
  for (const key of Object.keys(attributes)) {
    if (dropped.has(key) || key === "url.query") {
      delete attributes[key];
    } else if (addresses.has(key) && typeof attributes[key] === "string") {
      attributes[key] = withoutQuery(attributes[key] as string);
    }
  }
}

/**
 * Span processor that runs before the exporting one: telemetry from the web app never carries
 * query strings, client addresses or user ids (personal data stays out of traces).
 */
export class RedactingSpanProcessor implements SpanProcessor {
  onStart(): void {}

  onEnd(span: ReadableSpan): void {
    redactAttributes(span.attributes as Record<string, unknown>);
  }

  forceFlush(): Promise<void> {
    return Promise.resolve();
  }

  shutdown(): Promise<void> {
    return Promise.resolve();
  }
}

/** Whether a span name is a health probe (`GET /healthz`), polled every few seconds by Compose or Kubernetes. */
export function isProbe(spanName: string): boolean {
  return /\s\/(healthz|health\/\w+)$/.test(spanName);
}

/** Records every trace except health probes, which would otherwise fill the trace store. */
export class SkipProbesSampler implements Sampler {
  shouldSample(_context: unknown, _traceId: string, spanName: string): SamplingResult {
    return { decision: isProbe(spanName) ? SamplingDecision.NOT_RECORD : SamplingDecision.RECORD_AND_SAMPLED };
  }

  toString(): string {
    return "SkipProbesSampler";
  }
}
