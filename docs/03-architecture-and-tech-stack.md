# 03 — Architecture & Tech Stack

Last updated: 2026-09-15 · Status: **Approved** (user's stack; recommendations accepted)

**Local toolchain (verified 2026-09-15):** .NET SDK 10.0.201 · Node 24.13.1 · npm 11.10 · Angular CLI 21.1.4 · git 2.53 · gh 2.97 · **Docker: not installed. Docker Desktop is required** for local SQL Server and compose.

## 1. Recommendation

**Go with the team's stack: ASP.NET Core + Angular + SQL Server, all in Docker.** It fits this project:

| Requirement | Why this stack fits |
|---|---|
| Move to Azure next year | .NET containers → Azure Container Apps / App Service. SQL Server → Azure SQL (same engine, bacpac migration). Easiest possible move. |
| Runs on 2 vCPU / 8 GB VPS | .NET API ~200–400 MB, SQL Server ~2 GB (Express buffer pool is capped at 1,410 MB), Angular SSR ~300 MB. Fits with headroom. |
| E-commerce SEO | Angular SSR (server-side rendering + hydration) makes product and category pages indexable. |
| Developer expertise | Fastest delivery and lowest defect rate come from the stack the developer already knows. That matters with a 1-month plan. |
| Long-term support | .NET 10 is LTS (supported to Nov 2028). |

**Alternatives considered:** Node/Next.js + PostgreSQL (lighter, but a new stack to learn under deadline). WooCommerce/Shopify (fast, but the custom admin dashboard and the Azure move don't fit well). Rejected.

## 2. Stack

| Layer | Choice | Notes |
|---|---|---|
| API | ASP.NET Core Web API, **.NET 10 LTS** | Minimal APIs or controllers, OpenAPI |
| ORM | EF Core + migrations | |
| Validation | FluentValidation | |
| Mapping / mediator | Hand-written mappers + simple handler interfaces | **Avoid MediatR & AutoMapper**: both moved to commercial licences in 2025 |
| Auth | ASP.NET Core Identity. HttpOnly secure cookies + antiforgery (same-origin via proxy). Separate customer and admin cookie schemes. | Admin uses **permission-based policies** (see §3a). No JWT in localStorage. |
| Database | **SQL Server 2025 Express** on Linux container: one instance hosting the production **and** staging databases | Free for production. Express 2025 limits: 50 GB per database, 1,410 MB buffer pool, lesser of 1 socket / 4 cores, no SQL Server Agent (jobs run in .NET), no backup compression. Full-text is included but needs the `mssql-server-fts` package on Linux (custom image; not used in v1). Collation `Latin1_General_100_CI_AS_SC`. Images are **not** stored in the DB. Details: [08](08-database-design.md). |
| Storefront | **Angular 21** with **SSR** | Standalone components, signals, lazy routes |
| Admin | Separate Angular app in the same workspace, client-side only | Customers never download admin code |
| Charts (admin) | Chart.js via ng2-charts, or ECharts | |
| Images | Upload → resize to WebP (SkiaSharp or NetVips, both permissive licences) → served by proxy with long cache | Avoid ImageSharp licence ambiguity |
| Email | SMTP behind `IEmailSender` | Provider per Q8 |
| Background jobs | .NET `BackgroundService` (no SQL Agent on Express) | Expire unpaid orders (bank transfer and PayPal), poll stuck PayPal captures, send email outbox, nightly product stats + traffic aggregates, retention purges, stock reconcile (alert-only). Backups run in a separate cron sidecar container, not in the API. See [08](08-database-design.md). |
| Logging | Serilog → console + rolling file. Health checks at `/health`. | |
| Reverse proxy | **Caddy** (automatic HTTPS) or Nginx + certbot | Caddy recommended: simpler TLS |
| Tests | xUnit + Testcontainers (real SQL Server) · Angular unit tests · **Playwright** E2E + responsive + axe accessibility | See [06](06-engineering-process.md) |
| CI/CD | GitHub Actions (or Azure DevOps): build, test, push images to registry, SSH `docker compose pull && up -d` | |

## 3. Solution structure (clean architecture, SOLID)

```
spencer-denim-ecommerce/
├── src/
│   ├── backend/
│   │   ├── SpencerDenim.Domain/          # Entities, value objects, domain rules. No dependencies.
│   │   ├── SpencerDenim.Application/     # Use cases per feature, interfaces (ports), DTOs, validators
│   │   │   ├── Catalog/  Orders/  Payments/  Customers/  Inventory/
│   │   │   ├── Promotions/  Reviews/  Reporting/  Traffic/  Content/
│   │   ├── SpencerDenim.Infrastructure/  # EF Core, SQL Server, PayPal, SMTP, file storage, jobs
│   │   └── SpencerDenim.Api/             # HTTP endpoints, auth, composition root
│   └── frontend/                         # Angular workspace
│       ├── projects/storefront/          # SSR
│       ├── projects/admin/
│       └── projects/shared/              # UI kit, design tokens, models
├── tests/
│   ├── SpencerDenim.UnitTests/
│   ├── SpencerDenim.IntegrationTests/
│   └── e2e/                              # Playwright
├── deploy/
│   ├── docker-compose.yml  docker-compose.prod.yml  Caddyfile
│   └── backup/
└── docs/
```

### SOLID in practice

| Principle | Applied as |
|---|---|
| **S**ingle responsibility | One use-case handler per action (`PlaceOrder`, `ApproveBankTransfer`). Controllers stay thin. |
| **O**pen/closed | `IPaymentProvider` strategy: PayPal and BankTransfer now, and Koko or a local gateway later **without touching checkout code**. Same for `IShippingFeeCalculator` and `ICourierTrackingLinkProvider`. |
| **L**iskov | Every `IPaymentProvider` honours the same contract (Initiate → Confirm/Fail). Contract tests run against each one. |
| **I**nterface segregation | Small ports: `IFileStorage`, `IEmailSender`, `IClock`, `IOrderRepository`, not one giant service. |
| **D**ependency inversion | Application depends on interfaces. Infrastructure implements them. DI is wired in Api only. |

### 3a. Admin access control (RBAC)
- **Permissions** are code constants in the form `Module.Action`: modules × {View, Create, Edit, Delete, Approve, Export}, e.g. `Orders.Approve`, `Products.Edit`, `Users.Manage`.
- **Roles** are stored in the DB and hold a set of permissions. Super Admin can create and edit roles. Four default roles are seeded (Super Admin, Store Manager, Order Staff, Content Editor).
- **API:** every admin endpoint declares `RequirePermission("Orders.Approve")`. A custom `IAuthorizationHandler` checks the user's permissions (cached, invalidated when a role changes). This is the security boundary.
- **Admin UI:** route guards + a `*hasPermission` structural directive hide menus and buttons. This is for convenience only, never for security.
- **Audit log:** an EF Core `SaveChanges` interceptor records user, entity, action, before/after and timestamp for admin changes.
- The last Super Admin cannot be deleted or demoted.
- **No privilege escalation:** only a Super Admin (GrantsAll role) can edit roles or assign the Super Admin role, and nobody can grant a permission they don't hold. Refunds need `Orders.Refund`. Bank and PayPal settings need `Settings.Payments`. Email/SMTP, security and legal-retention settings need `Settings.Security`, which only Super Admins hold and which cannot be granted to any other role. A user who manages users can only manage staff whose permissions are a subset of their own. Every settings change is audited. Payment and security changes email all Super Admins and show a dashboard banner until acknowledged.
- **Super Admin safety:** 2FA mandatory, at least two Super Admin accounts recommended, time-boxed automatic lockout for everyone, and an audited break-glass CLI (migrator credentials entered interactively, never stored in `.env`). Details in [08](08-database-design.md).

### 3b. Media (images & video)
- Uploads go through `IMediaService` → `IFileStorage` (local volume now, Azure Blob later).
- **Images:** type and size checked by file signature (not just extension), resized to WebP renditions (thumb / card / large / zoom), and served with long cache headers.
- **Video:** MP4 (H.264) only, max 50 MB, poster image required. **No server-side conversion**, to protect the 2-vCPU VPS. Served with HTTP range requests for streaming.
- Media library entities track usage, so a file in use cannot be deleted.

### 3c. Payments
- `IPaymentProvider` implementations: `BankTransferProvider` (week 3) and `PayPalProvider` (week 4, built last). A future gateway (TBD) is a new class + settings, and checkout code does not change.
- Provider credentials are stored encrypted in Settings (ASP.NET Data Protection), editable only with the `Settings.Payments` permission.
- **PayPal cannot charge in LKR** (not a supported currency). Charge USD at an admin-set rate plus a buffer (default 3%), show the USD amount before redirect, and refund the captured USD amount. *Pending client confirmation.*
- A capture first claims the order (only if still PendingPayment). Late or duplicate captures go to a `CapturedRequiresRefund` status with an alert. Webhook signatures are verified before the event is stored.

### Key domain rules (to design carefully)
- **Order states:** `PendingPayment → Paid → Processing → Shipped → Delivered`, plus `Cancelled` / `Expired` / `Refunded`. Transitions live in the Domain.
- **Stock:** reserved at order placement. Released when the order expires (bank transfer not approved within N hours, default 48; PayPal not completed within its window) or is cancelled. A PayPal capture reported as PENDING extends the hold up to N days (default 5, client choice), and the hold is shortened again if the capture is denied. Deducted on Paid. Restocked once, on refund. Reservations are derived from order lines ([08](08-database-design.md) §5).
- **Prices:** stored in LKR as `decimal(18,2)`. The order snapshot copies product name, variant and price at purchase time.
- **PayPal:** server-side order create/capture + webhook verification. Never trust client-side success. Idempotent webhook handling.
- **Promo + sale:** clear precedence rule (sale price first, then promo code). Usage limits enforced with a DB transaction.

## 4. Deployment on Hostinger KVM 2

```
Internet ──► Caddy (443, TLS) ─┬─► storefront-ssr (Node, Angular SSR)   ~300–500 MB
                               ├─► /admin  static files (served by Caddy)
                               ├─► /api    api (.NET 10)              ~300–500 MB
                               └─► /media  public uploads volume (static, cached)
                                          │   private uploads volume (bank slips) → NOT mounted in Caddy, served only via API
                                   sqlserver (Express, max server memory ≈ 2 GB, prod + staging DBs)
                                   backup job (full + 15-min log backups + uploads → off-server storage)
```

- **`/media` is a shared namespace, and the proxy rule has to fall through.** The Angular browser build emits its own webfonts under `/media` (`dist/storefront/browser/media/inter-*.woff2`), so a Caddy rule that serves `/media` from the public uploads volume and stops there will shadow them and the storefront loads with fallback type. The rule must try the uploads volume and then fall through to the SSR server on a miss (`try_files`-style), or the build must be given a different `resourcesOutputPath`. Whichever is chosen, `/media` must still serve the uploads volume with the year-long immutable cache, the strong `ETag` and byte-range support that [08](08-database-design.md) §3.3 relies on — the `E2E_MEDIA_PASSTHROUGH_URL` route in the SSR server is a browser-test fixture with none of those and must never be used for this.
- **RAM budget (8 GB):** SQL ~2 GB (`max server memory`; Express caps the buffer pool at 1,410 MB, so more is wasted; **one SQL instance is shared by the production and staging databases**) · API 0.5 GB · SSR 0.5 GB · Caddy 0.1 GB · OS/Docker ~1 GB · staging API + SSR ~1 GB · headroom ~3 GB.
- OS: Ubuntu 24.04 LTS. Firewall: only 22 (key-only SSH), 80, 443. SQL port **never** exposed. Fail2ban enabled.
- **Backups:** full SQL backups plus transaction-log backups every 15 minutes, and the uploads volumes. All are shipped off the server (e.g. Backblaze B2 / Azure Blob, a few $/month, to confirm with client) and kept 35 days; retention details are in [08](08-database-design.md) §8.5. Staging uses SIMPLE recovery. Hostinger snapshots are extra, not the only copy. A restore rehearsal happens before go-live (target RPO 15 min, ≤ 1 h if the host is lost; RTO 4 h).
- **Secrets:** `.env` on the server (not in git) holds **bootstrap secrets only**: the SQL connection string and password, the Data Protection certificate path and password, and the first Super Admin credentials. Runtime secrets (PayPal credentials, SMTP password) are stored **encrypted in admin Settings** with ASP.NET Data Protection, whose key ring is protected by that certificate ([08](08-database-design.md) §3.8). The certificate (password-protected) is included in off-server backups; its password is kept outside the server.

## 5. Azure migration path (next year)

| Today (VPS) | Azure | Effort |
|---|---|---|
| api / storefront containers | Azure Container Apps or App Service for Containers | Same images, new env vars |
| SQL Server Express container | Azure SQL Database | bacpac export/import |
| Uploads volume via `IFileStorage` (local disk) | Azure Blob Storage adapter | One new class + config |
| Caddy TLS/proxy | Container Apps ingress / Front Door | Config only |
| `.env` secrets | Key Vault | Config only |
| GitHub Actions → SSH deploy | GitHub Actions → Azure deploy | Pipeline change |

**Rule for the whole build:** no code may depend on the host. Paths, URLs, connection strings and storage all come from configuration.

## 6. Decisions
- ✅ Stack approved: .NET 10 + Angular 21 + SQL Server Express, Docker.
- ✅ Caddy as reverse proxy (automatic HTTPS).
- ✅ GitHub + GitHub Actions.
- ⏳ Off-server backup target (recommend Backblaze B2, a few USD/month, client pays). Confirm before go-live.
- ⏳ Install Docker Desktop on the development machine.
