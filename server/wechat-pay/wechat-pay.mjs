// WeChat Pay APIv3 Native Acquiring Core (Zero-Dependency Web Crypto ESM)
// Runs identically on Cloudflare Workers and Node.js 20+ (globalThis.crypto.subtle + fetch).

const WECHAT_API_ORIGIN = 'https://api.mch.weixin.qq.com';
const ORDER_ID_REGEX = /^MIO[A-Za-z0-9]{10,28}$/;

function jsonResponse(data, status = 200) {
  return new Response(JSON.stringify(data), {
    status,
    headers: {
      'content-type': 'application/json; charset=utf-8',
      'cache-control': 'no-store',
      'access-control-allow-origin': '*',
      'access-control-allow-methods': 'GET, POST, OPTIONS',
      'access-control-allow-headers': 'Content-Type'
    }
  });
}

function bytesToBase64(bytes) {
  let binary = '';
  const arr = bytes instanceof Uint8Array ? bytes : new Uint8Array(bytes);
  for (let i = 0; i < arr.length; i++) binary += String.fromCharCode(arr[i]);
  return btoa(binary);
}

function base64ToBytes(b64) {
  const clean = String(b64 || '').replace(/\s+/g, '');
  const bin = atob(clean);
  const out = new Uint8Array(bin.length);
  for (let i = 0; i < bin.length; i++) out[i] = bin.charCodeAt(i);
  return out;
}

function pemToDer(pem) {
  const normalized = String(pem || '')
    .replace(/\\n/g, '\n')
    .replace(/-----BEGIN [^-]+-----/g, '')
    .replace(/-----END [^-]+-----/g, '')
    .replace(/\s+/g, '');
  return base64ToBytes(normalized);
}

function randomHex(byteLen = 16) {
  const buf = new Uint8Array(byteLen);
  crypto.getRandomValues(buf);
  return Array.from(buf, (b) => b.toString(16).padStart(2, '0')).join('').toUpperCase();
}

function formatChinaTimestamp(date) {
  // Format RFC3339 in UTC+8 (required by WeChat Pay time_expire)
  const bj = new Date(date.getTime() + 8 * 3600 * 1000);
  const pad = (n) => String(n).padStart(2, '0');
  return `${bj.getUTCFullYear()}-${pad(bj.getUTCMonth() + 1)}-${pad(bj.getUTCDate())}T${pad(bj.getUTCHours())}:${pad(bj.getUTCMinutes())}:${pad(bj.getUTCSeconds())}+08:00`;
}

function generateOutTradeNo() {
  const bj = new Date(Date.now() + 8 * 3600 * 1000);
  const pad = (n) => String(n).padStart(2, '0');
  const stamp = `${bj.getUTCFullYear()}${pad(bj.getUTCMonth() + 1)}${pad(bj.getUTCDate())}${pad(bj.getUTCHours())}${pad(bj.getUTCMinutes())}${pad(bj.getUTCSeconds())}`;
  return `MIO${stamp}${randomHex(5)}`; // 3 + 14 + 10 = 27 chars (<= 32)
}

async function importMerchantPrivateKey(pem) {
  const der = pemToDer(pem);
  return crypto.subtle.importKey(
    'pkcs8',
    der,
    { name: 'RSASSA-PKCS1-v1_5', hash: 'SHA-256' },
    false,
    ['sign']
  );
}

async function importPlatformPublicKey(pem) {
  const der = pemToDer(pem);
  return crypto.subtle.importKey(
    'spki',
    der,
    { name: 'RSASSA-PKCS1-v1_5', hash: 'SHA-256' },
    false,
    ['verify']
  );
}

async function buildWeChatAuthHeader(cfg, method, canonicalPath, bodyStr) {
  const timestamp = String(Math.floor(Date.now() / 1000));
  const nonceStr = randomHex(16);
  const message = `${method.toUpperCase()}\n${canonicalPath}\n${timestamp}\n${nonceStr}\n${bodyStr}\n`;

  const key = await importMerchantPrivateKey(cfg.privateKeyPem);
  const sigBuf = await crypto.subtle.sign(
    'RSASSA-PKCS1-v1_5',
    key,
    new TextEncoder().encode(message)
  );
  const signature = bytesToBase64(new Uint8Array(sigBuf));

  return `WECHATPAY2-SHA256-RSA2048 mchid="${cfg.mchid}",nonce_str="${nonceStr}",signature="${signature}",timestamp="${timestamp}",serial_no="${cfg.serialNo}"`;
}

async function callWeChatApi(cfg, method, canonicalPath, payloadObj = null) {
  const bodyStr = payloadObj ? JSON.stringify(payloadObj) : '';
  const auth = await buildWeChatAuthHeader(cfg, method, canonicalPath, bodyStr);

  const headers = {
    'Authorization': auth,
    'Accept': 'application/json',
    'User-Agent': 'MioHardwareMonitor-WeChatPay/1.0'
  };
  if (payloadObj) {
    headers['Content-Type'] = 'application/json; charset=utf-8';
  }
  // If using WeChat Pay Public Key mode (recommended for new merchants), include serial header when configured
  if (cfg.wechatPayPublicKeyId) {
    headers['Wechatpay-Serial'] = cfg.wechatPayPublicKeyId;
  }

  const resp = await fetch(`${WECHAT_API_ORIGIN}${canonicalPath}`, {
    method,
    headers,
    body: payloadObj ? bodyStr : undefined
  });

  const text = await resp.text();
  let parsed = null;
  try { parsed = text ? JSON.parse(text) : {}; } catch { parsed = { raw: text }; }
  return { ok: resp.ok, status: resp.status, data: parsed };
}

async function verifyNotifySignature(cfg, headers, rawBody) {
  if (!cfg.wechatPayPublicKeyPem) return false;
  const ts = headers.get('wechatpay-timestamp') || '';
  const nonce = headers.get('wechatpay-nonce') || '';
  const sig = headers.get('wechatpay-signature') || '';
  if (!ts || !nonce || !sig) return false;

  // Reject replay timestamps older than 5 minutes
  const tsNum = Number(ts);
  if (!Number.isFinite(tsNum) || Math.abs(Date.now() / 1000 - tsNum) > 300) {
    return false;
  }

  const pubKey = await importPlatformPublicKey(cfg.wechatPayPublicKeyPem);
  const msg = `${ts}\n${nonce}\n${rawBody}\n`;
  return crypto.subtle.verify(
    'RSASSA-PKCS1-v1_5',
    pubKey,
    base64ToBytes(sig),
    new TextEncoder().encode(msg)
  );
}

async function decryptNotifyResource(apiV3Key, resource) {
  const keyBytes = new TextEncoder().encode(apiV3Key);
  const aesKey = await crypto.subtle.importKey('raw', keyBytes, { name: 'AES-GCM' }, false, ['decrypt']);
  const iv = new TextEncoder().encode(resource.nonce);
  const additionalData = resource.associated_data
    ? new TextEncoder().encode(resource.associated_data)
    : undefined;
  const ciphertext = base64ToBytes(resource.ciphertext);

  const plainBuf = await crypto.subtle.decrypt(
    additionalData ? { name: 'AES-GCM', iv, additionalData } : { name: 'AES-GCM', iv },
    aesKey,
    ciphertext
  );
  return JSON.parse(new TextDecoder().decode(plainBuf));
}

function readConfig(env, requestUrl) {
  const origin = new URL(requestUrl).origin;
  const mchid = String(env.WECHAT_MCHID || '').trim();
  const appid = String(env.WECHAT_APPID || '').trim();
  const serialNo = String(env.WECHAT_SERIAL_NO || '').trim();
  const privateKeyPem = String(env.WECHAT_PRIVATE_KEY_PEM || '').trim();
  const apiV3Key = String(env.WECHAT_APIV3_KEY || '').trim();
  const notifyUrl = String(env.WECHAT_NOTIFY_URL || `${origin}/api/donate/wechat/notify`).trim();
  const merchantName = String(env.MERCHANT_NAME || '星澪科技').trim();
  const wechatPayPublicKeyPem = String(env.WECHAT_PUBLIC_KEY_PEM || '').trim();
  const wechatPayPublicKeyId = String(env.WECHAT_PUBLIC_KEY_ID || '').trim();

  const missing = [];
  if (!mchid) missing.push('WECHAT_MCHID');
  if (!appid) missing.push('WECHAT_APPID');
  if (!serialNo) missing.push('WECHAT_SERIAL_NO');
  if (!privateKeyPem) missing.push('WECHAT_PRIVATE_KEY_PEM');
  if (!apiV3Key || apiV3Key.length !== 32) missing.push('WECHAT_APIV3_KEY(32位)');

  return {
    mchid,
    appid,
    serialNo,
    privateKeyPem,
    apiV3Key,
    notifyUrl,
    merchantName,
    wechatPayPublicKeyPem,
    wechatPayPublicKeyId,
    configured: missing.length === 0,
    missing
  };
}

export async function handleDonateRequest(request, env = {}) {
  const url = new URL(request.url);
  const path = url.pathname.replace(/\/+$/, '') || '/';

  if (request.method === 'OPTIONS') {
    return jsonResponse({ ok: true }, 200);
  }

  const cfg = readConfig(env, request.url);

  // 1. Health & configuration check
  if (request.method === 'GET' && (path === '/api/donate/health' || path === '/health' || path === '/')) {
    return jsonResponse({
      ok: true,
      service: 'MioHardwareMonitor WeChat Pay Native v3 Acquiring',
      merchantName: cfg.merchantName,
      configured: cfg.configured,
      missing: cfg.configured ? [] : cfg.missing
    });
  }

  // 2. Create Native QR Order: POST /api/donate/wechat/native
  if (request.method === 'POST' && path === '/api/donate/wechat/native') {
    if (!cfg.configured) {
      return jsonResponse({
        ok: false,
        message: `收单服务端尚未完成配置，缺少环境变量：${cfg.missing.join(', ')}`
      }, 503);
    }

    let body;
    try {
      body = await request.json();
    } catch {
      return jsonResponse({ ok: false, message: '请求体必须为有效 JSON。' }, 400);
    }

    const amountFen = Math.round(Number(body?.amountFen));
    if (!Number.isInteger(amountFen) || amountFen < 100 || amountFen > 1000000) {
      return jsonResponse({ ok: false, message: '赞助金额需在 ¥1.00 ~ ¥10,000.00 之间。' }, 400);
    }

    const note = String(body?.note || '').trim().slice(0, 40);
    const outTradeNo = generateOutTradeNo();
    const expireDate = new Date(Date.now() + 15 * 60 * 1000);
    const expireIso = formatChinaTimestamp(expireDate);
    const desc = note
      ? `星澪硬件监控赞助（${note}）`.slice(0, 120)
      : '星澪硬件监控开源项目赞助';

    const wxPayload = {
      appid: cfg.appid,
      mchid: cfg.mchid,
      description: desc,
      out_trade_no: outTradeNo,
      time_expire: expireIso,
      notify_url: cfg.notifyUrl,
      amount: {
        total: amountFen,
        currency: 'CNY'
      }
    };

    const wxRes = await callWeChatApi(cfg, 'POST', '/v3/pay/transactions/native', wxPayload);
    if (!wxRes.ok || !wxRes.data?.code_url) {
      return jsonResponse({
        ok: false,
        code: wxRes.data?.code || `HTTP_${wxRes.status}`,
        message: wxRes.data?.message || '微信支付下单失败，请检查商户参数与 Native 支付权限。'
      }, 502);
    }

    return jsonResponse({
      ok: true,
      orderId: outTradeNo,
      amountFen,
      codeUrl: wxRes.data.code_url,
      expireAt: expireDate.toISOString(),
      merchantName: cfg.merchantName
    });
  }

  // 3. Query Order Status: GET /api/donate/wechat/status?orderId=MIO...
  if (request.method === 'GET' && path === '/api/donate/wechat/status') {
    if (!cfg.configured) {
      return jsonResponse({ ok: false, message: '收单服务端尚未配置完成。' }, 503);
    }

    const orderId = String(url.searchParams.get('orderId') || '').trim();
    if (!ORDER_ID_REGEX.test(orderId)) {
      return jsonResponse({ ok: false, message: '无效的订单号格式。' }, 400);
    }

    const queryPath = `/v3/pay/transactions/out-trade-no/${encodeURIComponent(orderId)}?mchid=${encodeURIComponent(cfg.mchid)}`;
    const wxRes = await callWeChatApi(cfg, 'GET', queryPath);
    if (!wxRes.ok) {
      return jsonResponse({
        ok: false,
        code: wxRes.data?.code || `HTTP_${wxRes.status}`,
        message: wxRes.data?.message || '查询订单状态失败。'
      }, 502);
    }

    return jsonResponse({
      ok: true,
      orderId,
      tradeState: wxRes.data.trade_state || 'NOTPAY',
      tradeStateDesc: wxRes.data.trade_state_desc || '',
      transactionId: wxRes.data.transaction_id || '',
      successTime: wxRes.data.success_time || '',
      amountFen: wxRes.data.amount?.total || 0
    });
  }

  // 4. Close Unpaid Order: POST /api/donate/wechat/close
  if (request.method === 'POST' && path === '/api/donate/wechat/close') {
    if (!cfg.configured) {
      return jsonResponse({ ok: false, message: '收单服务端尚未配置完成。' }, 503);
    }

    let body;
    try { body = await request.json(); } catch { body = {}; }
    const orderId = String(body?.orderId || '').trim();
    if (!ORDER_ID_REGEX.test(orderId)) {
      return jsonResponse({ ok: false, message: '无效的订单号。' }, 400);
    }

    const closePath = `/v3/pay/transactions/out-trade-no/${encodeURIComponent(orderId)}/close`;
    const wxRes = await callWeChatApi(cfg, 'POST', closePath, { mchid: cfg.mchid });
    return jsonResponse({ ok: wxRes.ok || wxRes.status === 204, orderId });
  }

  // 5. WeChat Pay Async Notify Webhook: POST /api/donate/wechat/notify
  if (request.method === 'POST' && path === '/api/donate/wechat/notify') {
    if (!cfg.configured) {
      return jsonResponse({ code: 'FAIL', message: 'Not configured' }, 503);
    }

    const rawBody = await request.text();
    if (cfg.wechatPayPublicKeyPem) {
      const valid = await verifyNotifySignature(cfg, request.headers, rawBody);
      if (!valid) {
        return jsonResponse({ code: 'FAIL', message: 'Invalid signature' }, 401);
      }
    }

    try {
      const event = JSON.parse(rawBody);
      if (event?.resource?.ciphertext) {
        const decrypted = await decryptNotifyResource(cfg.apiV3Key, event.resource);
        console.log('[WeChatPay Notify]', decrypted.out_trade_no, decrypted.trade_state, decrypted.amount?.total);
      }
      return jsonResponse({ code: 'SUCCESS', message: '成功' }, 200);
    } catch (err) {
      return jsonResponse({ code: 'FAIL', message: String(err?.message || err) }, 400);
    }
  }

  return jsonResponse({ ok: false, message: 'Not Found' }, 404);
}
