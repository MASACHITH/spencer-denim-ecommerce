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
