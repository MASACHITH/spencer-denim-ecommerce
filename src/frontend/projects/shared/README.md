# @spencer-denim/shared

Shared library for the storefront and admin apps. It is consumed from source through the `@spencer-denim/shared`
path mapping in `tsconfig.json`; it is not published to npm.

| Path | Contents |
|---|---|
| `src/styles/tokens.css` | Design tokens from `docs/04-design-system.md` §3 as CSS custom properties |
| `src/styles/base.css` | Global reset, including `[hidden] { display: none !important; }` (lesson L2) |
| `src/lib/api/` | `API_BASE_URL` + interceptor (server-side API address for SSR) and `PingApi` |
| `src/lib/api-status/` | `sd-api-status`: shows whether `GET /api/ping` answers; fails to a neutral message (lesson L1) |

Both CSS files are added to each app's `styles` array in `angular.json`. Components use the tokens only, never
hard-coded colours.

```powershell
npx ng test shared --watch=false   # Vitest, headless (jsdom)
npx ng lint shared
```
