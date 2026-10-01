# Colombo Wardrobe frontend (Angular 21 workspace)

The npm package is still `spencer-denim-frontend` and the shared library is still
`@spencer-denim/shared`: those are identifiers, not brand, and renaming them would rewrite every
import in the workspace for nothing a shopper sees. The *name* the applications display lives in
`projects/shared/src/lib/branding/brand.ts` and is read from there everywhere.

| Project | Type | Notes |
|---|---|---|
| `storefront` | Application, SSR (Node/Express) | Customer site. `ng serve storefront` on http://localhost:4200 |
| `admin` | Application, client-side only | Served under `/admin/`. `ng serve admin` on http://localhost:4300/admin/ |
| `shared` | Library (`@spencer-denim/shared`) | Design tokens + base CSS (`src/styles`), API helpers, shared UI |

Common commands (run in this folder):

```powershell
npm ci
npm run start:storefront      # proxies /api and /health to http://localhost:5080 (dotnet watch)
npm run start:admin
npm run lint
npm run test:ci               # Vitest, headless, all three projects
npm run build:all             # production builds into dist/
```

Server-side rendering environment variables (storefront Node server):

| Variable | Purpose |
|---|---|
| `PORT` | Listen port (default 4000) |
| `API_INTERNAL_URL` | API address used during SSR (default `http://localhost:5080`; browser calls stay same-origin `/api`) |
| `NG_ALLOWED_HOSTS` | Comma-separated public host names allowed for SSR (localhost is always allowed) |

See "Getting started" in the repository README for the full local workflow. Docker packaging is deferred to W0-15.
