import http from 'node:http';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { handleDonateRequest } from './wechat-pay.mjs';

const __dirname = path.dirname(fileURLToPath(import.meta.url));

// Minimal .env loader (zero external dependencies)
const envPath = path.join(__dirname, '.env');
if (fs.existsSync(envPath)) {
  for (const rawLine of fs.readFileSync(envPath, 'utf8').split(/\r?\n/)) {
    const line = rawLine.trim();
    if (!line || line.startsWith('#')) continue;
    const eq = line.indexOf('=');
    if (eq <= 0) continue;
    const key = line.slice(0, eq).trim();
    let val = line.slice(eq + 1).trim();
    if ((val.startsWith('"') && val.endsWith('"')) || (val.startsWith("'") && val.endsWith("'"))) {
      val = val.slice(1, -1);
    }
    if (!process.env[key]) process.env[key] = val;
  }
}

// Allow referencing a PEM file path via WECHAT_PRIVATE_KEY_FILE
if (!process.env.WECHAT_PRIVATE_KEY_PEM && process.env.WECHAT_PRIVATE_KEY_FILE && fs.existsSync(process.env.WECHAT_PRIVATE_KEY_FILE)) {
  process.env.WECHAT_PRIVATE_KEY_PEM = fs.readFileSync(process.env.WECHAT_PRIVATE_KEY_FILE, 'utf8');
}
if (!process.env.WECHAT_PUBLIC_KEY_PEM && process.env.WECHAT_PUBLIC_KEY_FILE && fs.existsSync(process.env.WECHAT_PUBLIC_KEY_FILE)) {
  process.env.WECHAT_PUBLIC_KEY_PEM = fs.readFileSync(process.env.WECHAT_PUBLIC_KEY_FILE, 'utf8');
}

const PORT = Number(process.env.PORT || 8787);
const HOST = process.env.HOST || '0.0.0.0';

const server = http.createServer(async (req, res) => {
  try {
    const chunks = [];
    for await (const chunk of req) chunks.push(chunk);
    const bodyBuf = Buffer.concat(chunks);

    const proto = req.headers['x-forwarded-proto'] || 'http';
    const host = req.headers.host || `127.0.0.1:${PORT}`;
    const fullUrl = `${proto}://${host}${req.url || '/'}`;

    const webReq = new Request(fullUrl, {
      method: req.method,
      headers: req.headers,
      body: req.method === 'GET' || req.method === 'HEAD' ? undefined : bodyBuf
    });

    const webResp = await handleDonateRequest(webReq, process.env);
    res.statusCode = webResp.status;
    webResp.headers.forEach((v, k) => res.setHeader(k, v));
    const respBuf = Buffer.from(await webResp.arrayBuffer());
    res.end(respBuf);
  } catch (err) {
    res.statusCode = 500;
    res.setHeader('content-type', 'application/json; charset=utf-8');
    res.end(JSON.stringify({ ok: false, message: String(err?.message || 'Internal Server Error') }));
  }
});

server.listen(PORT, HOST, () => {
  console.log(`[MioHardwareMonitor Pay] Listening on http://${HOST}:${PORT}`);
});
