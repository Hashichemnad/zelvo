export const dynamic = "force-dynamic";

/** Liveness endpoint for the container health check. */
export function GET() {
  return Response.json({ status: "Healthy" });
}
