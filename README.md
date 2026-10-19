# Colombo Wardrobe — Fashion E-Commerce Platform

Production build of the fashion e-commerce website for **Spencer Denim Industries Pvt Ltd** (client: Manusha Weerawardene), delivered by **TU2SL**. The shop trades as **Colombo Wardrobe** at `colombowardrobecompany.com` — that is the name on every page. The company name is the client's; `SpencerDenim.*`, `SpencerDenim.sln`, this repository and the `SpencerDenim_Dev` database keep their names as identifiers (owner's decision, 2026-10-02, `docs/02` §I2).

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
| 11 | [Business processes](docs/11-business-processes.md) | How the shop works end to end: catalogue, stock, checkout, payments, refunds, admin (WooCommerce-informed) |
| 12 | [Team collaboration](docs/12-team-collaboration.md) | **Parallel development**: issues, branch ownership, reviews, conflict-prone files, Claude sessions |
| 13 | [Onboarding](docs/13-onboarding.md) | New developer: install, first build, first pull request |
| 14 | [Multi-session coordination](docs/14-multi-session-coordination.md) | Handoff between Claude Code sessions/machines, machine readiness check, known machines |
| — | [CONTRIBUTING.md](CONTRIBUTING.md) | The short version of the workflow |
| — | [docs/journal/](docs/journal/) | One file per developer per day (keeps `MEMORY.md` conflict-free) |
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

Create the empty development database once, with the platform collation and `READ_COMMITTED_SNAPSHOT ON` (doc 08 §1.1), using the same script that creates the live database:

```powershell
sqlcmd -S "(localdb)\MSSQLLocalDB" -E -v DbName="SpencerDenim_Dev" -i tools\db\provision-database.sql
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
| `OrderPlacementThrottle__PermitsPerWindow` | `20` | Orders one visitor address may place per window (`POST /api/checkout/orders`, #107). Over it: 429 with `Retry-After`. A resend of an order already placed is never counted. In the deployed stack set `SD_ORDER_THROTTLE_PERMITS` / `_SHARED_PERMITS` / `_WINDOW_SECONDS` in `config/app.env` (see `deploy/app/.env.example`) — compose maps them to these three |
| `OrderPlacementThrottle__SharedAddressPermitsPerWindow` | `200` | The budget when the caller is not a visitor (loopback, private address, none): everybody behind one proxy. Must be at least the figure above |
| `OrderPlacementThrottle__WindowSeconds` | `600` | The window, and the `Retry-After` a refused caller is sent. 1 to 86400 |

### 2. Catalogue data: migrations and seed

The catalogue schema (doc 08 `catalog`, `media` and `inventory`) is created by EF Core migrations.
The database itself is **not** created by migrations, because the collation is fixed at
`CREATE DATABASE` (step 1 above, doc 08 §10.1).

```powershell
# Apply every migration to the database in ConnectionStrings:Database
dotnet ef database update --project src/backend/SpencerDenim.Infrastructure --startup-project src/backend/SpencerDenim.Api

# Roll back to an empty database
dotnet ef database update 0 --project src/backend/SpencerDenim.Infrastructure --startup-project src/backend/SpencerDenim.Api
```

`dotnet ef` needs the matching tool version: `dotnet tool update --global dotnet-ef --version 10.0.12`.

**Reference data needs no flag.** Doc 08 §9's reference data — provinces, districts, delivery methods
and their zone rates, payment providers, departments, categories, sizes, colours, attributes, size
guides, the homepage's structure and the `ops.Settings` keys — is established on **every** startup of
every environment, including production. None of it is sample data: a shop with no districts has an
empty district dropdown, no delivery options and an `Orders.DeliveryDistrictId NOT NULL` it cannot
satisfy, so it can never take an order. Migrate and start the API and it is there. The step is
idempotent — it inserts only rows that are missing and never overwrites an admin edit — so a second
startup over a seeded database inserts nothing. If it cannot run (no database, migrations not applied)
the API still starts — liveness and the OpenAPI document keep answering, so a late SQL Server is not a
crash loop — and it logs an error naming the fix. **`/health/ready` answers 503 until the data is really
there**, so an orchestrator does not route live traffic to a host that cannot take an order, and the
attempt is retried every 10 s, so applying the migrations or bringing the database up makes the host ready
on its own without a restart.

**Sample data is a separate, deliberate step** and is **off** unless you ask for it:

```powershell
$env:Seed__RunOnStartup = "true"          # this host may seed optional data
$env:Seed__IncludeDemoCatalogue = "true"  # the 45-product sample catalogue (development and staging only)
dotnet run --project src/backend/SpencerDenim.Api
# ... "Catalogue seeding inserted ..." appears in the log; stop the API and unset the variables
Remove-Item Env:Seed__RunOnStartup, Env:Seed__IncludeDemoCatalogue
```

| Variable | Default | Purpose |
|---|---|---|
| `Seed__RunOnStartup` | `false` | Allow this host to seed **optional** data at startup. Reference data is not gated by it |
| `Seed__IncludeDemoCatalogue` | `false` | Insert the sample catalogue (needs `Seed__RunOnStartup`). Never set this in production |
| `Catalog__DefaultLowStockThreshold` | `5` | Fallback when a variant has no threshold (doc 08 §9 `Inventory.DefaultLowStockThreshold`) |
| `Catalog__NewArrivalDays` | `30` | How long a published product keeps the "New" badge |
| `Catalog__BestSellerTopN` | `10` | Products per department that count as best sellers |
| `Catalog__DefaultPageSize` / `Catalog__MaxPageSize` | `24` / `96` | Listing page size and its upper bound |
| `Catalog__MediaBaseUrl` | `/media` | Public URL prefix a stored media key is appended to |
| `Catalog__RelatedProductCount` | `4` | Related products on the product page (PDP-06) |

#### Renaming the shop on a database that is already seeded (W1-15)

The seeders insert only missing rows and **never** overwrite an edit an admin may have made, so changing
seed data reaches **fresh databases only**. A database seeded before 2026-10-02 still holds SEO titles
ending `| Spencer Denim`, and re-running the seeder will not touch them — correctly, because it cannot
tell a stale seed value from a title the client typed.

That makes it a one-off operational step rather than a migration. A migration that rewrote `SeoTitle`
would also rewrite whatever the client has since written there, which is exactly what the seeders are
careful not to do. Run this by hand, once, per environment, and only on rows that still carry the old
suffix.

**`SET QUOTED_IDENTIFIER ON` is not optional, and leaving it out fails silently.** `catalog.Categories`
carries a filtered index, and SQL Server refuses any `UPDATE` on such a table unless that option is on.
`sqlcmd` defaults it **off** — which is the pattern this README documents everywhere else — so without the
first line below the run stops at `Msg 1934`, applies nothing, and **still exits 0**. Use
`sqlcmd -I -b` (`-I` sets the option for the session, `-b` makes a SQL error a non-zero exit code) or run
the block in SSMS, which has it on already. The `BEGIN TRAN` means a failure part-way leaves nothing
half-applied.

```sql
SET QUOTED_IDENTIFIER ON;

-- Check first. This LIKE is deliberately wider than the UPDATE's: it also shows a title with a
-- trailing space ('... | Spencer Denim ') or the old name somewhere other than the suffix, neither of
-- which the UPDATE below will touch. If this returns rows the UPDATE then leaves behind, they are
-- somebody's own wording and want a human, not a wider UPDATE.
SELECT 'Department' AS [Table], Id, SeoTitle FROM catalog.Departments WHERE SeoTitle LIKE '%Spencer Denim%'
UNION ALL SELECT 'Category', Id, SeoTitle FROM catalog.Categories WHERE SeoTitle LIKE '%Spencer Denim%'
UNION ALL SELECT 'Product',  Id, SeoTitle FROM catalog.Products   WHERE SeoTitle LIKE '%Spencer Denim%';

-- Then, in a transaction. The suffix-only LIKE is the whole safety: a title whose brand *suffix* an
-- admin has replaced is not touched. A title whose *subject* they rewrote still is: 'Ladies frocks |
-- Spencer Denim' becomes 'Ladies frocks | Colombo Wardrobe', which keeps their words and fixes the
-- brand, and is what you want.
BEGIN TRAN;
UPDATE catalog.Departments SET SeoTitle = LEFT(SeoTitle, LEN(SeoTitle) - LEN('| Spencer Denim')) + '| Colombo Wardrobe'
WHERE SeoTitle LIKE '%| Spencer Denim';
UPDATE catalog.Categories  SET SeoTitle = LEFT(SeoTitle, LEN(SeoTitle) - LEN('| Spencer Denim')) + '| Colombo Wardrobe'
WHERE SeoTitle LIKE '%| Spencer Denim';
UPDATE catalog.Products    SET SeoTitle = LEFT(SeoTitle, LEN(SeoTitle) - LEN('| Spencer Denim')) + '| Colombo Wardrobe'
WHERE SeoTitle LIKE '%| Spencer Denim';
-- COMMIT; (or ROLLBACK)
```

A fresh database (3 departments, 29 categories and, with the demo catalogue, 45 products) gets the new
titles from the seeder and needs none of this.

**The demo placeholder images are a separate matter, and are files rather than rows.** Their caption
(`Colombo Wardrobe — demo data, not a real product image`) is drawn into the JPEG, and
`DemoMediaPlaceholderGenerator` skips any asset whose original and renditions are already on the volume,
so an existing environment keeps the old caption until those files are deleted and the generator re-runs
(it rewrites each row's checksum, size and dimensions from the new bytes). This matters on a development
or staging machine and nowhere else: the demo placeholders are never seeded in production.

**Nothing else in the seeded data carries the name.** No home-page copy, announcement or newsletter row
does, and the alt text `ClientAssetImporter` writes already said "Colombo Wardrobe" before this rename.
That is a statement about what the seeders write, not a promise about what an admin has typed since —
which is what the wider `SELECT` above is for.


A seeded database holds 3 departments, 29 categories, 4 age groups, 19 attribute options,
14 colour families, 52 sizes, 3 size guides, 45 products, 104 colourways, 480 variants with stock,
149 media assets and 135 related-product links. Prices are LKR placeholders until the client sends
the real price list; images are placeholder storage keys (the upload pipeline is W1-04).

### 2b. The client's photographs, video and logo

The delivered assets are imported by a verb on the API, which never starts Kestrel and so binds no
port. It reuses the API's own composition root — the configured media volumes and their startup
validation, the real `IMediaService`, the database — and **every byte goes through the media
pipeline**, so each file gets the same signature check, EXIF stripping, WebP rendition set, checksum
deduplication and usage tracking as an admin upload.

```powershell
# What would happen, without storing anything: resolves every filename against the real catalogue
dotnet run --project src/backend/SpencerDenim.Api --no-launch-profile -- `
  import-client-assets --from <path-to-the-drop> --dry-run

# Do it
dotnet run --project src/backend/SpencerDenim.Api --no-launch-profile -- `
  import-client-assets --from <path-to-the-drop>

# The client sent a corrected photograph: overwrite the slot it belongs in
dotnet run --project src/backend/SpencerDenim.Api --no-launch-profile -- `
  import-client-assets --from <path-to-the-drop> --replace
```

The drop holds `logo/`, `products/`, `departments/`, `home/` and `social/`, named as the client
checklist describes (`<product-slug>-1.jpg` is the card image and `-2.jpg` the hover image;
`women|men|kids.jpg`; `hero.mp4` + `hero-poster.jpg`, `campaign.jpg`, `activewear.jpg`,
`trending-N.jpg`; `social-N.jpg`).

It is **safe to run again** — the second run stores nothing and writes no row — and it **fails
loudly**: the whole run is one transaction, so a refusal anywhere — or a Ctrl+C — rolls back every
row before it and deletes the files it had written, and the report says what to fix. Every filename
the importer had to repair is printed.

| Exit | Meaning |
|---|---|
| 0 | the drop landed |
| 1 | the command line was wrong |
| 2 | the import failed and was rolled back |
| 3 | it completed, but files were deliberately not applied (see below) |
| 4 | the operator interrupted it; everything was rolled back |

A slot that already holds **a picture somebody chose** is not overwritten: those files are listed
under *NOT APPLIED*, the command exits **3**, and `--replace` is what applies them. A slot holding
one of the demo placeholders is filled either way, because a placeholder was never a choice.

A drop that **stops sending** a file is a decision too. When a product's `-1` arrives without its
`-2`, the hover slot is emptied rather than left showing the photograph the client has withdrawn —
immediately if it holds a demo placeholder, and otherwise under *NOT APPLIED* as `<slug>-2` until
`--replace` is given, because the importer cannot tell its own earlier work from an upload somebody
made in ADM-11. If the withdrawn `-1` was itself a picture somebody chose, the whole correction
needs `--replace`: the run reports the card as a conflict and changes nothing, rather than moving
the card and leaving the hover behind.

Two files carrying **the same photograph** deduplicate to one asset in the library, and
`catalog.ProductMedia` holds one photograph per product once — so the second is reported under *ONE
PICTURE, TWO FILES* and shares the first one's gallery row. Both slots still point at it.

`--dry-run` resolves identity the same way the real run does (by SHA-256), so a plan and a run agree
about where every file lands. It cannot say whether a file would be *refused* — signature, size and
codec are only known by decoding it.

The five media-dependent homepage sections are switched on only once they have media to show — and
a picture that does not fit the slot its filename claims keeps its section off until a different
one is delivered (`ClientAssetHolds`). **Both holds from the first delivery were released in W1-14**
by decisions rather than by new files — the hero now carries a measured `object-position` and the
HOME-06 slot is relabelled "Linen and denim" to match the photograph — so that list is empty and
the mechanism is waiting for the next one.

A banner's **words** travel with its picture. The importer writes a heading, standfirst, button and
link for HOME-05 and HOME-06, and it may take back exactly the wording it wrote before and nothing
else: a line somebody typed in ADM-17 survives and the report says it did. That is what stops a
re-import switching a section on over copy that contradicts the picture, which is issue #63.

> **An interrupted run leaves one file set behind.** Ctrl+C rolls every row back and deletes the files
> of every upload that had finished, but the one that was still running when the interrupt arrived
> cannot be identified without sweeping the asset table — and an admin upload on another connection
> would be swept up with it, so it is left alone. At most one asset's worth, with no row pointing at
> it. The report says so.

> **Re-running repairs rows, not files.** The library deduplicates by checksum, so an import run
> against a database that still has its rows reports `[AlreadyPresent]` and writes nothing —
> including when the media volume underneath has been lost or wiped, in which case the storefront
> shows broken images and the import will not put them back. A lost volume needs a restore from
> backup, or the asset rows deleting first so the files are written again.

#### Kids stand-in photographs — TEST DATA, and still outstanding

**The client has not supplied photographs for the ten kids products, and they are still owed.** So
that the Kids section can be seen and tested, each kids product borrows the nearest delivered adult
garment's photograph (owner decision, 2026-10-02). Nothing about this is finished work: the client
progress page still says kids photographs are outstanding, and it is right.

```powershell
# Prints the mapping, builds the stand-in drop, and imports — the delivery first, then the stand-ins
./tools/kids-stand-in-photos.ps1 -ClientAssets <path-to-the-drop>

# What it would do, storing nothing
./tools/kids-stand-in-photos.ps1 -ClientAssets <path-to-the-drop> -DryRun
```

`-Out` defaults to the git-ignored `.local-kids-stand-ins/`. Whatever is at that path is **deleted and
rebuilt**, so the script refuses any path that is not either absent or a generated drop of this
mapping's own files — and refuses outright anything holding, held by or equal to `-ClientAssets`, which
is the only copy of the client's photographs and is in no repository.

A **relative** `-Out` is resolved against your shell's current location, which is where the delete would
land, and the guard is applied to that same resolved path. The two used to be resolved differently —
`[System.IO.Path]::GetFullPath` reads `[Environment]::CurrentDirectory`, which `Set-Location` does not
update — so after one `cd` the guard inspected a folder the delete was never going to touch.

**A first `-DryRun` cannot tell you the alt text, and says so.** Both steps are dry runs, so step 1
stores nothing for step 2 to find; with the donor's bytes still absent, the plan marks each line
`PROVISIONAL` rather than printing the kids product's own name as the sentence that would be written.
Run it again after the delivery is really imported and every line names the donor's sentence.

**Which kids product borrowed which photograph, and how good each match is, is in one file:
[`tools/kids-stand-in-photos.csv`](tools/kids-stand-in-photos.csv)**, with a reason per row. Two of
the ten are recorded as a bad match rather than dressed up as a good one — the baby bodysuit set
borrows a women's ribbed tee, and the muslin bib set has nothing close in the delivery at all and
borrows a canvas tote. `KidsStandInMappingTests` holds every slug in that file against the real
catalogue, so a renamed or an eleventh kids product fails the build instead of quietly keeping its
placeholder.

**It is one asset shared by two products, not a copy.** `UX_MediaAssets_Checksum_Ready` means the
same bytes cannot become a second asset however many filenames carry them, which is correct: the
outcome is one `media.MediaAssets` row with two `media.MediaUsages` rows, and the adult product keeps
its own card and hover untouched. Nothing is duplicated, no migration is involved, and the catalogue
is not changed.

**The order of the two imports is not a courtesy.** Alt text lives on the asset, so whichever import
creates it writes the sentence both products then read out. The script imports the client's delivery
first for that reason alone; run the stand-ins against a library that does not hold the donor's bytes
and the shared asset ends up carrying the *kids* product's name, which the women's or men's product
then reads out as well. `Importing_the_stand_ins_first_puts_the_kids_name_on_the_donors_photograph`
asserts that, so the day the importer stops depending on the order the test fails and the first step
can go.

**The alt text deliberately names the adult garment.** On the product page a girls' sundress reads
"Tiered Midi Dress, front view", because that is what is in the photograph. It is the only sentence
available — one asset, one sentence, shared — and writing "Girls Cotton Sundress" would be both false
and a regression on the women's product, which reads the same row.

**Disclosed on the product page, silent on the listing.** A product card's `<img>` carries a hard-coded
`alt=""` inside an `aria-hidden="true"` anchor (`product-card.ts`), because the photograph repeats the
link on the product name — the correct decorative-image treatment, and nothing to do with this change.
So the asset's sentence never reaches `/kids` at all: the borrowed garment is named on the product page
and nowhere on the listing.

`ProductMedia.AltTextOverride` is left null on purpose, and the reason is **not** that an override would
make the listing disagree — the listing says nothing, so it cannot. It is that `PlaceProductMedia` does
**not** clear an override when a photograph replaces the one before it, so an override written now would
outlive the stand-in and go on describing the wrong garment after the real photographs had landed. That
is exactly the archaeology this arrangement exists to avoid.

**When the real photographs arrive**, put them in the client's own drop as `<kids-slug>-1.jpg` and
`<kids-slug>-2.jpg` and re-import with `--replace`:

```powershell
dotnet run --project src/backend/SpencerDenim.Api --no-launch-profile -- `
  import-client-assets --from <path-to-the-drop> --replace
```

`--replace` is required and is the whole reversal. A stand-in is a picture somebody chose, so without
it the run lists the kids slots under *NOT APPLIED*, exits **3** and changes nothing a shopper sees —
**but the photographs themselves do enter the library.** The bytes go through the pipeline before the
slot is consulted, so a refused run leaves their assets and renditions stored and referenced by nothing:
measured at 2 assets and 8 files for the two kids photographs. Litter rather than loss, and nothing
purges it on its own; re-running with `--replace` then attaches the assets that are already there.

Afterwards, `./tools/kids-stand-in-photos.ps1 -Remove` deletes the generated drop, and the row for each
product that now has its own photograph comes out of the CSV. **Leaving a row in is safe, not
dangerous** — tidiness, not a guard. Re-run the script with the real photographs in place and the
importer refuses the slots it would overwrite and exits 3; the protection is the importer's conflict
rule, not the contents of the CSV.

### 3. Run the API

```powershell
dotnet watch --project src/backend/SpencerDenim.Api
```

It listens on http://localhost:5080:

| URL | What |
|---|---|
| `GET /api/ping` | Reachability check (no database) |
| `GET /health` | Liveness: the process answers |
| `GET /health/ready` | Readiness: SQL Server connectivity **and** whether doc 08 §9's reference data is established (503 until both hold — a connectable database with no migrations applied is not ready) |
| `/api/swagger` | Swagger UI, **Development only** (OpenAPI JSON at `/api/openapi/v1.json`) |
| `GET /api/catalog/departments` | Departments with their category tree (GLB-05, DEPT-01) |
| `GET /api/catalog/products` | Listing with filters, sort and paging (PLP-01..07, CARD-01) |
| `GET /api/catalog/products/{slug}` | Product page payload (PDP-01..06); 404 for an unknown slug |
| `GET /api/catalog/search?q=` | Search over the denormalised search text (SRCH-02) |

Listing parameters: `department`, `category` (includes the subtree), `ageGroup`, `minPrice`,
`maxPrice`, `size` (size label), `colour` (colour-family slug), `material`, `fit`, `occasion`,
`minRating`, `inStock`, `onSale`, `minDiscountPercent`, `newArrivals`, `sort`, `page`, `pageSize`.
The repeatable ones are repeated (`?size=M&size=L`). `sort` is one of `recommended`, `newest`,
`best-selling`, `price-asc`, `price-desc`, `rating`, `discount`. Invalid values return one 400 that
names every rejected parameter.

### 4. Run the storefront and admin (dev servers + proxy)

```powershell
cd src/frontend
npm ci
npx ng serve storefront   # http://localhost:4200
npx ng serve admin        # http://localhost:4300/admin/
```

`proxy.conf.json` forwards `/api` and `/health` to the API on http://localhost:5080, so start the API first. Each placeholder home page calls `/api/ping` and shows "API connected" or a neutral "not reachable" message. During server-side rendering the storefront calls the API directly at `API_INTERNAL_URL` (default `http://localhost:5080`).

**`proxy.conf.json` only applies to `ng serve`.** The built SSR server (`npm run serve:ssr:storefront`) serves the storefront and nothing else: it renders pages, it does **not** forward `/api`. That is the reverse proxy's job in production (doc 03 §4, Caddy), and it is why a built storefront run on its own answers a page fine but fails anything the browser fetches afterwards — "Load More", a filter change, the product search.

So:

| Running | `/api` reaches the API | Use it for |
|---|---|---|
| `npx ng serve storefront` (4200) | yes, via `proxy.conf.json` | day-to-day development, clicking through a page |
| `npm run serve:ssr:storefront` (4000) | **no** | checking the server-rendered HTML, the production bundle |
| behind Caddy / the reverse proxy | yes | staging and production |

`npx playwright test` needs neither: it starts the built SSR server *and* a stub catalogue
(`e2e/support/catalog-api-stub.mjs`), points the SSR process at it with `API_INTERNAL_URL`, and
routes the browser's own `/api` calls to the same stub — which is what the reverse proxy does.

### 5. Run the tests

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

### 6. Build and run the storefront SSR server locally

```powershell
cd src/frontend
npm run build:all                                   # dist/storefront (browser + server), dist/admin
$env:PORT = "4000"; $env:API_INTERNAL_URL = "http://localhost:5080"
node dist/storefront/server/server.mjs              # http://localhost:4000 (GET /healthz for liveness)
```

The server renders the page on each request, so the initial HTML already contains the "Colombo Wardrobe" heading and the API status. For a public host name, add it to `NG_ALLOWED_HOSTS` (comma-separated).

### CI

`.github/workflows/ci.yml` runs on every push and pull request: the backend job builds and tests against a `mcr.microsoft.com/mssql/server:2022-latest` service container; the frontend job runs `npm ci`, lint, headless unit tests and both production builds.

## Rules for anyone working on this project

1. Read `MEMORY.md` and `TASKS.md` first, every session.
2. Nothing is built that is not in [02-scope-baseline.md](docs/02-scope-baseline.md). New requests → logged as change requests (chargeable, per proposal §5).
3. Every task goes developer → tester; a failed test goes back to the developer until it passes ([06](docs/06-engineering-process.md)).
4. Every screen must work at 320 / 375 / 390 / 768 / 1024 / 1280 / 1440 / 1920 px.
5. Update `MEMORY.md` and `TASKS.md` at the end of every working session. Update the docs whenever a decision changes them.
