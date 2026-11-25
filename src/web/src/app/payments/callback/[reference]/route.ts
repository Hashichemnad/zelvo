import { NextResponse, type NextRequest } from "next/server";
import { apiFetch } from "@/lib/api";
import { authConfigFor } from "@/lib/auth/config";

/**
 * The payment gateway's direct notification (task E8): forwarded unchanged (body and signature
 * headers) to the API, which checks the signature the connector names and then confirms the
 * result with the gateway's status API. No session and no cookies: it always answers 200.
 */
export async function POST(request: NextRequest, { params }: { params: Promise<{ reference: string }> }) {
  const { reference } = await params;
  if (!/^[A-Z0-9]{8,40}$/.test(reference)) {
    return new NextResponse(null, { status: 200 });
  }
  const config = await authConfigFor(request);
  const body = await request.arrayBuffer();
  if (body.byteLength > 64 * 1024) {
    return new NextResponse(null, { status: 200 });
  }
  const headers = new Headers();
  for (const [name, value] of request.headers) {
    const lower = name.toLowerCase();
    // The content type and the gateway's own headers (signatures); never cookies or authorization.
    if (lower === "content-type" || (lower.startsWith("x-") && !lower.startsWith("x-forwarded") && lower !== "x-zelvo-tenant")) {
      headers.set(name, value);
    }
  }
  try {
    await apiFetch(`/api/v1/payments/callback/${reference}`, "", config.tenant, { method: "POST", headers, body });
  } catch {
    // The gateway may retry; Zelvo also asks the gateway itself.
  }
  return new NextResponse(null, { status: 200 });
}
