/**
 * OLI Telegram relay — a tiny Cloudflare Worker that proxies Telegram Bot API
 * traffic. Deploy it once, put the URL into OLI → Telegram → "Cloudflare-релей".
 *
 * Why it works from Russia: api.telegram.org is filtered, but a *.workers.dev
 * (or your own) domain on Cloudflare's edge is not. The Worker performs the
 * request to Telegram from Cloudflare's network and streams the response back.
 *
 * Deploy (no CLI needed):
 *   1. https://dash.cloudflare.com → Workers & Pages → Create → Worker
 *   2. Paste this file, Deploy.
 *   3. Settings → Variables → add RELAY_SECRET (any long random string),
 *      and paste the same value into OLI → Telegram → секрет релея
 *      (or leave RELAY_SECRET unset for an open relay, not recommended).
 *
 * Optional: bind a custom domain (e.g. oli-bot.yourdomain.ru) in the Worker
 * settings — custom domains are more resistant to IP-based blocking than
 * *.workers.dev.
 */

const TELEGRAM = 'https://api.telegram.org';

export default {
  async fetch(request, env) {
    const url = new URL(request.url);

    // Health probe for OLI's "Проверить" button.
    if (url.pathname === '/' || url.pathname === '/health') {
      return json({ ok: true, service: 'oli-telegram-relay', version: '1.0' });
    }

    // Expected path: /bot<token>/<method>
    const match = url.pathname.match(/^\/bot([^/]+)\/([A-Za-z]+)$/);
    if (!match) return json({ ok: false, description: 'Use /bot<token>/<method>' }, 404);

    if (env.RELAY_SECRET && request.headers.get('X-OLI-Relay-Secret') !== env.RELAY_SECRET) {
      return json({ ok: false, description: 'relay secret mismatch' }, 403);
    }

    if (request.method !== 'POST') return json({ ok: false, description: 'POST required' }, 405);

    const target = `${TELEGRAM}/bot${match[1]}/${match[2]}${url.search}`;
    const headers = new Headers();
    const contentType = request.headers.get('content-type') || 'application/json';
    headers.set('content-type', contentType);

    let body;
    if (contentType.includes('multipart/form-data')) {
      // Stream large uploads straight through instead of buffering them.
      body = request.body;
    } else {
      body = await request.text();
    }

    const upstream = await fetch(target, { method: 'POST', headers, body });
    return new Response(upstream.body, {
      status: upstream.status,
      headers: {
        'content-type': upstream.headers.get('content-type') || 'application/json',
        'cache-control': 'no-store',
      },
    });
  },
};

function json(value, status = 200) {
  return new Response(JSON.stringify(value), {
    status,
    headers: { 'content-type': 'application/json' },
  });
}
