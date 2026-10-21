// A stand-in for a deployed environment, used ONLY to test deploy/scripts/verify-public.sh (#195).
//
// It speaks plain HTTP and HTTPS on ports the operating system picks, behaves like the real thing when run with no
// faults, and breaks exactly one thing per fault name, so a test can show each check of verify-public.sh going red
// against the matching fault and green against none. It is not a model of the application: it answers only what
// verify-public.sh asks, in the shape the real stack answers it.
//
//   node mock-shop.mjs --env staging|production --host shop.test --cert c.pem --key k.pem --ready ready.json
//                      [--faults a,b,c] [--www]
//   node mock-shop.mjs --upstream --host shop.test --origin https://shop.test --ready ready.json [--bind 0.0.0.0]
//
// Writes {"https":<port>,"http":<port>,"pid":<pid>} to --ready once both are listening, then serves until killed.
// Binds to 127.0.0.1 only (unless --bind says otherwise, for --upstream).
//
// --upstream plays the application stack BEHIND the host nginx instead of the whole public site: plain HTTP only, the
// gateway's own headers, the storefront's robots.txt (which allows everything, on staging too) and no HSTS, no
// X-Robots-Tag, no TLS. Any Host it does not know is answered 400, like the storefront. It is what a real nginx
// (docker, see docs/16 tests) is pointed at, so the host-nginx templates are tested as written.
import fs from 'node:fs';
import http from 'node:http';
import https from 'node:https';
import tls from 'node:tls';

const args = Object.fromEntries(
  process.argv.slice(2).reduce((acc, a, i, all) => {
    if (a.startsWith('--')) acc.push([a.slice(2), all[i + 1] && !all[i + 1].startsWith('--') ? all[i + 1] : 'true']);
    return acc;
  }, []),
);
const env = args.env;
const host = args.host;
const faults = new Set((args.faults ?? '').split(',').filter(Boolean));
const fault = (name) => faults.has(name);
const upstream = args.upstream === 'true';
// A value-less option comes out as the string 'true': for the options that need a value that means it was left out.
for (const name of ['host', 'cert', 'key', 'ready', 'origin', 'env']) {
  if (args[name] === 'true') {
    console.error(`--${name} needs a value`);
    process.exit(2);
  }
}
if (upstream ? !host || !args.origin || !args.ready : !['staging', 'production'].includes(env) || !host || !args.cert || !args.key || !args.ready) {
  console.error(
    'usage: mock-shop.mjs --env staging|production --host H --cert C --key K --ready FILE [--faults a,b] [--www]\n' +
      '   or: mock-shop.mjs --upstream --host H --origin https://H --ready FILE',
  );
  process.exit(2);
}

let httpsPort = 0;
const origin = () => (upstream ? args.origin : `https://${host}:${httpsPort}`);
const known = (h) => {
  const bare = (h ?? '').split(':')[0].toLowerCase();
  return bare === host || (args.www === 'true' && bare === `www.${host}`);
};

const ssrPage = fault('no-ssr')
  ? '<!doctype html><html><head><title>shop</title></head><body><app-root></app-root></body></html>'
  : fault('ssr-twice')
    ? '<!doctype html><html><body><app-root ng-version="21.0.0" ng-server-context="ssr"><div ng-server-context="ssr"></div></app-root></body></html>'
    : '<!doctype html><html><head><title>shop</title></head><body><app-root ng-version="21.0.0" ng-server-context="ssr"></app-root></body></html>';

function shopHeaders(extra = {}) {
  const h = { 'X-Content-Type-Options': 'nosniff', 'Referrer-Policy': 'strict-origin-when-cross-origin', ...extra };
  if (upstream) return h;   // HSTS, X-Robots-Tag and the Server header belong to the host nginx in front of this
  if (!fault('no-hsts')) h['Strict-Transport-Security'] = fault('hsts-short') ? 'max-age=300' : 'max-age=31536000';
  if (env === 'staging' && !fault('no-noindex')) h['X-Robots-Tag'] = 'noindex, nofollow';
  if (env === 'production' && fault('prod-noindex')) h['X-Robots-Tag'] = 'noindex, nofollow';
  h['Server'] = fault('server-version') ? 'nginx/1.24.0 (Ubuntu)' : 'nginx';
  if (fault('powered-by')) h['X-Powered-By'] = 'Express';
  return h;
}

const trace = 'TypeError: Cannot read properties of undefined\n    at Object.handler (/app/dist/server/server.mjs:120:15)\n    at node_modules/express/lib/router/layer.js:95:5';

function robots() {
  if (!upstream && env === 'staging' && !fault('robots-staging-allows')) return 'User-agent: *\nDisallow: /\n';
  if (env === 'production' && fault('robots-prod-disallow')) return 'User-agent: *\nDisallow: /\n';
  const scheme = fault('robots-prod-wrong-sitemap') ? 'http://' : 'https://';
  const sitemapOrigin = fault('robots-prod-wrong-sitemap') ? `${scheme}${host}:${httpsPort}` : origin();
  return `User-agent: *\nAllow: /\nDisallow: /account\nDisallow: /checkout\nDisallow: /api/\n\nSitemap: ${sitemapOrigin}/sitemap.xml\n`;
}

function sitemap() {
  const base = fault('sitemap-wrong-host') ? 'https://colombowardrobecompany.example' : origin();
  return `<?xml version="1.0" encoding="UTF-8"?><urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9"><url><loc>${base}/</loc></url><url><loc>${base}/women</loc></url></urlset>`;
}

function cookieHeader() {
  const attrs = ['Max-Age=1800', 'Path=/', 'SameSite=Lax'];
  if (!fault('cookie-insecure')) attrs.push('Secure');
  if (!fault('cookie-nohttponly')) attrs.push('HttpOnly');
  return `sd_vs=7f3c3b0e-5d2a-4a3c-9d5b-0a1b2c3d4e5f; ${attrs.join('; ')}`;
}

function handle(req, res, secure) {
  const send = (status, body, headers = {}, type = 'text/html; charset=utf-8') => {
    res.writeHead(status, { 'Content-Type': type, ...(secure ? shopHeaders(headers) : headers) });
    res.end(body);
  };
  const reqHost = req.headers.host;
  if (upstream && !known(reqHost)) return send(400, 'Bad Request', {}, 'text/plain; charset=utf-8');
  const url = new URL(req.url, 'http://x');
  const path = url.pathname;

  if (!secure) {
    if (!known(reqHost)) {
      if (fault('unknown-host-200')) return send(200, ssrPage, shopHeaders());
      req.socket.destroy();
      return;
    }
    const status = fault('http-302') ? 302 : 301;
    const location = fault('http-wrong-location') ? `${origin()}/` : `${origin()}${req.url}`;
    res.writeHead(status, { Location: location, 'Content-Length': '0' });
    res.end();
    return;
  }

  if (!known(reqHost)) {
    // Only reachable with the unknown-host-200 fault (otherwise the TLS handshake already refused the name).
    return send(200, ssrPage);
  }
  if (args.www === 'true' && bareHost(reqHost) === `www.${host}`) {
    if (fault('www-no-redirect')) return send(200, ssrPage);
    res.writeHead(301, { Location: `${origin()}${req.url}`, 'Content-Length': '0', ...shopHeaders() });
    res.end();
    return;
  }

  if (req.method === 'POST' && path === '/api/traffic/page-views') {
    const status = fault('pageview-200') ? 200 : 204;
    res.writeHead(status, { 'Cache-Control': 'no-store', 'Set-Cookie': cookieHeader(), ...shopHeaders() });
    res.end();
    return;
  }
  if (req.method !== 'GET' && req.method !== 'HEAD') return send(405, 'method not allowed');

  switch (true) {
    case path === '/':
      return send(200, ssrPage);
    case path === '/robots.txt':
      return send(200, robots(), {}, 'text/plain; charset=utf-8');
    case path === '/sitemap.xml':
      if (env === 'production' && fault('sitemap-404')) return send(404, 'not found');
      return send(200, sitemap(), {}, 'application/xml; charset=utf-8');
    case path === '/admin/':
      if (fault('admin-500')) return send(500, 'boom');
      return send(200, '<!doctype html><html><head><meta name="robots" content="noindex, nofollow"></head><body><app-root></app-root></body></html>', { 'X-Frame-Options': 'DENY' });
    case path === '/api/catalog/departments':
      if (fault('departments-500')) return send(500, '{"title":"error"}', {}, 'application/json');
      if (fault('departments-html')) return send(200, ssrPage);
      return send(200, '[{"id":1,"name":"Women","slug":"women"}]', {}, 'application/json; charset=utf-8');
    case path.startsWith('/order/'):
      if (fault('order-500')) return send(500, 'Internal Server Error');
      return send(404, '<!doctype html><html><body><app-root ng-version="21.0.0" ng-server-context="ssr">Order not found</app-root></body></html>');
    case path === '/account/reset-password':
      if (fault('reset-500')) return send(500, 'Internal Server Error');
      return send(200, ssrPage);
    case path === '/internal/catalog/sitemap':
      if (fault('internal-open')) return send(200, '{"departments":[]}', {}, 'application/json');
      return send(404, '<!doctype html><html><body>Not found</body></html>');
    case path === '/health/ready':
      if (fault('ready-open')) return send(200, '{"status":"Healthy"}', {}, 'application/json');
      return send(404, 'not found', {}, 'text/plain');
    case path === '/health':
      return send(200, '{"status":"ok"}', {}, 'application/json');
    case path.startsWith('/api/'):
      return send(404, '{"title":"Not Found","status":404}', {}, 'application/problem+json');
    default:
      if (fault('trace-404')) return send(404, `<pre>${trace}</pre>`);
      if (fault('soft-404')) return send(200, ssrPage);
      return send(404, '<!doctype html><html><body><app-root ng-version="21.0.0" ng-server-context="ssr">Page not found</app-root></body></html>');
  }
}
function bareHost(h) {
  return (h ?? '').split(':')[0].toLowerCase();
}

if (upstream) {
  const server = http.createServer((req, res) => handle(req, res, true));
  await new Promise((resolve) => server.listen(0, args.bind ?? '127.0.0.1', resolve));
  fs.writeFileSync(args.ready, JSON.stringify({ https: 0, http: server.address().port, pid: process.pid, banner: 'sd195-mock-shop' }));
} else {
const key = fs.readFileSync(args.key);
const cert = fs.readFileSync(args.cert);
const secureContext = tls.createSecureContext({ key, cert });

const httpsServer = https.createServer(
  {
    key,
    cert,
    // Like ssl_reject_handshake: a name nobody here owns gets no certificate and no answer.
    SNICallback: (servername, cb) => {
      if (known(servername) || fault('unknown-host-200')) cb(null, secureContext);
      else cb(new Error(`no site for ${servername}`));
    },
  },
  (req, res) => handle(req, res, true),
);
httpsServer.on('tlsClientError', () => {});
const httpServer = http.createServer((req, res) => handle(req, res, false));

await new Promise((resolve) => httpsServer.listen(0, '127.0.0.1', resolve));
httpsPort = httpsServer.address().port;
await new Promise((resolve) => httpServer.listen(0, '127.0.0.1', resolve));
const httpPort = httpServer.address().port;
// no-http: the port was free a moment ago and nothing listens on it now, which is what a closed port 80 looks like.
if (fault('no-http')) await new Promise((resolve) => httpServer.close(resolve));
fs.writeFileSync(args.ready, JSON.stringify({ https: httpsPort, http: httpPort, pid: process.pid, banner: 'sd195-mock-shop' }));
}
