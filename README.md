# Spencer Denim — Fashion E-Commerce Platform

Production build of the fashion e-commerce website for **Spencer Denim Industries Pvt Ltd** (client: Manusha Weerawardene), delivered by **TU2SL**.

> Status: **Build phase.** The scope baseline ([docs/02](docs/02-scope-baseline.md)) is signed off and the project scaffold (W0-09) runs locally: .NET 10 API on LocalDB plus the Angular 21 storefront (SSR) and admin — see [Getting started](#getting-started). Features are built from [TASKS.md](TASKS.md), and nothing outside the scope baseline is built. Docker packaging is deferred to W0-15.

## Source of truth

Committed copies live under [`reference/`](reference/), so a fresh clone has everything. The originals stay outside the repository in the working folder (`douments/`, `tuasl-fashion/`).

| Priority | Source | Location in this repository |
|---|---|---|
| 1 | Signed budget proposal TU2SL-2026-0823 (23 Aug 2026) | `reference/client-documents/Spencer-Denim-Ecommerce-Website-Budget-Proposal.pdf` |
| 2 | Theme reference (replaces "Denim & Co. × Natural Linen") | `reference/client-documents/Refrence sites/refernce.txt` → https://bananarepublic.gap.com/ |
| 3 | Demo prototype shown to client (reference only — **not** reused as code) | `reference/tuasl-fashion-demo/` |
| 4 | Content checklist sent to client | `reference/tuasl-fashion-demo/Client/Spencer-Denim-Content-Checklist.pdf` |

Older drafts in `reference/tuasl-fashion-demo/Client/` (`CLIENT_SCOPE_PROPOSAL.md`, `CLIENT_COMMERCIAL_PROPOSAL.html`, `TUASL_Fashion_Proposal - Copy.html`) have been **replaced** by the signed PDF. Where they disagree, the PDF wins.

## Documents

| # | Document | Purpose |
|---|---|---|
| 01 | [Project understanding & open questions](docs/01-project-understanding.md) | What we understood, gaps, questions to answer before build |
| 02 | [Scope baseline](docs/02-scope-baseline.md) | Agreed features with IDs, demo items to remove, items to add, out of scope |
| 03 | [Architecture & tech stack](docs/03-architecture-and-tech-stack.md) | Stack, Docker, VPS layout, SOLID structure, Azure migration path |
| 04 | [Design system](docs/04-design-system.md) | Theme direction (Banana Republic–inspired), responsive rules |
| 05 | [Delivery plan & client tracker](docs/05-delivery-plan.md) | 1-month internal plan, milestones, client progress page |
| 06 | [Engineering process](docs/06-engineering-process.md) | Developer → tester agent loop, Definition of Done, test strategy |
| 07 | [Client progress update guide](docs/07-client-progress-update-guide.md) | How the team updates the client progress page (team only) |
| 08 | [Database design](docs/08-database-design.md) | Baselined schema (65 tables), state machines, stock concurrency, pricing |
| 09 | [Git & repository rules](docs/09-git-and-repository-rules.md) | **Push only to MASACHITH/spencer-denim-ecommerce with the personal account**, never the office account |
| 10 | [Client progress page publishing](docs/10-client-progress-publishing.md) | FTPS publishing of the task sheet **only** (4 files) to colombowardrobecompany-build-tracker.kt-wedding.online |
| 11 | [Multi-session coordination](docs/11-multi-session-coordination.md) | Handoff protocol between Claude Code sessions/machines, machine readiness check, known machines |
| — | [Guides](docs/guides/) | DEV-GUIDE-01 Docker Desktop on Windows · DEV-GUIDE-02 Fix "WSL needs updating" (PDF + HTML source) |
| — | [TASKS.md](TASKS.md) | Live task board (internal) |
| — | [MEMORY.md](MEMORY.md) | Running project log: decisions, what's done, what's next |

## Getting started

> Docker is **deferred to W0-15** (the office firewall blocks it). Everything below runs directly on Windows.

### Prerequisites

| Tool | Version | Check |
|---|---|---|
| .NET SDK | 10.0 (see `global.json`) | `dotnet --version` |
| Node.js / npm | Node 24, npm 11 | `node -v` |
| SQL Server LocalDB | 2019 (15.x) or later, instance `(localdb)\MSSQLLocalDB` | `sqllocaldb info MSSQLLocalDB` |

Only use LocalDB and databases named `SpencerDenim_*`. Never point the app or the tests at the default SQL Server instance (`.`, `localhost`, `MSSQLSERVER`).

### Layout

| Path | What |
|---|---|
| `SpencerDenim.sln` | Backend solution (run `dotnet` commands from the repository root) |
| `src/backend/` | `Domain` → `Application` → `Infrastructure` → `Api` (clean architecture, minimal APIs) |
| `tests/` | `SpencerDenim.UnitTests` (incl. architecture rules), `SpencerDenim.IntegrationTests` |
| `src/frontend/` | Angular 21 workspace: `storefront` (SSR), `admin` (client-side), `shared` (tokens, base CSS, API helpers) |
| `.github/workflows/ci.yml` | CI: backend build + tests with a SQL Server service container, frontend lint + tests + builds |

### 1. Database and connection string

Create the empty development database once, with the platform collation:

```powershell
sqlcmd -S "(localdb)\MSSQLLocalDB" -E -Q "IF DB_ID(N'SpencerDenim_Dev') IS NULL CREATE DATABASE [SpencerDenim_Dev] COLLATE Latin1_General_100_CI_AS_SC;"
```

The API reads `ConnectionStrings:Database`. In Development, `appsettings.Development.json` already holds the credential-free LocalDB value:

```text
Server=(localdb)\MSSQLLocalDB;Database=SpencerDenim_Dev;Trusted_Connection=True;TrustServerCertificate=True
```

To use anything else (and **always** for connection strings with a user name or password), override it with an environment variable or user-secrets. Never commit credentials.

```powershell
# Environment variable (current PowerShell session)
$env:ConnectionStrings__Database = "Server=(localdb)\MSSQLLocalDB;Database=SpencerDenim_Dev;Trusted_Connection=True;TrustServerCertificate=True"

# or user-secrets (stored in your profile, outside the repository)
dotnet user-secrets set --project src/backend/SpencerDenim.Api "ConnectionStrings:Database" "<connection string>"
```

Other settings (environment variable names):

| Variable | Default | Purpose |
|---|---|---|
| `Database__CompatibilityLevel` | `150` | SQL generated for this level. 150 = LocalDB/SQL Server 2019; use 170 for SQL Server 2025 Express and Azure SQL |
| `Database__MaxRetryCount` | `3` | EF Core retry-on-failure count |
| `Database__CommandTimeoutSeconds` | `30` | Command timeout |
| `Logging__ConsoleFormat` | `text` in Development, else `json` | Serilog console output |

### 2. Run the API

```powershell
dotnet watch --project src/backend/SpencerDenim.Api
```

It listens on http://localhost:5080:

| URL | What |
|---|---|
| `GET /api/ping` | Reachability check (no database) |
| `GET /health` | Liveness: the process answers |
| `GET /health/ready` | Readiness: includes SQL Server connectivity (503 when unreachable) |
| `/api/swagger` | Swagger UI, **Development only** (OpenAPI JSON at `/api/openapi/v1.json`) |

### 3. Run the storefront and admin (dev servers + proxy)

```powershell
cd src/frontend
npm ci
npx ng serve storefront   # http://localhost:4200
npx ng serve admin        # http://localhost:4300/admin/
```

`proxy.conf.json` forwards `/api` and `/health` to the API on http://localhost:5080, so start the API first. Each placeholder home page calls `/api/ping` and shows "API connected" or a neutral "not reachable" message. During server-side rendering the storefront calls the API directly at `API_INTERNAL_URL` (default `http://localhost:5080`).

### 4. Run the tests

```powershell
# Backend: unit + integration tests
dotnet test

# Integration tests against LocalDB (otherwise the SQL-backed tests are skipped, not failed)
$env:TEST_SQL_CONNECTION = "Server=(localdb)\MSSQLLocalDB;Trusted_Connection=True;TrustServerCertificate=True"
dotnet test
Remove-Item Env:TEST_SQL_CONNECTION
```

The integration fixture creates a fresh `SpencerDenim_Test_<guid>` database (collation `Latin1_General_100_CI_AS_SC`) on that server and drops it after the run. Don't put a database name in `TEST_SQL_CONNECTION`; the fixture picks its own.

```powershell
# Frontend (from src/frontend)
npm run lint
npm run test:ci           # Vitest, headless, shared + storefront + admin
```

### 5. Build and run the storefront SSR server locally

```powershell
cd src/frontend
npm run build:all                                   # dist/storefront (browser + server), dist/admin
$env:PORT = "4000"; $env:API_INTERNAL_URL = "http://localhost:5080"
node dist/storefront/server/server.mjs              # http://localhost:4000 (GET /healthz for liveness)
```

The server renders the page on each request, so the initial HTML already contains the "Spencer Denim" heading and the API status. For a public host name, add it to `NG_ALLOWED_HOSTS` (comma-separated).

### CI

`.github/workflows/ci.yml` runs on every push and pull request: the backend job builds and tests against a `mcr.microsoft.com/mssql/server:2022-latest` service container; the frontend job runs `npm ci`, lint, headless unit tests and both production builds.

## Rules for anyone working on this project

1. Read `MEMORY.md` and `TASKS.md` first, every session.
2. Nothing is built that is not in [02-scope-baseline.md](docs/02-scope-baseline.md). New requests → logged as change requests (chargeable, per proposal §5).
3. Every task goes developer → tester; a failed test goes back to the developer until it passes ([06](docs/06-engineering-process.md)).
4. Every screen must work at 320 / 375 / 390 / 768 / 1024 / 1280 / 1440 / 1920 px.
5. Update `MEMORY.md` and `TASKS.md` at the end of every working session. Update the docs whenever a decision changes them.
