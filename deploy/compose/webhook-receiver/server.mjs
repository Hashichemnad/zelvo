// Development webhook receiver (Compose only). Records the webhooks Zelvo sends so they can be
// looked at (GET /received) and checked by the end-to-end tests. Never part of an installation.
import { createServer } from "node:http";

const port = Number(process.env.PORT ?? 8096);
const received = [];
const maxKept = 200;

createServer((request, response) => {
  const url = new URL(request.url ?? "/", "http://localhost");
  if (request.method === "GET" && url.pathname === "/healthz") {
    response.writeHead(200, { "content-type": "text/plain" }).end("ok");
    return;
  }
  if (request.method === "GET" && url.pathname === "/received") {
    response.writeHead(200, { "content-type": "application/json" }).end(JSON.stringify(received));
    return;
  }
  if (request.method === "POST" && url.pathname === "/hook") {
    const chunks = [];
    let size = 0;
    request.on("data", (chunk) => {
      size += chunk.length;
      if (size <= 1_000_000) {
        chunks.push(chunk);
      }
    });
    request.on("end", () => {
      received.unshift({
        receivedAt: new Date().toISOString(),
        id: request.headers["webhook-id"] ?? null,
        timestamp: request.headers["webhook-timestamp"] ?? null,
        signature: request.headers["webhook-signature"] ?? null,
        event: request.headers["zelvo-event"] ?? null,
        environment: request.headers["zelvo-environment"] ?? null,
        body: Buffer.concat(chunks).toString("utf8"),
      });
      received.length = Math.min(received.length, maxKept);
      const status = Number(url.searchParams.get("status") ?? 200);
      response.writeHead(status >= 200 && status <= 599 ? status : 200, { "content-type": "application/json" }).end('{"received":true}');
    });
    return;
  }
  response.writeHead(404).end();
}).listen(port, () => console.log(`webhook receiver on ${port}`));
