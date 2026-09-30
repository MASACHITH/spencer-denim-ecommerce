import {
  AngularNodeAppEngine,
  createNodeRequestHandler,
  isMainModule,
  writeResponseToNodeResponse,
} from '@angular/ssr/node';
import express from 'express';
import { join } from 'node:path';

const browserDistFolder = join(import.meta.dirname, '../browser');

const app = express();
app.disable('x-powered-by');

/**
 * Allowed host names come from NG_ALLOWED_HOSTS (comma-separated), read by the engine at start-up.
 * localhost is always allowed. Production must list the public domain(s).
 */
const angularApp = new AngularNodeAppEngine();

/** Liveness probe for process managers and the reverse proxy. Does not render Angular. */
app.get('/healthz', (_req, res) => {
  res.setHeader('Cache-Control', 'no-store');
  res.json({ status: 'ok' });
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
 * `/media` on the storefront's own origin, forwarded to an internal media service.
 *
 * `CatalogOptions.MediaBaseUrl` is `/media` by default, so the API publishes media addresses on
 * the storefront's own origin and something in front has to route them: Caddy, from the public
 * uploads volume (doc 03 §"Deployment topology"), or the API's own `MediaFileEndpoints`. In
 * production that is still true and this is never mounted — the variable is unset, the route
 * does not exist, and nothing changes. It is here for the one stack with no reverse proxy in
 * it, the browser tests, whose media come from the catalogue stub on another port. Without it
 * the stub would have to publish absolute URLs naming that port, and those URLs are
 * server-rendered into the markup — the internal address GLB-11 (`product-listing.e2e.ts`)
 * refuses to let the HTML carry.
 *
 * Reads its target the same way, and for the same reason, as `API_INTERNAL_URL`: what is behind
 * the storefront is deployment configuration, not something the application may assume.
 *
 * Mounted **after** the static middleware, and it has to stay there: the browser build writes
 * its own fonts to `/media` too (`dist/storefront/browser/media/inter-*.woff2`), so `/media` is
 * a shared namespace on this origin, not the uploads volume's alone. In front of the static
 * files this route swallowed every webfont and the homepage's own "no failed requests" check
 * caught it. Whatever routes `/media` in a deployment has the same problem to solve.
 */
const mediaInternalUrl = process.env['MEDIA_INTERNAL_URL'];

if (mediaInternalUrl) {
  app.use('/media', (req, res, next) => {
    if (req.method !== 'GET' && req.method !== 'HEAD') {
      next();
      return;
    }

    void (async () => {
      try {
        // `originalUrl` and not `req.url`: mounting on a path strips the prefix, and the media
        // service is addressed by the same `/media/...` key the catalogue published.
        const upstream = await fetch(new URL(req.originalUrl, mediaInternalUrl), {
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
 * Start the server if this module is the main entry point, or it is run via PM2.
 * The server listens on the port defined by the `PORT` environment variable, or defaults to 4000.
 */
if (isMainModule(import.meta.url) || process.env['pm_id']) {
  const port = Number(process.env['PORT'] || 4000);
  const server = app.listen(port, (error) => {
    if (error) {
      throw error;
    }

    console.log(`Spencer Denim storefront (SSR) listening on http://localhost:${port}`);
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
