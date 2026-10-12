import {
  AngularNodeAppEngine,
  createNodeRequestHandler,
  isMainModule,
  writeResponseToNodeResponse,
} from '@angular/ssr/node';
import express from 'express';
import { join } from 'node:path';
import { BRAND } from '@spencer-denim/shared';

import { canonicalTarget, redirectStatusFor } from './server/canonical-path';
import { answerRenderFailure } from './server/render-failure';
import { ROBOTS_MAX_AGE_SECONDS, robotsTxt } from './server/robots-txt';
import { SitemapDocument } from './server/sitemap-xml';

const browserDistFolder = join(import.meta.dirname, '../browser');

const app = express();
app.disable('x-powered-by');

/**
 * Allowed host names come from NG_ALLOWED_HOSTS (comma-separated), read by the engine at start-up.
 * localhost is always allowed. Production must list the public domain(s).
 */
const angularApp = new AngularNodeAppEngine();

/**
 * **First, before anything that can route or render: one leading slash, not several.**
 *
 * `//robots.txt` answered **HTTP 500** — and a 5xx on `robots.txt` tells a crawler the whole site is
 * disallowed, which is the one outcome everything below exists to prevent. `src/server/canonical-path.ts`
 * has the full account; the short version is that `@angular/platform-server` throws NG05702 for a
 * protocol-relative URL, but only once a component is about to render, so the throw was unreachable
 * while the catch-all was a redirect and became reachable the moment it became a page (#28).
 *
 * Mounted at the top on purpose, and it tests the **effective** path — the one a URL parser produces
 * after resolving dot segments — not the raw request target. Testing the raw target is how F1's fix was
 * narrowed rather than complete: `/.//robots.txt` arrives with one leading slash and ends up with two, so
 * nine shapes went on answering 500 for a round (F9).
 *
 * Nothing below may assume it has been given a canonical path, and nothing below is where this belongs: a
 * route is exactly the thing that proved unreliable.
 */
app.use((req, res, next) => {
  const canonical = canonicalTarget(req.url);
  if (canonical === null) {
    next();
    return;
  }

  res.redirect(redirectStatusFor(req.method), canonical);
});

/**
 * **Forwarded headers nobody vouches for are dropped before anything renders** (W0-15).
 *
 * Angular's engine renders on the server only if every `X-Forwarded-*` header it receives is one it
 * was told to trust (`NG_TRUST_PROXY_HEADERS`); a single other one — `X-Forwarded-Server`,
 * `X-Forwarded-Ssl`, or a name a visitor simply made up — makes it serve the empty client-side shell,
 * with no content for a crawler and no `og:` tags. The gateway drops the common ones, but a list of
 * names can never be complete, so the rule lives here: anything under `x-forwarded-` that is not on the
 * trusted list is removed. The trusted ones are the gateway's own, overwritten on every request.
 */
/**
 * Unset must mean **what the engine's own default means**, not "trust nothing".
 *
 * `normalizeTrustProxyHeaders` in `@angular/ssr` maps an absent `trustProxyHeaders` to
 * `{x-forwarded-host, x-forwarded-proto}`, so falling back to `''` here made this list and the engine's
 * disagree in exactly the case nobody configures: every `x-forwarded-*` header was stripped, including the
 * two the engine would have honoured. The page still server-renders — so nothing fails — but the engine then
 * reconstructs the request URL from the internal `Host` and `http`, which is the silent half of the bug this
 * middleware exists to prevent. Compose always sets the variable; a `docker run` or an edited compose file
 * would not. Reading the same variable with the same default is what keeps the two in step.
 */
const configuredForwardedHeaders = process.env['NG_TRUST_PROXY_HEADERS'];
const trustedForwardedHeaders = new Set(
  (configuredForwardedHeaders === undefined || configuredForwardedHeaders.trim() === ''
    ? 'x-forwarded-host,x-forwarded-proto'
    : configuredForwardedHeaders
  )
    .split(',')
    .map((name) => name.trim().toLowerCase())
    .filter((name) => name.length > 0),
);

app.use((req, _res, next) => {
  for (const name of Object.keys(req.headers)) {
    if (name.startsWith('x-forwarded-') && !trustedForwardedHeaders.has(name)) {
      delete req.headers[name];
    }
  }

  next();
});

/** Liveness probe for process managers and the reverse proxy. Does not render Angular. */
app.get('/healthz', (_req, res) => {
  res.setHeader('Cache-Control', 'no-store');
  res.json({ status: 'ok' });
});

/**
 * `robots.txt` and `sitemap.xml` (GLB-11), answered here and **not** by the Angular router.
 *
 * **Why the position in this file matters.** Before this existed there was no such file anywhere in
 * the build, so a request for `/robots.txt` fell all the way through to the router — and a route that
 * matched it would have rendered a page or failed. **A crawler answered 5xx for `robots.txt` treats
 * the entire site as disallowed** until it can read one, so the one place that answer must not be able
 * to come from is a route table. These two routes are mounted ahead of the static middleware as well,
 * so neither can be shadowed by a file that lands in `public/` later: both have to be generated,
 * because both carry this deployment's own public origin and nothing checked into the repository can
 * know what that is (doc 03 §5).
 *
 * Express answers HEAD from a GET route with the body dropped, which is what a link checker and most
 * uptime monitors lead with.
 */
app.get('/robots.txt', (req, res) => {
  res.type('text/plain; charset=utf-8');
  res.setHeader('Cache-Control', `public, max-age=${ROBOTS_MAX_AGE_SECONDS}`);
  res.send(robotsTxt(req.headers));
});

/** One document per process, so the cache inside it is shared by every request (see the class). */
const sitemap = new SitemapDocument();

app.get('/sitemap.xml', (req, res, next) => {
  void sitemap
    .answer(req.headers)
    .then((answer) => {
      res.status(answer.status);
      res.type(answer.contentType);
      res.setHeader('Cache-Control', answer.cacheControl);
      if (answer.retryAfter !== undefined) {
        res.setHeader('Retry-After', String(answer.retryAfter));
      }
      res.send(answer.body);
    })
    // `answer` is documented never to reject; this is here so that if it ever does, the failure is
    // Express's problem rather than an unhandled rejection that takes the process with it.
    .catch(next);
});

/**
 * Serve static files from /browser with long caching (file names are content-hashed).
 */
app.use(
  express.static(browserDistFolder, {
    maxAge: '1y',
    index: false,
    redirect: false,
  }),
);

/**
 * **A test fixture. Never point this at a real media service.**
 *
 * `CatalogOptions.MediaBaseUrl` is `/media` by default, so the API publishes media addresses on
 * the storefront's own origin and something in front has to route them: Caddy, from the public
 * uploads volume (doc 03 §"Deployment topology"), or the API's own `MediaFileEndpoints`. This
 * route is not that. It exists for the one stack with no reverse proxy in it — the browser tests
 * — whose media come from the catalogue stub on another port. Without it the stub would have to
 * publish absolute URLs naming that port, and those URLs are server-rendered into the markup:
 * the internal address GLB-11 (`product-listing.e2e.ts`) refuses to let the HTML carry.
 *
 * It is deliberately named `E2E_...` rather than reading like a deployment knob, because it is
 * only good enough for a few-kilobyte fixture and silently wrong for anything else:
 *
 * - it forwards no `Range`, so a video could not be seeked through it;
 * - it forwards no `If-None-Match`/`If-Modified-Since` and returns no `ETag`, so every request
 *   is a full transfer and the 304 that `PublicMediaFiles` works to produce never happens;
 *   `Cache-Control: no-store` is set on purpose to stop a test caching a fixture, and is the
 *   exact opposite of the year-long immutable cache real media is served with;
 * - it buffers the whole body in memory.
 *
 * A deployment that needs `/media` on this origin routes it in the reverse proxy, which does all
 * three properly. If that ever stops being true, this is not the thing to reach for.
 *
 * Mounted **after** the static middleware, and it has to stay there: the browser build writes
 * its own fonts to `/media` too (`dist/storefront/browser/media/inter-*.woff2`), so `/media` is
 * a shared namespace on this origin, not the uploads volume's alone. In front of the static
 * files this route swallowed every webfont and the homepage's own "no failed requests" check
 * caught it. Whatever routes `/media` in a deployment has the same problem to solve — see the
 * deployment notes in `docs/03-architecture-and-tech-stack.md`.
 */
const e2eMediaPassthroughUrl = process.env['E2E_MEDIA_PASSTHROUGH_URL'];

if (e2eMediaPassthroughUrl) {
  app.use('/media', (req, res, next) => {
    if (req.method !== 'GET' && req.method !== 'HEAD') {
      next();
      return;
    }

    void (async () => {
      try {
        // `originalUrl` and not `req.url`: mounting on a path strips the prefix, and the media
        // service is addressed by the same `/media/...` key the catalogue published.
        const upstream = await fetch(new URL(req.originalUrl, e2eMediaPassthroughUrl), {
          method: req.method,
        });
        const contentType = upstream.headers.get('content-type');

        res.status(upstream.status);
        if (contentType) {
          // The type has to survive the hop: a `.webp` served as anything else is a picture the
          // browser refuses to decode, and a card that silently falls back to its placeholder.
          res.setHeader('Content-Type', contentType);
        }
        res.setHeader('Cache-Control', 'no-store');
        // Buffered rather than streamed: the only thing that ever travels this route is a test
        // fixture of a few kilobytes, and a stream would be a moving part with nothing to move.
        res.end(Buffer.from(await upstream.arrayBuffer()));
      } catch (error) {
        next(error);
      }
    })();
  });
}

/**
 * Handle all other requests by rendering the Angular application.
 */
app.use((req, res, next) => {
  angularApp
    .handle(req)
    .then((response) => (response ? writeResponseToNodeResponse(response, res) : next()))
    .catch(next);
});

/**
 * **The last line of defence, and the reason there is a second one at all.**
 *
 * The canonicalising redirect at the top closes the way NG05702 was reached, but a render can fail for
 * reasons nobody has thought of yet and the first defence should not be the only one. What it answers,
 * and why `robots.txt` is answered rather than failed, is in `src/server/render-failure.ts`, which is a
 * module so that it has a test rather than only a comment.
 */
app.use(
  (error: unknown, req: express.Request, res: express.Response, next: express.NextFunction) => {
    if (answerRenderFailure(error, req, res) === 'already-answered') {
      next(error);
    }
  },
);

/**
 * Start the server if this module is the main entry point, or it is run via PM2.
 * The server listens on the port defined by the `PORT` environment variable, or defaults to 4000.
 */
if (isMainModule(import.meta.url) || process.env['pm_id']) {
  const port = Number(process.env['PORT'] || 4000);
  const server = app.listen(port, (error) => {
    if (error) {
      throw error;
    }

    console.log(`${BRAND.name} storefront (SSR) listening on http://localhost:${port}`);
  });

  // Stop cleanly on SIGTERM/SIGINT so in-flight requests finish.
  const shutdown = (signal: string) => {
    console.log(`${signal} received, closing HTTP server`);
    server.close(() => process.exit(0));
    setTimeout(() => process.exit(1), 10_000).unref();
  };
  process.on('SIGTERM', () => shutdown('SIGTERM'));
  process.on('SIGINT', () => shutdown('SIGINT'));
}

/**
 * Request handler used by the Angular CLI (for dev-server and during build) or Firebase Cloud Functions.
 */
export const reqHandler = createNodeRequestHandler(app);
