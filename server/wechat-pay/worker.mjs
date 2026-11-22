import { handleDonateRequest } from './wechat-pay.mjs';

export default {
  async fetch(request, env) {
    try {
      return await handleDonateRequest(request, env);
    } catch (err) {
      return new Response(
        JSON.stringify({ ok: false, message: String(err?.message || 'Internal Server Error') }),
        { status: 500, headers: { 'content-type': 'application/json; charset=utf-8' } }
      );
    }
  }
};
