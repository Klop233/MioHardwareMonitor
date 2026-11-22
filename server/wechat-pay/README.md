# 微信支付 Native v3 收单服务端

用于配合桌面端「赞助支持」页面发起微信支付 Native v3 扫码收单与订单状态轮询。商户私钥只保存在服务端环境变量中，不进入开源客户端。

## 需要准备的微信支付商户配置

1. **开通 Native 支付**：登录微信支付商户平台 → `产品中心` → `我的产品`，确认已开通「Native 支付」。
2. **绑定 AppID**：`产品中心` → `AppID账号管理`，关联你的公众号、小程序或微信开放平台应用，记下 `AppID`。
3. **申请商户 API 证书**：`账户中心` → `API 安全` → `申请 API 证书`，下载得到 `apiclient_key.pem` 并记下 **证书序列号**（`WECHAT_SERIAL_NO`）。
4. **设置 APIv3 密钥**：`账户中心` → `API 安全` → `设置 APIv3 密钥`（32 位随机字符，`WECHAT_APIV3_KEY`）。
5. **配置微信支付公钥（可选，用于回调验签）**：`账户中心` → `API 安全` → `微信支付公钥`，下载 `pub_key.pem` 并记下公钥 ID（`WECHAT_PUBLIC_KEY_ID`）。
6. **公网 HTTPS 回调地址**：Native 支付 v3 下单本身通过请求体 `notify_url` 传递（例如 `https://donate.miolab.ink/api/donate/wechat/notify`），商户平台「开发配置」里的 Native 回调链接为 v2 遗留项，可留空或同填此地址。

## 部署方式 A：Cloudflare Workers（推荐，免运维自带 HTTPS）

```bash
cd server/wechat-pay
npx wrangler secret put WECHAT_MCHID
npx wrangler secret put WECHAT_APPID
npx wrangler secret put WECHAT_SERIAL_NO
npx wrangler secret put WECHAT_APIV3_KEY
npx wrangler secret put WECHAT_PRIVATE_KEY_PEM < apiclient_key.pem
# 可选：回调验签公钥
npx wrangler secret put WECHAT_PUBLIC_KEY_ID
npx wrangler secret put WECHAT_PUBLIC_KEY_PEM < pub_key.pem

npx wrangler deploy
```

在 `wrangler.toml` 中绑定 `donate.miolab.ink`（或使用默认 `*.workers.dev` 域名）即可。

## 部署方式 B：自有服务器（Node.js 20+，零依赖）

```bash
cd server/wechat-pay
cp .env.example .env
# 编辑 .env 填入商户信息，把 apiclient_key.pem 放到同目录
node node-server.mjs
```

配合 Nginx / Caddy 反代 HTTPS 到 `127.0.0.1:8787` 即可。

## 接口列表

- `GET  /api/donate/health` — 健康检查与配置状态
- `POST /api/donate/wechat/native` — 下单，入参 `{ "amountFen": 1800, "note": "可选留言" }`
- `GET  /api/donate/wechat/status?orderId=MIO...` — 查询订单支付状态
- `POST /api/donate/wechat/close` — 关闭未支付订单
- `POST /api/donate/wechat/notify` — 接收微信支付异步回调（RSA 验签 + AES-256-GCM 解密）
