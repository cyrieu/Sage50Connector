// Local, no-disk-payload proxy for the real invoice ingest E2E. It forwards
// bytes unchanged, retains only per-record hashes/counts, and never logs auth.
// Usage: node InvoiceIngestCaptureProxy.js <listen-port> <backend-port> <result.json> [progress.json] [pause-first-commit-ms]
const crypto = require("crypto");
const fs = require("fs");
const http = require("http");
const path = require("path");

const listenPort = Number(process.argv[2] || 4007);
const backendPort = Number(process.argv[3] || 4008);
const resultPath = path.resolve(process.argv[4] || "invoice-ingest-capture.json");
const progressPath = process.argv[5] ? path.resolve(process.argv[5]) : null;
const pauseFirstCommitMs = Number(process.argv[6] || 0);
const expectedFingerprint = process.argv[7] ||
  "313e26a5d58da77abe51cf9da378b722ca46338f160e9e1ec2da325a167cdffd";
const maxBodyBytes = 32 * 1024 * 1024;

const summary = {
  status: "running",
  listenPort,
  backendPort,
  reports: 0,
  reportRows: 0,
  uniqueIds: 0,
  duplicateIds: 0,
  missingIds: 0,
  pageSizes: [],
  pagesWithNextCursor: 0,
  responseStatuses: {},
  expectedFingerprint,
  actualFingerprint: null,
  fingerprintMatches: false,
  duplicatePayloadMismatches: 0,
  firstCommitComplete: false,
};
const seen = new Set();
const fingerprints = new Map();

function hash(value) {
  return crypto.createHash("sha256").update(value, "utf8").digest("hex");
}

function rawDataObjects(text) {
  const match = /"data"\s*:\s*\[/g.exec(text);
  if (!match) return [];
  const result = [];
  let index = match.index + match[0].length;
  while (index < text.length) {
    while (/\s|,/.test(text[index] || "")) index++;
    if (text[index] === "]") break;
    if (text[index] !== "{") return [];
    const start = index;
    let depth = 0;
    let inString = false;
    let escaped = false;
    for (; index < text.length; index++) {
      const character = text[index];
      if (inString) {
        if (escaped) escaped = false;
        else if (character === "\\") escaped = true;
        else if (character === '"') inString = false;
      } else if (character === '"') inString = true;
      else if (character === "{") depth++;
      else if (character === "}" && --depth === 0) {
        index++;
        result.push(text.slice(start, index));
        break;
      }
    }
    if (depth !== 0) return [];
  }
  return result;
}

function capture(body) {
  const bodyText = body.toString("utf8");
  let parsed;
  try {
    parsed = JSON.parse(bodyText);
  } catch {
    return;
  }
  if (!parsed || parsed.platform_entity !== "INVOICES" || !Array.isArray(parsed.data)) return;
  const rawRecords = rawDataObjects(bodyText);
  if (rawRecords.length !== parsed.data.length) return;

  summary.reports++;
  summary.reportRows += parsed.data.length;
  summary.pageSizes.push(parsed.data.length);
  if (typeof parsed.next_cursor === "string" && parsed.next_cursor.length > 0) {
    summary.pagesWithNextCursor++;
  }
  for (let index = 0; index < parsed.data.length; index++) {
    const record = parsed.data[index];
    const id = record && (record.id ?? record.ID);
    if (typeof id !== "string" || id.length === 0) {
      summary.missingIds++;
      continue;
    }
    if (seen.has(id)) summary.duplicateIds++;
    seen.add(id);
    const digest = hash(rawRecords[index]);
    if (fingerprints.has(id) && fingerprints.get(id) !== digest) summary.duplicatePayloadMismatches++;
    else fingerprints.set(id, digest);
  }
}

function writeProgress() {
  if (!progressPath) return;
  fs.mkdirSync(path.dirname(progressPath), { recursive: true });
  fs.writeFileSync(progressPath, JSON.stringify({
    reports: summary.reports,
    reportRows: summary.reportRows,
    uniqueIds: seen.size,
    duplicateIds: summary.duplicateIds,
    responseStatuses: summary.responseStatuses,
    firstCommitComplete: summary.firstCommitComplete,
    firstCommitUtc: summary.firstCommitUtc || null,
    responsePaused: summary.responsePaused || false,
  }, null, 2));
}

function finish(status) {
  const ordered = Array.from(fingerprints, ([id, digest]) => ({ id, digest }))
    .sort((a, b) => (a.id < b.id ? -1 : a.id > b.id ? 1 : 0));
  const aggregate = crypto.createHash("sha256");
  for (const item of ordered) aggregate.update(`${item.id}:${item.digest}\n`, "utf8");
  summary.status = status;
  summary.uniqueIds = seen.size;
  summary.actualFingerprint = aggregate.digest("hex");
  summary.fingerprintMatches = summary.actualFingerprint === expectedFingerprint;
  fs.mkdirSync(path.dirname(resultPath), { recursive: true });
  fs.writeFileSync(resultPath, JSON.stringify(summary, null, 2));
  process.stdout.write(
    `Invoice ingest capture ${status}: reports=${summary.reports} rows=${summary.reportRows} unique=${summary.uniqueIds} fingerprintMatches=${summary.fingerprintMatches}\n`,
  );
}

const server = http.createServer((incoming, outgoing) => {
  const chunks = [];
  let length = 0;
  incoming.on("data", (chunk) => {
    length += chunk.length;
    if (length > maxBodyBytes) {
      incoming.destroy();
      outgoing.writeHead(413).end();
      return;
    }
    chunks.push(chunk);
  });
  incoming.on("end", () => {
    const body = Buffer.concat(chunks);
    if (incoming.method === "POST" && incoming.url === "/versioned/ingest") capture(body);
    const isInvoiceReport = incoming.method === "POST" && incoming.url === "/versioned/ingest"
      && (() => { try { const parsed = JSON.parse(body.toString("utf8")); return parsed.platform_entity === "INVOICES" && Array.isArray(parsed.data); } catch { return false; } })();

    const headers = { ...incoming.headers, host: `127.0.0.1:${backendPort}` };
    delete headers.connection;
    delete headers["transfer-encoding"];
    headers["content-length"] = body.length;
    const request = http.request(
      {
        host: "127.0.0.1",
        port: backendPort,
        method: incoming.method,
        path: incoming.url,
        headers,
        timeout: 15 * 60 * 1000,
      },
      (response) => {
        const code = String(response.statusCode || 0);
        summary.responseStatuses[code] = (summary.responseStatuses[code] || 0) + 1;
        const responseChunks = [];
        let responseBytes = 0;
        response.on("data", (chunk) => {
          responseBytes += chunk.length;
          if (responseBytes <= maxBodyBytes) responseChunks.push(chunk);
          else response.destroy(new Error("backend response exceeded diagnostic limit"));
        });
        response.on("end", async () => {
          writeProgress();
          const shouldPause = isInvoiceReport && pauseFirstCommitMs > 0 && !summary.firstCommitComplete
            && response.statusCode >= 200 && response.statusCode < 300;
          if (shouldPause) {
            summary.firstCommitComplete = true;
            summary.firstCommitUtc = new Date().toISOString();
            summary.responsePaused = true;
            writeProgress();
            await new Promise((resolve) => {
              const timer = setTimeout(resolve, pauseFirstCommitMs);
              outgoing.once("close", () => { clearTimeout(timer); resolve(); });
            });
            summary.responsePaused = false;
            writeProgress();
          }
          if (outgoing.destroyed) return;
          outgoing.writeHead(response.statusCode || 502, response.headers);
          outgoing.end(Buffer.concat(responseChunks));
        });
      },
    );
    request.on("timeout", () => request.destroy(new Error("backend timeout")));
    request.on("error", () => {
      if (!outgoing.headersSent) outgoing.writeHead(502, { "content-type": "application/json" });
      outgoing.end('{"error":"local backend unavailable"}');
    });
    request.end(body);
  });
});

server.requestTimeout = 15 * 60 * 1000;
server.headersTimeout = 15 * 60 * 1000 + 1000;
server.listen(listenPort, "127.0.0.1", () => {
  process.stdout.write(`Invoice ingest capture proxy listening on 127.0.0.1:${listenPort}; backend=127.0.0.1:${backendPort}\n`);
});

let finishing = false;
function stop() {
  if (finishing) return;
  finishing = true;
  server.close(() => {
    finish(summary.reports > 0 ? "completed" : "no-invoice-reports");
    process.exit(summary.reports > 0 && summary.fingerprintMatches ? 0 : 1);
  });
}
process.on("SIGINT", stop);
process.on("SIGTERM", stop);
