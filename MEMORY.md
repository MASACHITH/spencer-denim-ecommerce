# MEMORY — Project Log

Running record of what was done, what was decided, and what's next. **Newest entry on top.** Read this first every session.

---

## Current state (keep this section up to date)

- **Phase:** Week 1 — Foundation & admin catalogue. Week 0 setup complete (scaffold + CI committed 2026-09-17). **First feature slice (admin login/RBAC foundation, W1-01) DONE 2026-09-21** after 3 developer↔tester fix rounds (all Major, all found by the tester, all fixed and re-verified). First real application code and passing tests now exist in the repo.
- **Deadline:** Thu **15 Oct 2026** (one month from 15 Sep). Today: 21 Sep — Week 1 runs 19–25 Sep, day 3, one of seven Week 1 tasks landed.
- **Scope:** [docs/02-scope-baseline.md](docs/02-scope-baseline.md). Every demo feature is kept, only the theme changes. Nav: Women · Men · Kids · New Arrivals · Sale. Admin can manage everything, with per-user role permissions.
- **Theme:** Banana Republic–inspired, full replacement → [docs/04](docs/04-design-system.md)
- **Stack (approved):** .NET 10 API + Angular 21 (SSR storefront + admin app) + SQL Server Express, Docker, Caddy, GitHub Actions. Hostinger KVM 2, Azure-portable. Scaffold is committed (`src/backend`, `src/frontend`); Docker packaging split out to **W0-15**, still deferred.
- **Payments:** Bank transfer in week 3. **PayPal last** (week 4). A second gateway comes later (TBD) through `IPaymentProvider`.
- **Repo:** https://github.com/MASACHITH/spencer-denim-ecommerce (user's **personal** account MASACHITH, branch `main`). Local git identity is set **per repo, per machine** (MASACHITH / sachithishara1@gmail.com) — see [docs/14](docs/14-multi-session-coordination.md). `destinity-sachithm` is the user's **office** account. **Never push this project with it, and never switch the active `gh` account to it without asking the user.** Before every push, confirm `gh auth status` shows MASACHITH as active. If it doesn't, ask the user before switching.
- **Multiple sessions/machines:** More than one Claude Code session now works on this repo, sometimes on different machines. Handoff protocol, machine readiness check and the known-machines log: [docs/14-multi-session-coordination.md](docs/14-multi-session-coordination.md). Referenced from the root `CLAUDE.md`.
- **Reference archive:** `reference/` holds read-only copies (taken 2026-09-16) of the old demo (`tuasl-fashion-demo/`), the client documents (signed proposal + theme reference) and the original prototype prompt. See `reference/README.md`. The originals remain outside the repo.
- **Blockers:** W0-05 Docker/WSL still paused (blocks W0-15). This machine (new, 2026-09-21) has no `gh` CLI, so it can't push yet — see doc 11.
- **Next:** **W2-01 (homepage, HOME-01..10) PASSED the tester** and is pushed to `origin feature/W2-01-homepage` awaiting a PR: content schema, seeder, read-only content API, newsletter signup endpoint and the page itself. ADM-17's admin screens are **not** in it. Issue #37 (the homepage cache rounds schedule boundaries to its five-minute TTL) is filed, not built.
- **Next:** **W1-02 (catalogue foundation — schema + demo seeder + the read-only storefront catalogue API) is in the developer↔tester loop** on `feature/W1-02-catalogue-foundation` (worktree `worktrees/sd-catalog`): tester round 1 failed it on 3 major + 5 minor, round 2 on 2 major + 4 minor, all fixed; not pushed, not merged, not Done. Its ADM-11 admin screens are a separate task, **W1-12 (TODO)** — W1-02 no longer means "categories (create/edit/reorder)". W1-08 (Users & Roles CRUD), W1-09 (audit log viewer), W1-10 (2FA enrollment), W1-11 (route guard, SecurityStamp revalidation, forwarded-headers regression test) are the W1-01 follow-ups, tracked but not started; the user/architect should decide priority between those and W1-03 once W1-02 passes.
- **Client progress page:** `client-progress/` is tracked in this repository (moved 2026-09-22, W0-18) and publishes from whichever machine holds the DPAPI-encrypted FTP credential — the `SCIENTER\sachithi` PC, where it was published on 23 and 24 Sep. An earlier note here said it could not be done from "this machine"; that was written from the second machine and is not a property of the repository.
- **Next:** **W1-12 (ADM-11: the departments and categories admin screens) is a developer pass awaiting a tester** on `feature/W1-12-category-admin` (worktree `worktrees/sd-rename`): the admin read model, eight write endpoints with RBAC and audit rows, the Angular screens with a keyboard- and touch-first reorder, and one migration adding the slug CHECK constraints. Not pushed, not merged, not Done.
- **Next:** **W1-14 (hero framing, the activewear relabel, the poplin re-import) is a developer pass awaiting a tester** on `feature/W1-14-hero-and-activewear` (worktree `worktrees/sd-hero`): both W1-13 holds are released — the hero ships with a measured `object-position` and HOME-06 is relabelled "Linen and denim" over a listing that holds linen and denim — and a drop that withdraws a photograph now withdraws it from the product. Not pushed, not merged, not Done. **The hero video still wants re-exporting at 16:9 and ≥1920 px, with a poster cut from the same take**; what shipped is the best the 360x640 file can do.
- **Next:** **W1-13 (the client's real photographs, video and logo) is back with the tester after fix round 3** on `feature/W1-13-client-assets` (worktree `worktrees/sd-assets`): a repeatable import verb on the API, three of the five media-dependent homepage sections switched on, and a HOME-01/02/05/06 fix for blocks sized by `aspect-ratio`. **The hero and the activewear feature are held off by their artwork's checksum** — the delivered video is portrait in a 16:9 frame and the delivered activewear picture is a linen flat-lay — and each releases itself when the owner delivers a replacement. Not pushed, not merged, not Done. **The logo is imported but not wired in** — the three delivered files are opaque JPEGs named `.png`, the "light" wordmark is the same mockup as the dark one, and the mark reads COLOMBO WARDROBE rather than Spencer Denim. That needs a decision from the owner and flat artwork, not code.
- **Open assumptions:** A1 (Trending Categories / Best Sellers homepage blocks stay removed per signed proposal), A2 (video MP4 ≤ 50 MB, no server conversion).

---

## Key decisions log

| Date | Decision | Why | By |
|---|---|---|---|
| 2026-09-15 | Timeline one month: 15 Sep → 15 Oct 2026 (internal = client tracker) | Q1 | User |
| 2026-09-15 | Banana Republic theme, full replacement of Denim × Linen | Q2 | User |
| 2026-09-15 | PayPal implemented last; another gateway later (TBD); bank transfer kept | Q3 | User |
| 2026-09-15 | Keep **all** demo home/page features; only theme changes | Q4 | User |
| 2026-09-15 | Nav: Women · Men · Kids · New Arrivals · Sale | Q5 | User |
| 2026-09-15 | Admin manages everything (products, video, categories, content, settings) with role-based access per user | User request | User |
| 2026-09-15 | Q6–Q16 go with architect recommendations (see docs/01 §3) | User delegated | User |
| 2026-09-15 | Gaps found mid-build are discussed before building | Process | User |
| 2026-09-15 | Signed PDF proposal overrides earlier drafts | Latest agreed version | Team |
| 2026-09-15 | Client progress page is separate from codebase; shows features + progress only | Client asked for task sheet | User |
| 2026-09-15 | Every task: developer agent → tester agent → loop on failure; SOLID; Docker; 8 breakpoints | Standards | User |
| 2026-09-21 | This repo is worked on by multiple Claude Code sessions/machines under one shared git identity; adopt a handoff protocol | A second session/machine started working on the project | User |

---

## Session log

### 2026-10-02 (4) — five merges, and what the tester loop cost and bought

`main`: `de78aaf` → `ac22dda` (#30) → `fd96260` (W1-12) → `9fd4486` (W2-07) → `5b5d3f8` (Angular security) → `d0624d0` (W2-10). Client page 45% → **47%**; search 55% → 90%, category management done.

**Every one of the four features failed its first review, and in each case the finding was something a
user would have met.** A live-only category 404'd with `noindex` whenever the departments feed was
unhealthy (#30). Every category and department save silently wiped the client's images — and **1,571
passing tests missed it**, because every fixture set those columns to `null`, so `null → null`
round-tripped vacuously (W1-12). A second Escape emptied the search box and reopened the suggestions,
and the search history stored email addresses because the privacy rule was anchored to a term that was
*exactly* an address (W2-07). And `//robots.txt` — one extra slash — answered **500**, which tells a
crawler the entire site is disallowed (W2-10).

**The recurring defect is not in the code, it is in the instruments.** Three times this week a test
could not have failed: a selector that matched nothing while the helper returned `[]` for both "all
passed" and "nothing matched" (#80); a 5xx sweep that listed only the URL shapes its client was able to
send; and a C# docstring claiming to be the test that caught a TypeScript bug. The `//robots.txt` fix
then survived a whole round for the same reason — Playwright normalises dot segments client-side, so
`/.//robots.txt` arrived as the already-fixed `//robots.txt`. The answer was **Node's
`http.request({ path })`**, which puts the path on the wire byte for byte where `fetch` and undici will
not. Lesson, for the next person: before trusting a red-before observation, check the *client* can
express the thing being tested.

**Found by reading `npm audit` while adding a dependency, not by anything watching:**
`@angular/router` 21.0.0–21.2.23 carries an SSR denial of service via numeric URL matrix parameters
([GHSA-ff3f-86qr-9cv3](https://github.com/advisories/GHSA-ff3f-86qr-9cv3)) and we were on 21.2.23
exactly, on a server-rendered storefront with a public catch-all. Fixed to 21.2.25. **Nothing runs
`npm audit` in CI** (#84) — the same gap #65 recorded for axe-core, which `docs/06` had listed for
weeks without it being a dependency. Do not add `--audit-level=high` as a gate: the two remaining
criticals are build-time only and npm's proposed remedy is `@angular/build@19.2.27`, a downgrade
across two majors.

**Owner decisions, 2026-10-02.** Department name and slug are **not** editable (doc 11 P1 step 1) —
and the `CatalogSlug` follow-up W1-12 carried is satisfied without them, because `Department.Slug`'s
write path has been *the seeder* since W1-02, which is what the setter guard and `CK_Departments_Slug`
protect. Reordering is **buttons, not drag and drop** (doc 11 P1 step 4 now records it). And W2-09 goes
first on the `sales` schema, creating `Carts`/`CartItems` only, with Tharanga's #53 stacking on top
(coordination note on #53).

**Three features landed deliberately incomplete rather than faked**, which is worth defending: "popular
searches" has a specified source (`analytics.SearchQueriesDaily`, doc 08 §1.7) that is unbuilt, so the
overlay offers the real catalogue's entrances instead and two tests assert the word "popular" appears
nowhere (#78); GLB-10's content pages wait on the client's legal copy; and the bag's promo code and
delivery charge wait on ADM-13 and #33.

**Process:** this clarifies the `CLAUDE.md` vs doc 14 contradiction over `MEMORY.md`/`TASKS.md` — doc
14 wins, the orchestrating session does both once at the end, feature branches write only a journal.
W1-12's PR proved why: it edited both files, conflicted when `main` moved, and **GitHub does not run CI
on a conflicting PR**, so it sat looking as if it were waiting on a slow build. Also: agents inherit the
parent session's cwd rather than the worktree named in their brief, and must be given an absolute path.

**Flakes are now a pattern, not five bugs:** #66, #75, #88 and #89 (×2) — every one a timeout or a
state read under contention, **never a wrong value**. Worth settling before W4-04, when an unexplained
rate stops being an annoyance and starts hiding things.


### 2026-10-02 (3) — W1-12 fix round 2: the discard was right, the silence was not

The tester passed everything except one major, having re-introduced the blocker itself to watch the two
frontend tests fail, enumerated both request DTOs against the rendered controls, failed to defeat
`CheckBannerLink` v2 with 60 inputs, and reproduced the N+1 figure character for character. Eight of the
nine remaining items were corrections to comments and claims rather than code.

**MAJOR: the category tree threw away a staged reorder in silence, and round 1 had made the two halves of
the screen contradict each other.** Stage a move, hide an unrelated category, and the banner and the Save
order button vanished while the live region said only "Traditional wear was updated". The round-1 report
said the tree had already been fixed and only the department list had missed out; that was wrong, and the
tester owned it - the tree had always discarded deliberately, and nobody had looked at that half.

**The owner's decision, and it is the right one: keep the discard, make it visible.** The department list
can reapply by id because three fixed rows cannot gain or lose a member; a category reload genuinely can,
so reapplying a stale arrangement would send a list the server refuses with `stale_order`. The defect was
only the silence.

**So "discarded" is now a value the screen can read rather than an absence.** The staged order is stored
with the tree it was made from (`StagedOrder { madeFrom, nodes }`), so "nothing is staged" and "something
was staged and no longer applies" are different states. A `role="status"` notice says what happened, why,
that nothing else was lost and what to do, with a Dismiss button; the live region says the same words,
because the announcement is a **computed** that prefers the discard message over the last action - the
admin already knows about the change they asked for. One string feeds both, so they cannot drift.

**m3: two pieces of copy promised behaviour that does not exist.** The size-guide null option read "Inherit
from the nearest parent" and nothing in the codebase walks ancestors; the age-group hint read "Required for
Kids categories" and nothing requires it. Both now say what is true.

**m4, and this one was the coordinator's error rather than mine, which is worth recording because the wrong
claim had already reached three files.** A category without a size guide does **not** break PDP-04.
`ProductCatalogReader` reads `Products.SizeGuideId` and only that; the single place `Category.SizeGuideId`
reaches a product is `DemoCatalogueSeeder`, which copies it at creation. Doc 08 3.2's "inherited from the
nearest ancestor" is a design note, not built behaviour. So the field is the default W1-03's product form
will inherit, and it reaches no storefront page today. Building the pickers was still right (doc 11 P1
step 2); the description was not.

**m1: the credit for catching the blocker was in the wrong file.** `AdminCatalogWriteShapeTests` cannot
catch it - its key-set assertion compares two hand-written C# lists and no C# test can see TypeScript. The
two frontend tests catch it. The integration suite keeps its real value, which is proving the API stores
everything it is handed, and now says so.

**m2: "a derived port cannot collide by accident" was false** - 60 slots is about 1 in 60 per pair and
roughly 1 in 5 that some pair of seven worktrees collides, at which point `reuseExistingServer` reproduces
the 113-failure bug silently. The wording is corrected, and the suite no longer relies on the arithmetic: a
`beforeAll` asks the server which worktree's build it is serving and refuses to trust it otherwise.

**m5: the documented picker fallback did not exist.** A failed options request left a select showing nothing
for a category that does have an age group - misreporting the row, and the next save would write what it
showed. The row's own value is now kept in the list, labelled as a stand-in.

**m6: the two "strictness" e2e tests proved only that the stub refuses**, because they issued a raw `fetch`
and bypassed the client entirely - exactly what their own comment claimed they prevented. They now
intercept the screen's own request and take the header or the row version away from it, so the client
building it, the server refusing it and the screen saying so are all in the test. **Proven by mutation**:
sending the token under the wrong header name fails them with `Received: undefined`.

**m7** is a note rather than a fix (the stub and `AdminWriteRequest` read a doubly quoted `If-Match`
differently, and nothing sends one), and **m8** folds the permission sweep's ad-hoc bodies into the one
`BodyFor`, so the sweep has a single body to keep current.

**Numbers:** backend **887 unit + 456 integration** (unchanged), 0 skipped; frontend 21 / 460 / **212**
admin; **Playwright 256**. Lint clean, Release build clean with 0 warnings. Six full Playwright runs: five
clean, one failure of `storefront-shell`'s reduced-motion drawer - **issue #66**, on a file this branch does
not touch.

**Next:** back to the tester.

### 2026-10-02 (2) — W1-12 fix round 1: every save was quietly clearing the images it could not show

The tester failed it on 1 blocker, 3 majors and 9 minors, and confirmed the backend use cases and the
migration — 45 inputs against the CHECK constraints including an emoji surrogate pair, 60/60 across 15 runs
of the concurrency tests, the `kids/girls` stranding exactly as described. All fixed on
`feature/W1-12-category-admin`.

**BLOCKER, and the lesson is about which side of a contract gets a test.** `CategoryFormValues` and
`DepartmentFormValues` simply did not list the image columns, the services spread those objects into the
body, the request DTOs bind an absent field to `null`, and the handlers assign it. So every category save
cleared its tile and banner images and every department save cleared its hero image — **and the audit row
recorded the staff member doing it**, which is the part that would have been unexplainable months later.

**Four suites and 1,571 tests missed it for one reason: every fixture set all three columns to `null`.** A
dropped field then round-trips null to null and no assertion can see it. The fix is two lines in a model;
the finding is the missing test. I had built `AdminCatalogPayloadShapeTests` for the **read** payloads
precisely because the screen's TypeScript interfaces are a separate hand-written copy — and then left the
write side, which has exactly the same hazard, unpinned. There is now
`AdminCatalogWriteShapeTests`, whose fixtures are deliberately non-null on every nullable column, plus two
frontend tests that assert the outgoing `PUT` body. **Watched red first**: `expected undefined to be 41`.

**The owner settled the department question, and the tester demolished the reading we were both working
from.** Name and slug are **not** editable (doc 11 P1 step 1 wins). The W1-02 follow-up it appeared to serve
needs no editable field: **`Department.Slug` has had a write path since W1-02 — the seeder** — so the setter
guard and `CK_Departments_Slug` already guard the path that exists, which is the argument
`CatalogSlugConstraint`'s own comment was already making. Both fields are gone from the command, the
request DTO, the handler, the form and the screen; the two now-dead store members are gone rather than left
as a port promising a capability the API does not have. `UX_Departments_Name` and `UX_Departments_Slug`
stay: they are the seeder's guard.

**MAJOR: `load()` threw away a staged department reorder without a word.** Stage a move, press any
Hide/Show, and the order reverted with no message. `refreshDepartmentCounts()` already guarded that exact
hazard, with a comment explaining why — the guard was in one of the two places that needed it. It is the
sibling of the defect I found and fixed for the category tree; the department list did not get the fix. The
staged value is now a list of **ids**, not of rows, so a reload both keeps the arrangement and refreshes the
counts under it — holding rows did neither.

**MAJOR: the banner-link rule was defeated three ways, all starting with a single slash.** `/\evil.example`,
`/<TAB>/evil.example` and `/<CR><LF>//evil.example` all resolve to `https://evil.example/`: the URL standard
treats a reverse solidus as a solidus for special schemes, and a browser strips TAB, CR and LF before
parsing. `Clean()` only trims the ends. The rule now refuses backslashes and C0 controls outright and then
**resolves the value against a base and requires the origin to be unchanged** — the question a browser
would ask, rather than reading the first two characters. The `javascript:`/`data:` half held up under
attack and its tests are kept.

**MAJOR: the e2e stub was more polite than the real API in the two ways that mattered**, under a header
claiming it was not. It never checked `X-XSRF-TOKEN` and ignored row versions entirely, so if the screen had
stopped sending either, all 246 browser tests would still have passed. It enforces both now, moves a row
version on every successful write, and — the real gap — **implements `parentCategoryId` on
`PUT /categories/{id}`, so re-parenting, the largest thing this screen does, has browser coverage at all.**

**m10: the age-group and size-guide pickers are built**, and the deferral was wrong. The image pickers wait
on ADM-18's media library; an age group is four seeded rows and a size guide is three, and what was missing
was a `SELECT`. Without them a Kids category made here could never be given either — and a new category
silently got no size guide, breaking PDP-04 for every product in it with no override.

**The rest:** a keyboard-reachability assertion that was true on a blank page is gone (`document.body`
contains `document.body`); "one round trip" was wrong and is now the measured two queries and five commands,
with `AdminCatalogQueryPlanTests` to catch an N+1 creeping back — **proven able to see one**, 135 commands
for 130 categories; the reorder routes' concurrency story is said accurately (the set check plus EF's own
predicate, no `CheckRowVersion`); the "every panel open at once" worst case was unreachable and is replaced
by the two that are; the antiforgery check covers all 8 write routes and the permission matrix now runs both
directions; the admin e2e port is derived from the worktree path, which is the mechanism behind this
branch's own 113-failure incident; and both defensive cases have answers — an orphaned category is drawn at
the top level rather than vanishing, and the stale path on a soft-deleted descendant is written down as the
restore task's problem rather than left silent.

**Numbers:** backend **887 unit** (was 875) **+ 456 integration** (447), 0 skipped; frontend 21 / 460 / **209** admin;
**Playwright 255** (was 246). Lint clean, Release build clean with 0 warnings.

**One flake of my own, found rather than tolerated.** The admin "nothing is logged to the console" test
navigated a second time while the page `beforeEach` had opened was still loading, so its own cancelled
requests arrived as failures — about one full-suite run in five, passing every time in isolation. It waits
for the first load to settle and ignores `net::ERR_ABORTED` now, with the reason written down; three
consecutive full runs are clean.

**Next:** back to the tester.

### 2026-10-02 (1) — W1-12 (ADM-11): the admin owns the catalogue structure, and the switch says what it will cost

`feature/W1-12-category-admin` (worktree `worktrees/sd-rename`). Developer pass only: not pushed, not
merged, **not Done**, and `client-progress/progress-data.js` is untouched until the tester passes it.

**The read side had to be a second reader, not a flag.** `ProductCatalogReader` and
`DepartmentCatalogReader` drop every row an admin has switched off — that *is* doc 08 §7.1's switch — so a
screen built on them could show only the rows already live and could never switch one back on.
`IAdminCatalogReader` answers the other question about the same tables, with the counts the screens need
and a base64 `rowVersion` per row.

**The deactivation warning is three numbers, and the third is the one nobody predicts.** `IsActive` is per
row and the listing checks only a product's **own primary category**, so hiding a *parent* does not take
its children's products out of the shop — it takes every menu route to them away and leaves them on sale
with no way to reach them. The impact payload says all three (leaving, descendant categories, stranded
products) and names up to five products; the screen fetches it **before** offering the button. Proved end
to end against a real database and the real storefront API, not by asserting a flag was set.

**Reorder is a whole list, and that is what makes two admins safe.** `PUT …/categories/order` takes every
sibling id in order and refuses a list that is not exactly the sibling set (`stale_order`, 409) — a subset
would silently leave out a sibling somebody else had just created. Every row carries `RowVersion`, so the
second of two concurrent saves loses with `stale_row_version` rather than interleaving. The integration
test fires both at once and asserts the result is **exactly one of the two requested orders, never a mix**.

**On screen it is buttons, not drag-and-drop**: Move up / Move down, 44 px, usable by tap, click,
Tab + Enter and Alt + arrow from either move button; moves are staged and one Save order sends each changed
sibling list as its own request and its own audit row. Dragging needs a pointer and would have to be built
twice to be usable at all.

**Moving to another parent is the edit form's parent field**, and it re-paths the whole subtree in one unit
of work (doc 11 P1 step 4, doc 08 §10.1). Refused for a loop, for another department, and for a move that
would push the deepest descendant past three levels — the guard measures the *subtree*, not the category,
which is the case a naive check gets wrong.

**Issue #59 is answered for this module.** `IAuditLogWriter.Enlist` adds the row to the caller's own unit of
work, so the trail and the change commit together: a refused write leaves no row, and a save of an unedited
form writes neither a row nor a statement (the row version does not even move). Create / Update /
StatusChange / SoftDelete, changed properties only; a reorder is **one** row naming the list
(`EntityType = CategoryOrder`, `EntityId = "{department}/{parent|root}"`) rather than one per category.

**Issue #24's answer for two more columns.** `CatalogSlug` is now checked in the command — a bad slug is a
400 naming the field, not an `ArgumentException` out of a property setter — *and* the setter guard was added
to `Category.Slug` and `Department.Slug`, which had none because neither had a write path before.

**One migration, `CatalogSlugWriteGuards`:** `CK_Categories_Slug` and `CK_Departments_Slug`, the slug rule in
SQL. **It needs a binary collation and that is the whole trick** — the database is `CI_AS`, so
`LIKE '%[^a-z0-9-]%'` accepts `DENIM`, and a case-sensitive collation is not enough either because `CS_AS`
interleaves the cases so `A`–`Z` still fall inside the range `a-z`. Only `Latin1_General_100_BIN2` gives the
ranges their ASCII meaning. Verified on 21 inputs, and applied to a database migrated to the previous
migration and then seeded: 3 departments and 29 categories pass, no `WITH NOCHECK`.

**Two defects the browser found that jsdom could not.** The six-button row was `flex: 0 0 auto`, so its own
`flex-wrap` never applied and it ran **489 px wide inside a 320 px page** at 320/375/390. And focus
restoration after a move ran on a microtask: `@for` tracks by id, so a move *relocates* the row's DOM node,
and re-inserting a focused element blurs it in Chromium — the unit test passed because it calls
`detectChanges()` synchronously first. It is an `afterEveryRender` hook now, preferring the control the
admin actually pressed.

**And one the test found by being written:** the staged order survived a fresh tree arriving from the
server, so after a create or a removal the screen would draw nodes that no longer existed and then send a
list the API would rightly refuse. It is a `linkedSignal` on the tree input now.

**A flake that was mine and was not the code.** Three admin e2e runs failed on random tests until I noticed
a **stale stub process from earlier in the session** that `reuseExistingServer` was happily reusing —
docs/14's "never run a server you did not start this session" in a new shape. The stub now keys its state on
a per-worker cookie, and three consecutive parallel runs are clean.

**Numbers:** backend **875 unit** (was 772) **+ 447 integration** (401), 0 skipped; frontend 21 shared /
451 storefront / **203 admin** (131); **Playwright 246** (213). Lint clean, Release build clean.

**Checked against the real stack**, not only the stub: the real API on LocalDB with the demo catalogue, the
built admin app behind a session proxy, signed in as a catalogue-only staff user. Create → the category is
in the storefront navigation; reorder → the mega menu follows; hide Skirts → the listing goes from 2 to 0
and the category leaves the menu; show → back to 2; remove with products → refused with the count; remove
empty → gone; audit rows name the user and the address; no console errors, no 5xx, no horizontal scroll at
any of the eight widths.

**For the owner, not fixed here:** doc 11 P1 step 1 says staff may edit "their description, hero image, sort
order and SEO" of a department, but W1-12 was asked to put `CatalogSlug` on `Department.Slug`'s **write
path**, which means there has to be one. The edit therefore includes the name and the slug, with a warning
on the slug field that old links stop working. If the owner wants departments' names and slugs locked, say
so and the two fields come out.

**Next:** the tester.

### 2026-10-01 (9) — issue #30: the storefront stops calling a page gone when it could not find out

**The defect, as the W2-03 tester measured it.** The route guards accept only slugs the catalogue tree
knows. When `GET /api/catalog/departments` is unhealthy that tree is `core/navigation/navigation.data.ts`,
a frozen copy of the seed, and a category that exists live and not in that copy answered **404 with
`<meta name="robots" content="noindex">`** — under a 500, a destroyed connection, a 12 s answer past the
2 s timeout, and a `200 []`. Harmless only because nothing can add a category yet; W1-12 (ADM-11) is in
flight in `worktrees/sd-rename` right now, and it makes every admin-added category one API blip away from
being dropped from Google.

**The fix is a third answer, named in the code.** `tree$` now carries `CatalogTree { departments, source }`
and the guards answer `known` / `missing` / `unconfirmed`:

- `known` — the tree in hand has the slugs. Listing, as before.
- `missing` — the **live** tree was in hand and does not have them. A real 404 with `noindex`
  (`isMissingCategory`, which replaces `isKnownDepartment` on the not-found routes — that is the actual
  bug: the old guard was allowed to say "gone" having read the frozen copy).
- `unconfirmed` — the tree was the fallback, so the catalogue was never asked. A new
  `CatalogUnavailablePage` answers **503 with `Retry-After: 120`** and **no `noindex`**
  (`isUnconfirmedCatalogue`). A crawler retries a 503 and keeps the URL; it drops a 404.

`unknown(tree)` is one line and it is the whole fix: a live tree that lacks a slug has settled the
question, the fallback was never asked it. The two 503s in the storefront (this and the product page's)
now share one `serviceUnavailable()` and one `RETRY_AFTER_SECONDS`, and **both remove a `robots` tag
rather than setting one** — the product page used to set `noindex` on its 503, which is the same mistake
one page over. `RobotsMeta` is the one place that knows the word, shaped like `CanonicalLink`, and
`NotFoundPage` now clears on destroy: it was the only setter without a matching clear, so "Back to the
home page" from a 404 left the home page carrying `noindex`.

**`Retry-After: 120`, and what it is based on.** Nothing in the storefront caches a failure — a root
injector is per SSR request, so the next render re-asks the feed, and each attempt is bounded by its own
timeout — so there is no backoff or TTL to read a figure off. It says how long it is worth *not* asking:
an API container restart, or a loaded VPS. Same figure as the product page, so a crawler meets one value
across an outage. The tester examined this and agreed, including that it is correctly absent on a 404.

**Browser half.** A 503 exists only during SSR. On a client-side navigation the page itself carries it:
it says what happened, keeps the header and footer, lists the seeded departments and offers "Try again",
which re-requests the address (`PageReload`) rather than repainting over a response that still says 503.

**Only a tree the render would route on is handed to the browser, and the operator order is the point.**
`tap(give)` sat above `timeout` and above the vetting, so a `200 []` was handed over before anything
judged it; the browser found a non-null value in `TransferState` and **made no request at all**, so the
page stayed on the 503 for the rest of the session. A 500, a destroyed connection and a timeout all
recovered on hydration, because those hand over nothing. Measured both ways per mode: with `tap` above,
`empty` and `garbage` keep `"catalog:departments"` in `#ng-state` and never reach "Coats" while `status`
passes; with it below and guarded on `source === 'live'`, all five recover.

**Tester round 1 — three Majors, six Minors, and the two design corrections worth remembering.**

1. **The single-segment 503 route is gone, and with it a deny-list that could never be right.** The first
   version also answered 503 for `/:department`, guarded by a hand-written `NON_CATALOGUE_PATHS` plus a
   slug-shape rule to keep `/robots.txt` out of it (a 5xx on `robots.txt` disallows the whole site). The
   tester measured 25+ addresses that 503'd where `main` answers 302 — `/checkout`, `/login`, `/en`,
   `/wp-admin`, `/assets/x` — and the decisive point: a deny-list protects what exists today and nothing
   added tomorrow. **A department-level 503 wants an allow-list and belongs in W1-12**, where the thing
   that creates departments lives. Recorded, not built.
2. **The 503 route now requires a department the tree already holds** — the same allow-list shape, one
   level down, derived from the catalogue so there is nothing to maintain. Dropping the single-segment
   route alone was *not* sufficient, and that is worth stating: `canMatch` on `:department/:category`
   matches any two segments, so measured on the unguarded build `/api/catalog/departments`,
   `/api/catalog/products`, `/en/women`, `/si/women`, `/assets/x`, `/static/x`, `/account/orders`,
   `/bag/checkout` and `/wp-admin/setup-config.php` **all answered 503**. `/api/...` is the sharp one:
   `server.ts` does not proxy `/api` (Caddy does), so a direct container hit or an uptime monitor would
   have been handed a 503 HTML page by the storefront. Nine of those are now regression tests.
3. **The new page's own links were 20 px tap targets and the assertion was scoped so it could not see
   them.** `undersized(page, '.unavailable__actions .btn')` measured the two buttons; the five department
   links were `A.link` at 57x20, 33x20, 33x20, 96x20 and 32x20. The selector is now
   `.unavailable a, .unavailable button` and the links take the footer's shape
   (`min-height: var(--tap)` on a flex box). Exactly MEMORY's own lesson: an assertion that asserts what
   the component does cannot report what the component got wrong.

**A shared-machine hazard, measured rather than theorised.** This file's pair was first written on 4430.
Another session then started the **admin** application on 4430; its dev server answered `/healthz` with
200, Playwright's `reuseExistingServer` reused it, and the suite ran against the admin panel — the
touch-target test failed reporting "Try again" missing from a page that was never the storefront. The
pair moved to **4432/4433** and is the only one in the config with `reuseExistingServer: false`, so a
stranger on the port is a bind error naming it rather than a silent pass. **The other five pairs still
carry `reuseExistingServer: !process.env['CI']` and have the same hazard** (the config holds twelve
`webServer` entries, so six pairs; ten of the twelve entries keep `!CI`). Doc 14 already says a port
that answers is not proof that it is yours; this is what it looks like.

**Numbers:** frontend 21 / **502** (+42) / 131; Playwright **256** (+43); backend **772 unit**, 0
warnings. Lint clean. Backend untouched. Rebased onto `origin/main` (`de78aaf`).

**Pre-existing, not mine, and it fails `dotnet test`** (filed as #74): three `ClientAssetImportTests`
never call `sqlServer.SkipIfUnavailable()`, so without `TEST_SQL_CONNECTION` they open
`new SqlConnection("")` — the machine's **default** SQL Server instance, which doc 06 explicitly forbids
pointing tests at. One line each; left alone because it is W1-13's test file.

**Also found, deliberately unchanged:** there is no `robots.txt` and no `sitemap.xml` in the storefront at
all (GLB-11 territory); `**` still redirects an unknown single segment home on a *live* tree, which is
#28; the homepage hero-muted flake is #75; and the storefront's heading order (`H3 H3 H3 H2 H1`, from the
mega-menu markup, identical on the home and 404 pages) is #76.

### 2026-10-01 (8) — W1-14 fix round 1: the guard the whole hero decision rests on could not fail

The tester re-derived the arithmetic independently and matched it (31.64% at 768–1440, **25.78%** at
1920, safe interval **21.83%–33.69%**), confirmed all three defect fixes end to end on a real database
with the real client files, and checked the destination really returns the five products. One major,
six smaller findings, all fixed.

**MAJOR, and it is the W1-13 lesson landing on me: the framing test ran at a 940 px viewport, and at
1920 the hero is 880 px tall starting 118 px down the page.** 58 px of it were off the screen, so the
*lower* assertion could not fail at the one width that constrains the lower edge. He proved it:
`object-position: center 20%` cuts her chin off at 1920 and **all five tests passed**. The suite was
pinning 15.2%–33.7%, not the 21.8%–33.7% my own comment claimed.

**And the obvious guard for it is itself dead, which is the part worth writing down.** Asserting that
the capture is as tall as the element — the first fix I wrote, and what I was asked for — can never
fail: Playwright does **not** return a short image for an element hanging below the viewport, it
returns the element's full height with the off-screen rows **blank**. Measured at 1920: box 1920x880 at
y=118, viewport 940, capture **880 px**, marker's last row **821** instead of 879. A blank row reads
exactly like "not the marker", which reads exactly like "the band stops before the bottom edge". So I
wrote the assertion, watched it pass at 940 where the bug lives, and only then replaced it with the one
that works: `markerBand` refuses to measure an element that does not fit on the screen, which is the
same question `edgesWithoutRing` asks. **Two dead assertions in two rounds, both of the shape "I
checked the thing next to the thing".** Verified in both directions now — 20% fails at 1920 at the new
height, 940 raises the guard rather than passing, 33% passes and 34% fails.

**The comment was wrong twice over, and both corrections are facts the next person needs.** The upper
bound is 33.69%, not "34%" — 34% fails the test. **And in round 2 the same trap turned up mirrored at
the other end:** 22% fails too, because the e2e marker is rows 160–269 of 640 = 25.00%–42.19%, a shade
taller than the 25%–42% her face occupies, so the test is stricter at the bottom than the arithmetic.
Both numbers are written down now — **21.8%–33.7% on paper, 23%–33% as the test measures it** — because
"21.8%" plus a developer rounding outward is a red build with no explanation in the comment. And "no garment is visible above 1024 px" is false:
the crop is identical in source terms from 768 to 1440 because `max-height` does not bite until
1564 px, and the collar and shoulders are in frame at all of them. Only **1920** is garment-free. The
sentence contradicted my own two lines earlier, which is how it got past me. Corrected in the
stylesheet, MEMORY, TASKS and the journal.

**The value also runs out, and nothing said so.** The clamped box keeps widening past 1564 px and the
surviving band keeps shrinking: measured, 30% holds to about **2,560 px** of hero width, has failed by
2,890, and past about **2,900 px** the band (17.1% and falling) is narrower than her face (17.0%), so
**no** value of `object-position` fits — 3440 x 1440 is an ordinary desktop. One more line on the
re-export bill.

**`BannerCopy.Supersedes` leaked across fields.** The four retracted strings were one flat set tested
against every field, so an admin who typed "Activewear" as the *button label* — an ordinary thing to do
in the activewear slot — had it silently overwritten by a retraction that was only ever about the
heading. Silently, because the write looked like a retraction and so did not even earn the "somebody
wrote this" note. The field is part of the key now (`"ButtonLabel\0Shop activewear"`), with a test that
is red on the flat set.

**The e2e stub still carried the old HOME-06 copy**, so the only suite that draws this block in a
browser was drawing the exact picture/words/destination contradiction issue #63 is about — and the
repeated-`material` link had never been through `toLink` → `routerLink` → a listing anywhere. The stub
carries the real copy and the real link now, and a new test clicks the banner and asserts all three
material chips arrive, which is the thing `toLink`'s query handling exists for.

**A possible orphan gallery row, found by reading rather than by running.** When the hover row holds the
card's own asset *and* the card slot is taken by another picture, the row can neither move nor go —
`galleryFor` draws every non-Video row, so deleting it would take the card's photograph out of the
gallery, which is the defect this round already fixed in a third shape. It is left in place and a note
says so; an `else Remove` would have been the quick answer and the wrong one.

**The relabel is in the scope baseline now**, as a dated decision in a new *Decisions after baselining*
section referencing issue #63, so doc 02 does not read as if HOME-06 drifted by accident.

**Numbers:** backend **736 unit + 374 integration** (was 736 + 373), 0 skipped; frontend 19 / 451 / 131;
**Playwright 200** (was 199). Lint clean, Release build clean with 0 warnings. Re-verified in the
browser against a rebuilt database and the real drop: no console errors, no failed requests, no
horizontal scroll, still-not-video on phones and under reduced motion.

**A pre-existing Playwright flake, measured rather than blamed on this branch.** `storefront-shell`'s
*reduced motion → mobile navigation drawer* tests fail about **one full run in five**, on
`#storefront-menu` resolving but staying `hidden` past the 5 s timeout. Because the suspect was my own
change — the hero sample is a different video now, and a looping video in a neighbouring worker is
exactly what the stub's own comment warns about — I measured it instead of assuming: 1 failure in 5
full runs here, and **1 in 6 on a worktree built from `f78663d` with none of this branch's changes**,
the same test with the same message. In isolation it passes 10 of 10, so it is contention under four
workers. Not mine, not fixed here, and worth filing beside the missing `axe-core` run.

**Two things that are the owner's, not mine, and deliberately not "fixed":** the tester would serve the
still at every width and hold the video until the 16:9 export arrives — same facts, opposite conclusion,
and it touches docs/04 §4 so it is the client's call. And the denim half of the HOME-06 photograph is a
denim **jacket**, which the catalogue has no women's equivalent of; three of the five products behind
the link are denim, so a shopper arriving from a linen-led picture meets a mostly-denim list. Honest,
and thin. Worth a line to the client.

**Next:** back to the tester.

### 2026-10-01 (7) — W1-14: the two held-back sections go live, by decision rather than by new files

Three owner decisions on top of W1-13, on `feature/W1-14-hero-and-activewear` (worktree
`worktrees/sd-hero`). Not pushed, no PR.

**The hero is usable from the file we have, and the honest part is how much that is worth.**
`object-position: center 30%` on `.hero__video`. The arithmetic is worth writing down because the
value is not a taste judgement: a 360x640 source in a 16:9 frame overflows only vertically, and the
band that survives `cover` is 31.6% of the source height below 1565 px and **25.8% at 1920**, where
`max-height: 880px` makes the box 2.18:1. Her face runs from 25% to 42% of the source height — 17
points against a 25.8-point band — so 1920 is the width that pins the value and the safe interval is
22%–34% at every breakpoint at once. 30% is inside it with headroom above her hair at 1920 and still
leaves the shirt collar in frame at 768. Checked by eye at all five widths and at t = 0, 5 and 9.8 s;
she barely moves across the clip, which is what makes a single value possible at all.

**And it is soft.** 1920 px of frame from 360 px of source is a 5.3x upscale — the skin and hair carry
no texture at 1440 or 1920, though the source's own haze hides it better than a sharp file would. More
to the point, **at 1920 no garment is in frame at all**: the clamp takes the collar and shoulders that
are there at 768 through 1440, and the hero reads "Linen, denim and everyday pieces" over a face. And
the value runs out above this site's breakpoints: 30% holds to about 2,560 px of hero width, has failed
by 2,890, and past about 2,900 the surviving band is narrower than her face, so **no** value fits —
3440 x 1440 is an ordinary desktop. I would ship this and keep asking for the 16:9 export; it is
better than a headless chest and worse than a hero.

**The poster stays as delivered, and it is not a frame of the video.** `hero-poster.jpg` is 1376x768,
the same model in a different take and a *different outfit*, and it is the better picture — correctly
shaped for the 16:9 frame and good at 4:5 on a phone. Inventing a frame from the portrait video would
be worse at every width. The cost is real and should be said to the owner: the `poster` attribute
paints first, so a desktop visitor sees the beige co-ord and then watches it become the cream shirt,
and a phone or a reduced-motion visitor never sees the video's outfit at all. One take, two garments.

**HOME-06 is relabelled rather than waiting for an activewear photograph** — "Linen and denim",
"Breathable linen for the heat, washed indigo for everything else", *Shop linen and denim*, opening
`/women?material=linen&material=linen-blend&material=denim`. Five products: the relaxed linen-blend
shirt and the linen drawstring trousers (which is the white co-ord in the picture), both jeans and the
denim wrap skirt. A department page would have "included" linen; this is a listing *of* it. The copy
names **fabrics, not co-ords**, because the catalogue sells separates and a flat-lay of two matching
sets would otherwise promise something nothing in the shop can honour — issue #63 one step further on.

**The trap had a second half that `??=` was hiding.** The old wording is already in the database on any
machine that ran W1-13's import, and the importer wrote banner copy with `??=`, so a re-import would
have switched the section **on** over "Activewear … Pieces that move with you" pointing at
`/women/activewear`. Copy now travels with its picture: the importer may take back exactly the four
strings it wrote before (`BannerCopy.Supersedes`, keyed on the literal values for the same
self-expiring reason the holds are keyed on a checksum) and nothing else — a headline somebody typed in
ADM-17 survives and the report says so.

**Both holds are released; `ClientAssetHolds` is empty and intact.** The record, the injection and the
tests stay, and the shipped list is now a comment explaining why each entry went.

**A drop that stops sending a file now stops the site showing it.** The poplin correction found it: the
client moved the right photograph to `-1` and withdrew the wrong one, and the hover slot kept the
withdrawn picture — pointer and gallery row — because the import only ever visits slots a file arrives
for. The code *looked* handled; it only handled the placeholder case. It is cleared under `--replace`
and reported as a conflict named `<slug>-2` until then, because an admin's own upload cannot be told
apart from the import's earlier work. **And the first attempt at that fix deleted the client's only
photograph out of the gallery**: the promoted picture was sitting in the hover row (one asset is one
row), so emptying that row removed it — card 191 above three demo placeholders, measured on the real
drop. It moves into the card slot the same run has just vacated instead.

**What the re-import actually did to the poplin shirt**, against a database rebuilt to the pre-
correction state: the plain run reported the card as *NOT APPLIED* and changed nothing — right, because
a card that moved while the hover did not is the same contradiction in a new place. `--replace`: card =
the shirt, hover = none, one gallery row at the card slot, the withdrawn picture with no product usage
left. The product renders with a single image.

**The test that mattered.** A test asserting `object-position: center 30%` would have been worthless,
so the e2e sample video is now 360x640 like the delivered one, with a magenta band at exactly 25%–42%
of its height standing in for her face. The suite **photographs the hero** at 768/1024/1280/1440/1920
and asserts the band is whole inside the picture, decoding the screenshot in the page. Red at all five
widths with `object-position: center`, with the message "the marker band runs off the top of the hero".

**Numbers:** backend **736 unit + 373 integration** (was 736 + 369), 0 skipped; frontend 19 / 451 /
131 unchanged; **Playwright 199** (was 194). Lint clean, Release build clean with 0 warnings.

**Checked in a browser** — API, built SSR storefront, reverse proxy — at 320 / 768 / 1280 / 1920 plus
375 / 390, reduced motion at 1280, the poplin product page and the linen-and-denim listing: no console
errors, no failed requests, no horizontal scroll, the still (not the video) on phones and under reduced
motion, and the hero box clamping to 1920x880 as expected.

**Next:** the tester. Then the owner, for the hero re-export and the poster cut from the same take.

### 2026-10-01 (6) — W1-13 fix round 3: the rollback was deleting somebody else's files

Round 2 is verified — the blocker is gone, all four duplicate corners hold, the `--replace` corner my
own regression test found is properly fixed, plan and run agree line for line, the poster repro is back
to exactly 596 files and all five exit codes behave. One new major, introduced by round 2's own fix.

**MAJOR: the discard list was "every asset row now, minus a snapshot from the start of the attempt",
and that is a sentence about the whole table, not about this run.** It is right about this attempt's
uncommitted inserts and wrong about everybody else's: a row another connection commits mid-run is
committed and was not in the snapshot, so a failing import deleted a stranger's file. The tester
measured it — an ADM-18-shaped upload 16 s into a failing run, file deleted, row intact, and the note
saying **90** assets where the run had created 89. **The end state is the worst one available**: the
row survives, the bytes do not, there is nothing to restore from and nothing reports it — an admin
finds a broken image days later beside an import that said FAILED for an unrelated reason.

My reason for leaving the outcome list was sound (the poster genuinely never surfaces as a DTO, which
was round 2's finding) and the replacement was not: it bought "cannot miss an asset" at the price of
"can delete someone else's". **A missed file is litter; a deleted file is data loss**, so the trade is
never worth making. The list is built from outcomes again — nothing the pipeline did not hand *this
attempt* can enter it — and the one asset outcomes cannot see is added at the single call site that
creates one, after asking whether the library already holds those bytes. That question has to be asked
**before** the upload: afterwards the two cases are indistinguishable, and deleting a deduplicated
poster would be deleting the file somebody else's row points at. Proven red with the tester's own
sentence: *the other connection's file was deleted by this import's rollback*.

**And the honest consequence, which the new test pins rather than hides.** An outcome list cannot know
about an upload that was still running when a Ctrl+C arrived, so an interrupted run leaves that one
asset's files behind. It is left there on purpose: the only way to find them is to sweep the asset
table for `Processing` rows, and the admin upload path has **no transaction** — it commits a
`Processing` row and flips it to `Ready` afterwards — so a concurrent upload would be swept up with
it. The report says so in its own note, and the test asserts the bound (**at most one** asset's worth,
with no row pointing at it) instead of asserting a round number that was never true.

**MINOR: a dry run said `0 refused` and exited 0 for a drop whose real run fails.** The boundary was
drawn honestly in the code and nowhere the operator reads. A 12-byte `wordmark-light.png` planned green
and then failed the real run at exit 2 — and the documented workflow is dry-run first, so a green plan
followed by a hard FAILED is the one thing `--dry-run` exists to prevent. Outcomes are labelled
**`Planned`** in a dry run, the summary says "90 planned", and a footer says the plan does not open the
files, so `0 refused` means **not checked**, not none. Verified on his exact scenario.

**Ctrl+C is still unverified, and is staying that way until somebody presses it.** He got further than
I did — `AttachConsole`, `SetConsoleCtrlHandler` and `GenerateConsoleCtrlEvent` all returned success
against the import's own console and the run still finished normally. Neither of us will guess whether
`cmd /c` swallowed it, the harness is wrong, or the wiring does not fire. The downstream path is
covered by the decorator test; the three lines in `Program.cs` are not. **The owner should press it
once at a real terminal before merge** — and finding 1 is the argument for doing so rather than taking
it on trust, because it showed the discard step is exactly where an over-delete hides.

**Numbers:** backend **736 unit + 369 integration** (was 736 + 368), 0 skipped; frontend 19 / 451 /
131; Playwright 194. Lint clean, Release build clean with 0 warnings.

**Next:** back to the tester.

### 2026-10-01 (5) — W1-13 fix round 2: the refactor dropped an invariant the old key had by accident

The tester reproduced the rollback with the database byte-identical to baseline and the volume back to
596 files, confirmed 0 `HomeSection` / 17 `HomeSectionItem` usages all resolving to readable labels, and
proved the `clientWidth` correction is the border rather than a cover-up — the offset is exactly 2 px at
**every** breakpoint, including ones where no clamp can apply. He failed the branch on one blocker that
round 1 introduced, plus two minors and an exit-code argument. All fixed.

**BLOCKER, and the lesson of the round: round 1's key agreed with the database's unique index by
accident, and the refactor dropped that without noticing.** `catalog.ProductMedia` is unique on
`UX_ProductMedia_Product_Asset_Colour` = `(ProductId, MediaAssetId, ProductColourId)`. Round 1 keyed the
slot lookup on the asset, which *is* that index; round 2 re-keyed it on `(product, role, sort order)` —
the right fix for the replacement defect, and a key the database knows nothing about. Two byte-identical
files for one product deduplicate to **one** asset, both slots asked for it, and SQL Server refused the
second row: `Cannot insert duplicate key row ... The duplicate key value is (37, 198, <NULL>)`, on the
plain documented command with no flags. **The worst part was that `--dry-run` called that same input a
clean green plan, exit 0** — somebody who checked first was told it was fine.

The slot lookup asks both questions now, in the order that matters: is this slot taken by someone's
choice (conflict), is this photograph already in this product's gallery (one row, reported as *ONE
PICTURE, TWO FILES*, with both pointers still naming it), and only then insert or replace. Proven red
first, with the tester's exact SQL error.

**And the new test found a second case I had not: under `--replace`, the slot the duplicate was meant
for kept its superseded row.** One asset is one row, so that slot has to be emptied rather than left
pointing at a photograph the drop no longer sends. Fixed before the round closed, by the test written to
prove the first half.

**A dry run that disagrees with the run is worse than no dry run.** It resolves identity exactly as the
pipeline does — SHA-256 over the same bytes — with a negative stand-in id per distinct new checksum, so
two identical files are one planned asset and the plan walks the *same* placement logic against the
*same* rows. It now predicts duplicates and conflicts, and an explicit test asserts that a plan and a
run name the same files. It still cannot say whether a file would be **refused**: signature, size and
codec are only knowable by decoding, and claiming otherwise would be the same mistake in a new place.

**Minor: a rolled-back run left the hero poster's five files behind.** `UploadVideoAsync` stores the
poster as a second asset that never surfaces as its own outcome, so a discard list built from outcomes
could not see it — the database was perfect and the volume five files heavier, under a report that said
nothing was written. The list is a **diff of the asset table** now, taken while the transaction is still
open, which cannot miss an asset however it came to exist and cannot delete one that was already there.
*The test for this had to be rewritten once:* a refused video has its poster rolled back by the pipeline
itself, so the obvious repro never reaches the state in question — the hero has to **succeed** and the
run fail later.

**Minor: Ctrl+C skipped the discard and threw.** (**And I could not press it either** — see below.) The catch filter excluded `OperationCanceledException`,
so rows were safe (the transaction disposes) and every file written before the interrupt stayed —
finding 2 at a few hundred files — and the exception left `Program.cs` as a stack trace with an
undocumented exit code. Caught on its own now: roll back, discard, report "cancelled", **exit 4**.

**Exit codes, on the tester's argument and the coordinator's agreement:** exit 0 means "the drop landed"
to every script that will ever call this, and a run that deliberately did not apply files did not. **3 =
completed with files not applied**, documented beside 0/1/2/4. A *same picture* file is not counted in
it: its content did reach the library and its slot does point at it.

**The Ctrl+C could not be pressed from here, and that is said rather than glossed.** The tester found
`kill -INT` does not reach .NET's handler from Git Bash; this session's harness is worse — its process
has no console at all and `AllocConsole` is refused with ERROR_ACCESS_DENIED, so
`GenerateConsoleCtrlEvent` returns true and delivers to nothing. Measured three times against an import
deliberately slowed to 61–84 s: the signal was sent at 2.5 s, 5 s and 8 s and the run finished normally
every time. So the cancellation path is proven by a **deterministic** integration test instead — an
`IFileStorage` decorator that cancels the token at the twelfth file, asserting the rollback, the file
discard and the `Cancelled` report, and proven red on the old catch filter with the exception escaping
as `System.OperationCanceledException`. What remains unverified end to end is only the three-line
`Console.CancelKeyPress` wiring in `Program.cs`; the exit-code mapping it feeds now has its own tests.

**Written down rather than fixed:** a hold matches by `HomeSectionType`, so the same bytes under a
different contract name would fill that other slot unheld — correct, because the judgement is about that
picture in that slot and not a quarantine, and the code now says so. And the e2e count of 6 describes
the **stub's** homepage; the real page after this import has 4.

**Numbers:** backend **736 unit + 368 integration** (was 730 + 361), 0 skipped; frontend 19 / 451 / 131;
Playwright 194, two consecutive clean runs. Lint clean, Release build clean with 0 warnings.

**Worth saying before a client demo:** with the hero withheld the front page opens on "Shop by
department" rather than on artwork. That is the right call against a headless crop, and it is an
argument for getting the re-exported hero soon rather than for shipping the crop.

**Next:** back to the tester.

### 2026-10-01 (4) — W1-13 fix round 1: a failed run that had already committed everything

The tester verified every number, confirmed the three inferred renames by looking at the pictures, and
brute-forced every 1–4 character truncation of all 45 slugs against the repair rule — 0 misattributions.
He failed the branch on four majors and two asset problems. All fixed on `feature/W1-13-client-assets`.

**Major, and the shape of the round: "all or nothing" was only true of the one failure I had thought
about.** The name-resolution check stops before a byte is stored, so I had written that half an import
is the worst outcome available — and then left every *other* failure free to produce one.
`IMediaService` saves on the same scoped `AppDbContext` for every upload, so everything was committed as
it went. Measured with a patched HEVC hero: the run reported **FAILED, exit 2**, and permanently stored
88 assets, 358 files, 70 gallery rows and switched **four homepage sections live**. The whole run is one
transaction now, committed only when nothing has failed, and because storage is not transactional the
files of assets the attempt created are deleted on the way out. **The lesson is about where a guarantee
is written down:** a sentence in a class comment that only one code path honours is worse than no
sentence, because it stops the next reader looking. Proven red first — the old shape leaves 22 assets,
7 gallery rows, 24 usages and 12 section items behind.

The transaction needed the execution strategy rather than a bare `BeginTransactionAsync`:
`EnableRetryOnFailure` is on (there is a unit test asserting `RetriesOnFailure`), and that strategy
refuses a user-initiated transaction unless the whole unit of work is inside it. So the *attempt* is
what gets retried, and each attempt starts with `ChangeTracker.Clear()` and fresh caches — a retry on a
context still holding the rolled-back attempt's edits would be worse than no retry.

**Major: a corrected photograph was added beside the one it superseded, not in place of it.** The
gallery row was keyed on `(product, asset)`, so a re-encoded file — a different asset — was an
insert: two rows at sort order −2 both claiming to be the card, the superseded photograph still in the
gallery, and its usage row keeping the delete guard shut for ever. Two drops would fight over the card
and the gallery would grow on every run. **A slot is `(product, role, sort order)` now**, so the same
slot takes the new picture and the old one leaves. Verified on the real data: one row before and after,
the pointer moved, and the superseded asset left with **0** product usages.

**And the other half of that defect was the opposite mistake.** The card pointer was written
unconditionally, so an import reverted an admin's own choice every time it ran. It cannot be both — the
database cannot tell a corrected photograph from somebody's deliberate upload — so the operator says
which: `--replace`. A slot holding one of the demo placeholders is filled either way, because **a
placeholder was never a choice**; anything else is reported under *NOT APPLIED* and left alone. That
distinction is what makes "never overwrite a person" implementable on a seeded database at all.

**Major: the same `aspect-ratio` + `max-height` defect was still live in a third place.** The
department tiles' media frame — `aspect-ratio: 4/5; max-height: 420px`, no width — so the transferred
max-height became a 336 px max-width and the photograph stopped short of its own tile: 336 of 343 at
375 px, 336 of 358 at 390 px, worse between 422 and 767. Invisible until this import put photographs in
those tiles. The tester swept the codebase: exactly three rules set both properties, and the e2e test
now names all three and **asserts the count**, so a fourth added without a width fails there rather than
on a screen. Proven red on the tile at 375 and 390.

**Major: `MediaUsages` named the section, not the item.** Doc 08 §3.3 enumerates `HomeSectionItem`, and
a section id names "the homepage" for thirteen pictures — the "used in…" panel could not tell an
operator which tile to go and change, and `LoadOwnerLabelsAsync` had no case for it so it showed
`HomeSection 4` with no label. Now the item id, with a label that falls back from the tile's own heading
to its department's name.

**The two asset problems are decided, not coded around.** The hero video and the activewear flat-lay
are held back **by the SHA-256 of the delivered bytes** (`ClientAssetHolds`), with the reason printed in
the report. Keyed on the bytes because a judgement about a particular photograph has to expire by
itself: a hold keyed on the section type, or an operator flag somebody has to remember, outlives the
problem and quietly keeps a finished block off the front page — the same class of mistake in the other
direction. Both files are still imported and still attached to their items, so the client can see them
in ADM-17; what is withheld is only the automatic switch-on. The list is injected rather than baked in,
so the rule is testable with a checksum a test controls — the shipped entries are keyed on two real
files no test can conjure.

**Minors:** a case-variant filename (`Chino-Shorts-1.jpg`) killed the whole run under a message saying
the product did not exist, and on Windows that is the same file — matching ignores case now and always
resolves to the catalogue's spelling; the logo files' lack of an alpha channel is said in the report
rather than left for a design review (`ImageTransparency`, a header read, no decode); the CLI takes
Ctrl+C and rolls back; and the README says that re-running repairs **rows, not files**, so a lost media
volume needs a restore rather than another import.

**The guard the tester asked for:** no two product slugs may differ only by a prefix short enough for
the repair to reach across. It is checked against the **real catalogue** with the predicate the repair
itself uses, so a product the client adds later cannot quietly make a truncated filename ambiguous.

**Numbers:** backend **730 unit** (was 721) **+ 361 integration** (355), 0 skipped; frontend 19 / 451 /
131 unchanged; **Playwright 194**. Lint clean, Release build clean with 0 warnings, no new dependency.

**Checked by hand again, on the real assets:** 90 imported, 0 refused, 0 unmappable; a second run 0
imported / 90 already present and 960 files before and after; a corrected photograph reported under *NOT
APPLIED* and then applied with `--replace`, leaving one row in the slot and the superseded asset with no
usages; hero and activewear **off**, campaign, trending and social **on**; department tile photographs
filling their tiles at 375 and 390; 0 broken images, 0 console errors, 0 4xx/5xx over five widths, and
no horizontal scroll at 320 / 375 / 390 / 768 / 1920.

**Next:** back to the tester.

### 2026-10-01 (3) — W1-13: the client's pictures are in, and the logo is not a logo yet

The grey boxes are gone. `feature/W1-13-client-assets` (worktree `worktrees/sd-assets`) imports the
owner's delivered photographs, video and logo and switches on the five homepage sections W2-01 seeded
off. Developer pass only: not pushed, not merged, **not Done** — it goes to the tester next, and
`client-progress/progress-data.js` is untouched until it passes.

**It is a verb on the API, not a seeder and not a tool project.**
`import-client-assets --from <dir> [--dry-run]` reuses the whole composition root — the configured
volumes with their startup validation, the real `IMediaService`, the database — and never starts
Kestrel. A seeding flag would have tied the client's own photographs to `Seed__*`, which means "the
shape a development database starts in"; a tool project would have rebuilt the media wiring and been
free to drift from the one the API runs on. **Every byte goes through `IMediaService`**: nothing in
the importer composes a `MediaAssets` row or writes a file, so each photograph gets the same
signature check, EXIF stripping, rendition set, checksum deduplication and usage tracking as an admin
upload.

**The decision worth keeping: the naming contract is a pure function, in the Application layer,
separate from the thing that reads the disk.** `ClientAssetNaming` takes a filename and the
catalogue's slug list and answers with a product and a role. That rule — *which garment is this a
photograph of* — is the one thing here that can go wrong silently and expensively, so it is tested
exhaustively and without a database (31 cases). Three kinds of damage in the delivered batch are
undone: a doubled extension (`...-1.jpg.jpg`), the folder name run into the slug, and a character
lost off the front. **Every repair is bounded by the real slug list, so none of them can invent a
product**; a near miss with two answers is refused rather than guessed; and anything unplaceable
stops the whole run before a byte is stored, because half an import is the worst outcome available.

**Three filenames had to be inferred, and that is the whole of the "several products have only one
image" problem.** `ourt-canvas-sneakers-2` → `court-canvas-sneakers`, `productsleather-look-belt-1` →
`leather-look-belt`, `productspique-polo-shirt-2` → `pique-polo-shirt`. All three confirmed against
the pictures themselves. Resolved, coverage is **35 of 35 adult products with both photographs** and
**0 of 10 kids**, which keep their placeholders — no adult photograph is put in front of a children's
garment to fill a gap.

**90 files imported, 0 refused, 0 unmappable, and a second run imports nothing** (960 files on the
volume before and after; 0 imported, 90 already present).

**The video: `hero.mp4` is H.264 and the pipeline took it.** `avcC` says profile 0x64 (High), level
0x1e (3.0); the row is `video/mp4`, 360 × 640, 10.01 s, 1,140,963 bytes, `Ready`. **But it is
portrait**, and the hero frame is 16:9 from 768 px with `object-fit: cover` — so it is cropped to
about a sixth of its height and upscaled roughly 5×. It plays; it is soft. The owner should re-export
at 1920 × 1080. The second `.mp4` in that folder is reported as "not in the contract" rather than
quietly skipped.

**The logo is imported and deliberately not wired in, and the reason is the files.** All three are
**JPEGs** (2048 × 2048, `mode=RGB`, no alpha) carrying a `.png` name — the pipeline stored them as
`image/jpeg`, which is the proof, since it reads the signature and ignores the name.
`wordmark-dark.png` and `wordmark-light.png` are the *same mockup*: the mark embossed on a
photographic concrete wall with a drop shadow, mean luminance 165.4 against 167.3, so there is no
white version for a dark background. `monogram.png` sits on a cream paper texture, so a favicon cut
from it is a cream square. And the mark reads **COLOMBO WARDROBE**, where this codebase and the
signed proposal say Spencer Denim. Per the brief, the work stopped at the import rather than
half-rebuilding the header. **What is needed is a decision and flat SVG/transparent-PNG artwork, not
code.**

**The social gallery is wired as ordinary admin content.** Six `HomeSectionItems.LinkUrl` rows on
`https://example.invalid/PLACEHOLDER/<network>` — RFC 2606 reserves `.invalid` so it can never be
registered or resolve — and each tile's accessible name reads "<Network> — PLACEHOLDER LINK, the real
account URL has not been supplied yet (opens in a new tab)". A plausible `instagram.com/...` would
have tested just as well and could have shipped by accident. The existing external-link rules hold:
`target="_blank"`, `rel="noopener noreferrer"`, and the new-tab warning in the accessible name, all
confirmed in the browser.

**A defect nothing could see until these sections had something to show: the hero and both banners
stopped short of the right-hand edge at 1920** — the hero 1564 px of 1898, each banner 1680 px,
leaving a white band down the side of the front page. CSS transfers a definite `max-height` through
`aspect-ratio` into a **max-width** on an auto-width box: 880 × 16/9 = 1564.44 and 720 × 21/9 = 1680,
exactly the measurements. Fixed with an explicit `width: 100%`. **The lesson is the project's own, in
a new place:** the five sections were seeded off, so no page had ever drawn one of these blocks, and
every hero test asked whether the element existed rather than how wide it was. The new test measures
the rendered width against the width available at all eight breakpoints and is proven red on the old
CSS at 1920 (expected 1920, received 1564).

**Checked by hand, in a browser** — API + built SSR storefront behind this session's own reverse
proxy, private scratch directory, per-session ports, a throwaway database, all torn down: the hero
video plays muted on the desktop and is replaced by its poster at 320 px and under
`prefers-reduced-motion` (no pause control either); the campaign and activewear banners, the four
trending tiles and their filtered listings, and the six-tile social wall all render; real photographs
on the listing and the product page, with the hover image proven to be the same garment's second
frame (`relaxed-linen-blend-shirt-1` → `-2`); the kids listing still shows ten labelled placeholders.
**0 broken images, 0 console errors, 0 failed requests over nine pages, no horizontal scroll at 320 /
768 / 1920, and 0 original files fetched** — the browser takes `card.webp`/`thumb.webp`.

**One small thing not fixed, and named rather than worked around:** the hero's still inherits the
poster's alt text, which `MediaService` prefixes with "Poster image: ". That is a W1-04 convention
with its own tests, not an import bug, so it is reported rather than special-cased here.

**Numbers:** backend **721 unit** (was 690) **+ 355 integration** (344), 0 skipped; frontend 19 / 451
/ 131 (unchanged); **Playwright 194** (186), two consecutive clean runs. Lint clean, Release build
clean with 0 warnings, no new dependency. The integration suite is about a minute longer: the import
tests build a database of their own rather than mutating the assembly's, because the import rewrites
card images and switches sections on — the exact state a dozen catalogue and content tests assert
against.

**Next:** the tester.

### 2026-09-24 (6) — W2-01 PASSED, and three things found on the way out

The tester verified all eleven round-1 fixes against a fresh database and the real stack rather
than against the description, confirmed every number, and **passed the branch**. He backed both
judgement calls — the hero test rewrite and the flake root-cause — and improved the second:
`MediaUrlBuilder.ToPublicUrl` passes an `http`/`https` key through but **prefixes anything else**,
so the old `data:` URI would have reached a real storefront as `/media/data:video/mp4;…`. The
fidelity argument for serving the video from an absolute URL was stronger than it was put.

Three small things fixed before the PR.

**Minor, and the most interesting: one public-address call permanently silenced the shared-bucket
warning.** In `NewsletterSignupThrottle` the once-only guard ran *before* the is-this-local test, so
the first call spent the flag whatever address it came from. The tester proved it against the
shipped Release assembly: loopback first → 1 warning; **public first, then loopback three times → 0
warnings**. One signup from a monitor, a probe or a real visitor in any window before Caddy fronts
the API, and the warning that exists to announce the W0-15 shared-bucket state never fires again for
the life of the process. The guards are swapped. **The lesson is about the tests, not the code:** the
three existing tests each used one kind of address on a fresh instance, so not one of them could
see it — the bug lived in the *sequence*, and nothing tested a sequence. The new theory does
(`203.0.113.7` then `::1`, and two more pairs); proven red on the old order, 3 failures, green after.

**Two stale comments.** The browser suite still exempted failed `data:` requests "the hero's `data:`
video is a stand-in the browser cannot decode" — inert since the hero became a real WebM, and an
exemption nothing needs is one that hides the next failure; it is gone, with no exemptions at all.
`TASKS.md` still said four sections are seeded off; it is five, HOME-04 among them.

**Filed, not built (issue #37): the cache flattens scheduling precision.** `HomePageCache` always
expires at `now + 5 minutes`, so a section whose `EndsAtUtc` falls inside a warm entry outlives it —
measured, a sale that had ended was still served ~250 s later, and a campaign set to end at midnight
shows until 00:05. Inside doc 08 §3.7's five minutes and harmless to data, but the scheduling rule
was built and tested to the millisecond and nothing said that caching rounds it off. The class doc
now says so and names the issue and its fix (clamp to `Min(now + Lifetime, nextScheduleBoundaryUtc)`).

**A process rule that has now cost two sessions on one task, written down in
[docs/14](docs/14-multi-session-coordination.md) and pointed at from [docs/06](docs/06-engineering-process.md)
§4.** The scratch directory and the machine's ports are shared and not namespaced. A script this
session wrote as `scratchpad/proxy.mjs` came back, on a later read, as **another session's script**;
and the tester's round-2 proxy bound a port a **foreign `node` process already held**, so his first
batch of measurements went through someone else's process before he caught it and redid them.
Neither failure announces itself. The rule: a private uniquely named directory, every script written
fresh with a name nobody else would choose, `sha256sum` checked immediately before running,
per-session ports verified free and verified to be answering with your own banner — and never run a
script you did not write this session, whatever its name suggests.

**Numbers:** backend 385 unit (round 1: 382) + 212 integration, 0 skipped; storefront unit 443,
shared 19, admin 9; Playwright 184, three consecutive clean runs. Lint clean, all builds clean.

**Next:** pushed to `origin feature/W2-01-homepage`; the owner opens the PR.

### 2026-09-24 (5) — W2-01 fix round 1: two gates that disagreed with the rendering

The tester verified every number and every claim from the developer pass — schema, seed counts,
scheduling, the server-side rows, the `routerLink` fix and all eight API-failure modes, against the
real stack — and failed the branch on six majors and five minors. All fixed on
`feature/W2-01-homepage`.

**Major, and the shape of the round: a section's has-content gate and its rendering disagreed.**
`isUsableTile` counted an *external* link as content; both tile components render with `routerLink`
and drop anything that is not a path on this site. So a HOME-02 or HOME-04 section whose tiles all
carried an absolute URL passed the gate and then drew an empty list — a bare heading over a gap
between two real blocks. Pasting a full `https://spencerdenim.lk/women` into a department tile is an
ordinary ADM-17 mistake. Reproduced on the real stack: with all three department tiles set to
absolute URLs the served HTML contained **0 tiles, 0 headings and no section element**, and the four
other sections were untouched; with one tile absolute, only that tile went. **The lesson is the one
from W2-05 in a new place: a gate that asks a different question from the renderer is a gate that
passes things the renderer then drops.** The gate now asks `toLink(...)?.kind === 'internal'`, which
is exactly what the components can draw.

**Major: the hero video could never autoplay, and the pause control lied about it.** The `<video>` is
never in the server's HTML — the server always renders the still — so it is always created by
Angular, and on that path a bare `muted` **attribute** does not set the `muted` **property**.
Measured live, twice: `HTMLMediaElement.muted === false` with the attribute, `true` with
`[muted]="true"`. Worse, `playing` was `signal(true)` — an assumption, not an observation — so with
playback refused no event ever fired and the button read "Pause" over a stopped video; pressing Pause
started it. `playing` now starts `false` and moves only on the element's own `play`/`playing`/`pause`
events, and `toggle()` no longer sets it optimistically.

**And the test was one that could not fail.** `${width}px: the video plays, with a way to stop it`
asserted that the element existed and that a hard-coded label matched `/pause/i`. It passed with the
product broken. Two things were needed to make it real: it now reads the `muted` **property**, and
the e2e stub serves a genuinely decodable video (`e2e/support/hero-sample.webm`, three seconds of
VP8 recorded with this same Chromium) instead of a `data:` URI carrying an MP4 header and nothing
else. With that in place the test polls `!video.paused`, watches `currentTime` advance, and presses
the control both ways. **Proven red on the old markup** — the five width cases fail, reporting
`muted: false`.

**What could not be proven, and is not claimed:** that Chrome *refuses* the unmuted element.
Playwright's Chromium disables the autoplay policy, and overriding it per test
(`--autoplay-policy=document-user-activation-required`) did not take; a fixture with an audio track
played unmuted too. The wrong property is measured; the consequence rests on Chrome's documented
policy. The one direct sighting of it: while the video URL was still being fetched from the wrong
origin, the element did not play and the control correctly read **"Play the background video"** —
which is the honest behaviour the fix introduces.

**Major: re-running the seeder resurrected announcement copy the client had removed.** An
announcement has no key but the sentence itself, so "insert what is missing" could not tell a
rewritten line from a deleted one: the "Free island-wide delivery on orders over Rs. 10,000"
placeholder — a promise the client may have deliberately taken down — came back on the next
deployment, onto every page of the site. Doc 08 §9/N13 forbids overwriting an admin edit. That step
is all-or-nothing now: once the table holds anything, it does nothing. Reproduced end to end on a
fresh database — delete one line, rewrite the other, reseed: `announcements 0`, deletion respected,
rewrite intact. The class doc claimed every seeded row had a key copy could not change; it now says
which rows that is true of and which it is not.

**Major: doc 08 §3.7's five-minute homepage cache was simply not implemented**, and one render cost
16 SQL commands (18 with a department listing) against 5 for a whole listing page — on the busiest
page on the site. Built as a decorator (`CachedHomePageQueryHandler`) over a keyed registration, with
the eviction hook ADM-17's save has to call (`IHomePageCache.Evict`, `TODO(ADM-17)` naming it) and a
`TimeProvider`-driven store. An integration test proves the second render costs **0** SQL commands
and that evicting makes a change visible at once. On the real stack: 2–17 ms warm against 9–70 ms for
the equivalent uncached listing call. The per-row cost inherited from the listing reader is
unchanged and is not this task's to fix.

**Major: `id="newsletter-email"` was on both the homepage form and the footer's**, which is on every
page including that one. An id is document-unique, so the footer's label pointed at the homepage's
input: the footer field had **no accessible name at all** and activating its label threw focus up the
page. axe 4.10 retired `duplicate-id`, so nothing was going to catch it. Scoped per section, like
`hintId()` and `errorId()` already were. Verified live: no duplicate id anywhere in the document, and
each label resolves to the input in its own section.

**Major: the signup rate limit was one bucket for the whole site behind a proxy.** Partitioning on
`RemoteIpAddress` is right and unspoofable, but doc 03 §4 puts Caddy in front and W1-01 deliberately
left `X-Forwarded-For` off, so every visitor lands in Caddy's partition. **Forwarded headers were not
turned on here** — that would re-open the audit-log spoofing defect with no proxy address to pin.
Instead the limit moved *inside* the use case as a port, so the order is validate → take a permit →
write and a mistyped address costs nobody anything; the ceiling is sized for the shared-bucket case
(600 per 10 minutes); and the throttle logs a warning, once, naming W0-15 when the address it sees is
loopback or private. W0-15's row now carries the forwarded-headers work as a **launch blocker**.
Verified live: 800 mistyped addresses consumed **zero** permits and a real signup still went through;
620 valid ones produced exactly 600 rows and then 429 with `Retry-After: 600`.

**Minors** — the footer signup is wired to the endpoint and recorded as `footer` rather than telling
every visitor on every page that nothing was saved (GLB-07 finished; the API accepts `homepage` and
`footer` and nothing else, so no caller's string reaches a `varchar(30)` the client reads); two more
tests that could not fail (the announcement assertion looked for a line that is *also* in
`PLACEHOLDER_ANNOUNCEMENTS`, so it passed whether the feed worked or not — the stub now serves a line
the fallback does not contain; and narrowing the shell's touch-target sweep had silently dropped the
root-level `.skip-link`, which is back); the newsletter's timing side channel (95 ms for a new
address against 7.6 ms for a known one) closed with a 250 ms floor on every answer, driven through
`TimeProvider` so the tests do not sleep; `parseQuery` builds on `Object.create(null)`, so
`?__proto__=x` adds a key instead of reassigning a prototype; and the newsletter consent IP joined
P17's retention table at 90 days, matching the order and enquiry IPs, with both signup forms now
saying an IP is recorded and a `TODO(GLB-10)` for the policy link.

**HOME-04's seed changed on the tester's argument, and he is right.** Seeding it `auto` shipped a
best-selling *product row* labelled "Trending now" — not one of HOME-01..10, not the thing HOME-04 is
defined by, and with a "view all" pointing at `/`. It is seeded `{"mode":"manual"}` and **disabled**,
alongside the four other artwork-dependent sections, for exactly their reason. A best-sellers row is
a change request to discuss, not a default. Both modes stay built and tested.

**A flake root-caused rather than retried:** an unrelated newsletter e2e timed out about one run in
three once the hero had a real video. The video was being proxied through the tests' own
`page.route` handler, and a looping video held that handler open while everything else on the page
queued behind it. The stub serves the video from an absolute URL of its own now, so it never enters
the route handler. Three consecutive clean runs.

**Numbers:** backend 382 unit (round 1: 348) + 212 integration (204), 0 skipped; storefront unit 443
(431), shared 19, admin 9; Playwright 184 (183), three consecutive clean runs. Lint clean, all builds
clean, no new runtime dependency.

**Next:** back to the tester.

### 2026-10-01 (2) — W1-04: the Linux CI job caught a defect Windows structurally cannot

PR #60 was green on Windows and failed the backend job on Linux. Two failures, one bug.

**The bug: two different products produced byte-identical files.** The demo placeholder put the
whole difference between frames in its *text* — the product name and the colourway. The GitHub
runner's container has no font SkiaSharp can find, so the renderer drew no glyphs, every
placeholder came out as the same image, every one hashed to the same SHA-256, and
`UX_MediaAssets_Checksum_Ready` refused the second one. Seeding died with a duplicate-key
exception and took most of the integration suite with it. **The index was doing its job.** The
worst shape was the shared fabric-detail frame: no colourway, the same fallback swatch, so all
forty-five of them were one picture even before the font question.

**Fix 1 — the image is distinct without a glyph.** A SHA-256 of the asset's storage key (which
already carries a unique index) now drives a ground tint and a forty-cell identity block: a grid of
small neutral squares, drawn, not written. Text is an *enhancement* on top. Proved against the real
data rather than argued from entropy: every one of the 149 demo frames, rendered with no font,
hashes to a distinct value, and twelve of them now generate and **save in one transaction** — the
save that used to throw.

**Fix 2 — no host font is required at all.** Doc 03 §7, and this was that rule broken quietly. The
obvious move was to reuse the storefront's Inter (`@fontsource/inter`, OFL, which does permit
bundling). It cannot be done: that package ships only `.woff`/`.woff2`, and Skia opens neither —
measured, `SKTypeface.FromFile` returns null for both. Rather than commit a new TrueType binary and
its licence for words on a development-only image, labels now come from
`Media__PlaceholderFontPath` when a deployment names a `.ttf`/`.otf`, from the host's font manager
when it has one, and from nowhere otherwise. A missing or unreadable file logs and falls back; it
never takes the API down.

**Fix 3 — the test asserted something that cannot always be true.** `The_renderer_can_draw_text_on_this_host`
failed the build on any fontless machine. The placeholder tests now render with `font: null`
throughout — the deployment path — and the label assertion is gated on a font actually being
present. `IPlaceholderLabelFont` is the seam that lets the **integration** suite run the fontless
path on Windows, which is the only way this class of defect is ever catchable here.

**The other host assumption, found while looking:** the upload spool used `Path.GetTempPath()`. On
a Linux image `/tmp` is frequently `tmpfs` — RAM, on a VPS where doc 03 §4 has already given SQL
Server 2 GB of 8, and a 50 MB video goes through it. Now `Media__UploadSpoolPath`, resolved against
the content root, created at startup.

**`proxy.conf.json` did not proxy `/media`** — so under `ng serve` every card image 404'd and fell
back to its placeholder. It forwards **`/media/public`**, not `/media`: the Angular build emits its
own webfonts to `/media/inter-*.woff2`, and a blanket rule swallows them — the same trap doc 03 §4
already describes for Caddy. Verified under a real `ng serve`: the product image 200s from the API,
`inter-latin-400-normal.woff2` 200s from the dev server.

**Found by hand, not by a test: the stored original still carried a polyglot's payload.** The
renditions were always clean (they are re-encoded from pixels), but the JPEG stripper copied
everything after the start-of-scan marker verbatim — which is exactly where an appended script
lives. It now walks the entropy-coded data properly (stuffed bytes, fill bytes, restart markers,
progressive JPEG's later scans) and stops at the end-of-image marker, so anything after it is
dropped. A file with no EOI is still kept whole and left to the decoder.

**The hand check, end to end** — API + built SSR storefront behind a reverse proxy, per-session
ports, private scratch directory, throwaway database, all torn down after: a real JPEG carrying a
real EXIF GPS block and camera serial uploaded and stored with both gone; a real H.264 MP4 (1.1 MB,
CC0) read as 960×540 / 5.06 s with a byte range returning 1,000 bytes identical to the source and
`If-None-Match` answering 304; a script, a zip and a 20 MB body refused 415/415/413; a filename of
`../../../../etc/passwd<script>.jpg` stored as `passwdscript.jpg`; and a bank slip on the private
volume with a Ready row in the database refused by **ten** spellings of its key through `/media`
while a public key answered 200 on the same endpoint. The storefront shows labelled placeholders,
not grey boxes.

**And one more gate that asked the wrong question.** The generator's "is this asset already finished?"
check looked for every code in `MediaRenditions.Current`, but a 1200 px placeholder has no
`zoom.webp` — `ProducedFor` clamps it to `large`'s width and does not write it. So the check never
found the file, always answered "not finished", and re-rendered all 149 placeholders on **every
startup**. Nothing failed and nothing logged; it was simply a few hundred encodes of blocked boot
that the class documentation said were not happening. The same shape as the defect `ProducedFor`
was introduced to fix, one layer up. Now gated on the produced set, with an integration test that
runs the generator twice and checks the file's write time did not move — proven red on the old gate
first (`the finished asset should have been counted as already present`).

**Numbers:** backend 690 unit (was 680) + 344 integration (341), 0 skipped; frontend 19 / 451 / 131;
Playwright 186. Lint clean, all builds clean, no new dependency. CI green on Linux.

### 2026-10-01 — W1-04 media library: PASSED after two fix rounds

The media pipeline is done and the tester passed it. Branch `feature/W1-04-media-library`
(worktree `worktrees/sd-media`), pushed, awaiting a PR. **Not Done until merged and the client
progress page is updated** — and `client-progress/progress-data.js` lives outside this repo, so it
is not in this branch.

**The find that mattered.** The renditions had been generated, stored and served with a year's
cache since the first commit of this task, and *nothing linked them*. Every card, every home row
and every gallery served the full-size original. The pipeline was half a feature that looked
finished, and no test could see it: the integration fixture never configured a media root, so the
demo placeholder generator was a silent no-op, every seeded asset had a null `RenditionSetVersion`,
and the payloads legitimately fell back to originals. The suite could not tell a reader that picks
the right rendition from one that ignores renditions entirely. Giving the fixture a real throwaway
volume is what made the gap visible — and the reason to prefer a fixture that runs the real thing
over one that is merely fast.

**Measured, not estimated:** a 32-image listing went from 2,101 KB to 383 KB (82%); the card
rendition a card now links is 10,432 bytes against the original's 56,976.

**Decisions worth keeping:**

- **`src` *and* `srcset`, with `sizes` from the client.** `srcset` alone buys nothing: with no
  `sizes` the browser assumes the image fills the viewport and fetches the widest candidate. Proved
  it — the control run fetched `large.webp` where `card.webp` was wanted. `sizes` describes the grid
  an image sits in, and only the component drawing that grid knows it, so the server ships
  candidates and their true widths and the client ships layout. The DTO cost is one nullable string
  per image slot.
- **One rule for which renditions exist** (`MediaRenditions.ProducedFor`), read by both the encoder
  and the URL builder. Renditions clamp to the source and never upscale, so a 1200 px photograph
  rendered `large` and then `zoom` byte for byte — a quarter of the seeded renditions were pure
  copies. When the two sides disagreed, one wrote a file the other would not link, or worse linked
  one that was never written. Seeding dropped 745 files to 596. An unknown source width is treated
  like an unknown set version: claim nothing and fall back to the original, because a heavier
  picture beats a broken one.
- **A guarantee in the doc needs a constraint in the database.** Doc 08 §3.3 promises deduplication
  by checksum; it was a read followed much later by a write, and eight simultaneous identical
  uploads produced eight assets and forty files. Now a filtered unique index
  `(Checksum, MediaType, Visibility) WHERE IsDeleted = 0 AND Status = 1` settles the race and the
  loser withdraws and answers `Duplicate`, so a caller cannot tell which path served it. Filtered on
  `Ready` so the index bites at publication, when the loser has real files to roll back, and so a
  `Failed` row never blocks a retry.
- **Soft delete is a save, so it rewrites the rows the asset owns.** Deleting a video used to leave
  the usage row naming it as the poster's owner, which bricked the poster for ever: 409 blocked by a
  video the query filter hides, with a null label because the label lookup reads through that same
  filter. Two assets ruined by one delete.
- **`Status` is not covered by the soft-delete filter.** The new projections guarded `IsDeleted` by
  inheritance and `Status` not at all, so a `Failed` asset produced a perfectly formed URL for a
  file that was never written, where the same asset soft-deleted produced a clean empty frame. Two
  ways to have no file, two different answers. `HomeContentReader` had the guard from the start,
  which is what made the asymmetry visible.
- **The doubled `<PublicRoot>/public/...` path segment is deliberate.** The key is the asset's
  address (doc 08 §3.3), so key and path stay identical: the mapping is reversible and the planned
  Azure Blob adapter can use the key verbatim as the blob name. Stripping the prefix would also mean
  the containment guard validated a different string than the grammar approved. Only the dev root
  was renamed, to `.local-media/uploads`, so the repetition stops reading as a mistake.

**Two hazards found and written down rather than fixed here:**

- **`/media` is a shared namespace in production.** The Angular browser build emits its own webfonts
  under `/media` (`dist/storefront/browser/media/inter-*.woff2`), so a Caddy rule that serves
  `/media` from the uploads volume and stops there will shadow them. Noted in doc 03's deployment
  topology; it needs a fall-through to SSR on a miss, or a different `resourcesOutputPath`.
- **`proxy.conf.json` does not proxy `/media`,** so under `ng serve` every card image 404s and
  silently falls back to its placeholder. One line, unrelated to this branch, no test covers it.

**Not in scope, filed separately by the coordinator:** media uploads and deletes write no
`ops.AuditLogs` row. `IAuditLogWriter` is injected only by the auth handlers, and doc 08 requires
"every write is audited" for Settings and RBAC but never for media — a cross-cutting gap, not a
media bug.

**Numbers at hand-off:** backend 680 unit + 341 integration, 0 skipped, 0 warnings. Frontend 19 /
451 / 131 unit, lint clean, both builds clean, Playwright 186 across two consecutive clean runs.

**Next:** the coordinator opens the PR.

### 2026-09-24 (4) — W2-01 homepage, developer pass

The placeholder home page is gone. `/` is a real, server-rendered page on
`feature/W2-01-homepage` (worktree `worktrees/sd-home`), built on the product-page branch. Not
pushed, not merged, not Done: it goes to the tester next.

**The page has no opinion about what is on it.** Ten section types, every one of them a row in
`content.HomeSections` that the client will edit in ADM-17: which blocks appear, in what order,
with what heading, for how long. The page renders the list the API sends, and a section that
arrives with nothing in it renders **nothing at all** — no frame, no heading over a gap, no
"coming soon". `hasContent` is one pure function with one test over all ten types, rather than ten
`@if`s that have to agree.

**The decision worth recording: a product row is resolved server-side, from its own listing URL.**
`SettingsJson` carries a storefront listing URL (`/new-arrivals`, `/sale`, `/?sort=best-selling`),
`ListingUrl.ToCriteria` turns it into the criteria the catalogue read store already takes, and the
same URL is the section's "view all". So the row *is* the head of the listing it links to, by
construction — they cannot drift apart, and an integration test asserts it against the catalogue
endpoint itself. Verified live: the homepage's New Arrivals row and `/new-arrivals` both show the
same two products. The alternative — the page fetching each row — would have put three HTTP round
trips inside the render of the page a crawler reads, and made the row and its link two definitions
of one listing. Cost: three sequential SQL queries in one request (sequential because they share a
DbContext).

**Four sections are seeded switched off**, per doc 08 §9's "media-dependent sections disabled until
content exists": the hero video, the campaign banner, the activewear feature and the social
gallery are nothing but artwork and W1-04 has not landed. All four are built against the real
shape and exercised in the browser through a stub that is deliberately **richer** than the seed —
mirroring the seed would have left them untested in the one place their behaviour can be seen.

**HOME-04 is the one place the brief and doc 08 disagree, and doc 08 wins.** The brief listed
Trending among the product-backed rows; doc 08 §3.7 says "trending items store a filtered listing
URL" and scope HOME-04 says "each card opens the matching filtered listing", which is a row of
tiles, not of products. Both modes are built: `mode: manual` renders the client's tiles, each
opening its listing, and that is what HOME-04 means. It is **seeded `auto`** anyway, because §9
pins the seed at six items and a tile needs artwork — so the row works on day one and becomes the
tile row the moment the client uploads pictures.

**HOME-10 really subscribes.** `marketing.NewsletterSubscribers` is specified in doc 08 §3.6, so
the minimal endpoint was built rather than the form being disabled with an explanation. Single
opt-in, which is the shape that table has (two statuses, no "pending"); a double opt-in needs the
§3.8 outbox. The answer is the same sentence whether or not the address was already on the list,
because a form that said otherwise is a way to test whether someone shops here. Rate-limited per
caller. Verified live end to end: a row with status Subscribed, source `homepage`, the real socket
peer as the consent IP, and an unsubscribe token.

**Three defects the browser found and jsdom could not:**
- **`routerLink` given a whole URL as one command percent-encodes the `?`.** A trending tile
  pointing at `/women/jeans?fit=wide-leg` landed on a path literally called "jeans?fit=wide-leg" —
  a 404 dressed up as a filter, on the one feature whose entire point is that the card opens the
  matching filtered listing. `toLink` now splits the query off and hands it over as `queryParams`,
  repeatable filters included. The announcement bar had the same hole.
- **The gallery's inset focus ring was no ring at all** from outside the photograph. `edgesWithoutRing`
  reported all four edges, correctly: the cell was clipping its overflow, so the first version went
  inset — which is what lesson L4 is about. `object-fit: cover` already crops the square, so the
  clip was never needed and the ring is an ordinary outward one now.
- **A container section flush against the banner below it lost the focus ring on its last row.**
  The full-bleed blocks are `position: relative`, so they paint above the static content before
  them. Measured on a trending tile at 1280, bottom edge. Fixed with real bottom spacing, which the
  layout wanted anyway.

**Two shared tests were pointed somewhere they could no longer mean what they said**, and were
corrected rather than worked around: the shell's touch-target sweep took every `a[href]` on `/`,
which was fine while `/` was a placeholder and is not now that a product card's stretched link is
on it — it measures the shell's own controls, and `home-page.e2e.ts` proves the stretched link's
target really is the card; and the listing's "not the listing" assertion read `.product-grid`,
which the homepage now uses too, so it asks about the component.

**What the API and the data could not serve, and was therefore not invented:**
- **No media at all (W1-04).** Department tiles render without photographs — a named panel that
  opens the department is usable, unlike an empty banner frame — and the four artwork sections stay
  off the page.
- **New Arrivals is two products on the seeded catalogue**, because `Catalog.NewArrivalDays` is 30
  and only two of the 45 demo products are still inside their window. Thin, and honest: it is
  exactly what `/new-arrivals` shows.
- **There is no "everything" listing route**, so the trending row has no "view all" to offer and
  renders none rather than a link to the home page.
- The benefits copy and the two announcement lines are **placeholders** the client replaces before
  go-live (docs/04 §4 decision 8, D8). None of them states a figure the system has not been
  configured with.

**Numbers:** backend 348 unit (was 232) + 204 integration (146), 0 skipped; storefront unit 431
(361), shared 19, admin 9; Playwright 183 (128), three consecutive clean runs. Lint clean, all
builds clean, no new runtime dependency.

**Next:** the tester.

### 2026-09-24 (3) — W2-05 fix round 2: the same blindness, one layer down

The tester re-implemented round 1's `edgesWithoutRing` himself, proved it falsifiable and
deterministic over 10 controls at all eight breakpoints, and then found that **the fix I made
alongside it had exactly the fault the round was about.**

**Major: the sold-out slash was invisible on 12 of the 16 seeded colourways.** It painted in
`--color-text-muted` whatever the swatch under it was — the dot has no text, so that `color` only
fed the gradient. Measured against WCAG 2.2 AA 1.4.11's 3:1: Olive Green **1.08:1**, Deep Teal
1.16, Indigo Rinse 1.30, Plum 1.31, Terracotta 1.46, Charcoal 1.59, Mid Wash Blue 1.68, Deep
Maroon 1.74, Chocolate 1.80, Mustard 2.46, Blush Pink 2.59, Deep Navy 2.68. Only Sandstone, True
Black, Coconut Cream and Optic White passed. So the size buttons got a real third state and the
swatches got a mark that mostly was not there.

**And my test made the same mistake the ring test used to**: it asserted `::after { content }` was
not `"none"` — that the mark was *specified* — on a single stub colourway that was itself one of
the invisible cases. **Specified is not visible. That is twice now in one task**, so it is worth
stating as a rule: *an assertion that reads back a declaration is testing the stylesheet. If the
finding is "a shopper cannot see this", the test has to measure what reaches the screen.*

The new test reads the colour Chromium resolved for the slash and for the dot beneath it and
computes the real ratio, over all sixteen seeded colourways (the e2e stub carries the full palette
now, all sold out on one product). Proven red first, naming the same twelve with the same ratios.

**The fix is a measurement, not a threshold.** `core/platform/contrast.ts` picks between
`--color-text` and `--color-on-primary` by measuring both against the swatch. Choosing the better
of two inks at opposite ends of the scale has a floor — the crossover where both score alike —
which for these two is **4.17:1** at luminance 0.2017. I first wrote 4.58, which is the figure for
*pure* black and white; the dark ink is `#1a1a1a`. The spec walks the whole colour cube and pins
the real number, so an admin's next colour cannot fall through. Worst across the seeded palette is
now 4.75:1 (Terracotta). `isLight()` is deliberately not reused — it answers "does this need a
border against white?", and its 0.8 threshold calls Mustard and Blush Pink dark, where white scores
2.17:1 and 2.06:1.

**Minor, and the same shape again: the ring test was aimed at the one control that could not
fail.** The quantity group is three controls edge to edge, so each one's right-hand ring lands
under the next sibling; `FOCUSABLE` listed `.qty__step:not(:disabled)` and `.first()` resolved to
the **plus** — the last child, the only one whose ring cannot be covered. The box and the minus
both lost `right` at all eight breakpoints. All three are listed now, with the page put into the
state where the minus is live.

**Minors** — `scroll-padding-top` on `html`, because the browser scrolls a focused element to the
top of the viewport and the header is sticky (118 px of the zoom button sat behind it); the size
guide's dead CSS moved to a shared `button.link` rather than being deleted, since the markup stays
for issue #32 and deleting the styling would have left it unstyled the day the API serves content;
one more pre-hydration flake (reading `document.body` the instant a replayed click resolves).

**The 4-vs-8 sub-44 px link count is settled**: 4 on the stub's profile, 7 on the real catalogue, 9
with a recently-viewed row on screen. The invariants the test asserts hold in all three.

**Numbers:** storefront unit 361 (round 1: 333), Playwright 121 (117), shared 19, admin 9; backend
232 + 146, 0 skipped. Lint clean, builds clean, four consecutive clean Playwright runs.

**Next:** back to the tester.

### 2026-09-24 (2) — W2-05 fix round 1: a test that could not fail

The tester verified every number and every claim from the developer pass and failed the branch on
two majors and five minors. All fixed on `feature/W2-05-product-page`.

**Major, and the lesson of the round: the thumbnail focus ring was clipped, and the test was
structurally incapable of noticing.** The rail scrolls, so its padding box is the clip edge, and
the thumbnails sat flush against it; a ring is drawn *outside* the border box, so three of its four
sides were cropped and a 2 px sliver along the top survived. Lesson L4 again. The assertion read
`getComputedStyle(element).outlineWidth` — the outline the element *would* paint — which no
ancestor's clipping can ever change, so it passed on a ring three quarters invisible. It now
screenshots the strip outside each edge with the control focused and again blurred and fails on any
edge whose pixels come back identical. **Proven red on the old CSS before the fix** (all four edges
at 1280 and at 375), green after, green for the other six controls throughout.

Two of its first reds were the helper *refusing to measure* rather than passing quietly — a control
flush against the viewport edge, and the zoom button's top strip covered by the sticky header at
y=118 — which is the behaviour worth having. Both were answered by the viewport the test sets, not
by loosening the assertion. **Rule: an accessibility assertion that reads a computed style is
testing the stylesheet, not the screen. Measure the pixels, and make the unmeasurable case throw.**

**Major: a sold-out size or colour looked exactly like one in stock.** The CSS distinguished only
`:disabled`, bound to "not made in this colourway"; a variant that is made with nothing left got no
treatment at all. Measured on the live API: sizes 26 and 28 of `wide-leg-rinse-jeans` were
byte-identical to 30/32/34, and both swatches of `sleeveless-column-dress` drew the in-stock
border. The screen reader was told and the sighted shopper was not. Three states now, the sold-out
one marked with a diagonal slash rather than a colour change (WCAG 1.4.1), inset inside the border
box so it can never reach the focus ring.

**Minors** — the quantity box kept showing `0`, an empty string or `1.7` whenever the typed value
clamped to the number the binding already held (`[value]` writes nothing when the value is
unchanged), so the clamped number is written back to the element; with no size chosen the page
quoted "3 × Rs. 5,990 = Rs. 17,970" under a price line reading "From", and offered a ceiling of 138
— the whole colourway's stock — so the ceiling is 1 and the note asks for a size until a variant is
settled; the touch-target test claimed "every control" while excluding every link, and now says
what it measures, with the links checked separately against the exemption each one actually stands
on (a stretched card link whose target is the card, or a breadcrumb crumb at 44 px tall and over
the AA 24 px minimum); `role="tabpanel"` no longer renders without a tab list.

**The size-guide trigger is gone until issue #32.** `SizeGuideRefDto` is `(id, name)` and the API
projects none of the content in `catalog.SizeGuides`, so the button opened a dialog to say there
was nothing to see. `SizeGuideContentDto` now writes down the shape the API has to send,
`hasSizeGuideContent` is the single condition the trigger and the modal hang on, and a test proves
both come back the moment content arrives. `SizeGuideModal` stays in the tree.

**The stub was still shaped more politely than the API** — everything in stock, everything
discounted, every product with a size guide — so those branches never reached a browser. Stock and
discount are derived from the variant grid now, every eleventh product is wholly out of stock and
every fourth has no size guide. **Same lesson as W2-03 round 3 and as the description bug: a stub
that is tidier than the real API tests nothing.**

`Modal` also got its own spec (14 cases), because the only cover it had was the size-guide tests
that just went away; and the rating row joined the price block in the shared stylesheet rather than
being copied into two components.

**Numbers:** storefront unit 333 (round 1: 312), Playwright 117 (112), shared 19, admin 9; backend
232 + 146, 0 skipped. Lint clean, builds clean with no budget warnings.

**Next:** back to the tester.

### 2026-09-24 (1) — W2-05 product detail page, developer pass

`/product/:slug` is a real, server-rendered page on `feature/W2-05-product-page` (worktree
`worktrees/sd-pdp`), built on the listing branch. Not pushed, not merged, not Done: it goes to the
tester next.

**The decision worth recording: the colourway and the size live in the query string.** The rule
was "shareable and server-renderable if it changes what is shown", and both do. A colour changes
the *photographs* — `catalog.ProductMedia.ProductColourId` ties each gallery shot to one colourway
and the demo seeder gives every colour its own, so `?colour=47` and `?colour=48` are different
pictures. A size changes the *money* — `ProductVariants.PriceOverride` is real and seeded, and
Washed Linen Camp Shirt is Rs. 5,990 in XL and Rs. 6,290 in XXL. The **canonical** address carries
neither, so the colourways stay one page in an index rather than one URL per colour.

**Three failures, three answers.** A slug the catalogue does not know (404) and a slug the API will
not accept (400) are the same thing to a shopper, so both render the site's own not-found page at
a real HTTP 404. A store that answers badly is different — the product probably exists — so that
is a "try again" page and **503 with `Retry-After`**, which a crawler retries where it drops a 404.
Same reasoning as the fallback-tree note filed against ADM-11 last session.

**What the API cannot serve, and was therefore not invented:**
- **PDP-04 has no size-guide content.** `catalog.SizeGuides` holds `IntroHtml`, `TableJson`,
  `MeasuringTipsHtml` and an image, and `CatalogReferenceDataSeeder` fills all four — but
  `SizeGuideRefDto` is `(int Id, string Name)` and no endpoint serves the rest. The modal is built
  against the real shape and says plainly that the chart is not published yet, pointing at the
  WhatsApp button. **The fix is an API change, not a component change**: project the guide's
  content onto `SizeGuideRefDto` or add `GET /api/catalog/size-guides/{id}`. Filed for W1-02/ADM-17.
- **PDP-05's delivery estimate, delivery methods and returns terms have no source at all.**
  Delivery methods and fees are ADM-22/CHK-02 settings and the returns copy is a GLB-10 content
  page, both week 3. Nothing was written: "2–3 working days" on a storefront is a promise no part
  of the system has made yet. The description, material, fit, occasion, age group and care are all
  from the API and all render.
- No seeded product has a **video**. The gallery handles one (role `Video`, poster, its own slide);
  the e2e stub is what exercises it.
- **Media is still not served (W1-04)**, so every `<img>` on the page 404s today. Every frame is a
  fixed 3:4 box and a failed image swaps for a placeholder inside it, so the layout is identical
  either way and nothing moves.

**Two defects that only clicking through the real API found, after the suites were green:**
- `Products.Description` is **sanitised HTML** (doc 08 §3.2) and the seeder writes `<p>…</p>` into
  it. Interpolating it printed the tags at the shopper. It goes through `[innerHTML]` now (Angular
  sanitises it again), with the markup stripped out of anything copied into a `<meta>`. The e2e
  stub served a plain sentence in that field, which is exactly why no test saw it — **a stub that
  is shaped more politely than the real API tests nothing**, the same lesson as round 3 of W2-03 in
  a different coat. The stub serves HTML there now.
- A not-found page re-asked the API after hydration and collected a second, guaranteed 404 in the
  console. A missing product is handed over as missing; a *fault* still is not, because a 500
  during the render may be over by the time the page hydrates.

**Two the browser tests found, both in the new modal:** the full-screen panel was a grid item at
`height: 100%` and a grid track sizes to its content, so a tall photograph pushed the zoom controls
off the bottom of the screen; and the focus trap never armed, because the `@if` and the view query
settle in the same render and the effect read only `open()`, so it ran once while the panel was
still undefined and gave up. Both fixed. A third was in a test, not the code: a helper looked for
`[role="dialog"]` and found the shell's permanently mounted, hidden mobile nav drawer.

**Deliberately absent:** Add to Bag and Buy Now (bag API is W2-09), the real wishlist (W2-08 — the
heart is the same per-browser `LocalWishlist` the card uses), reviews (PDP-08, W2-06) and Quick
View (QV-01, W2-04). Nothing dead or disabled stands in for any of them.

**Numbers:** storefront unit 312 (was 220), Playwright 112 (was 65), shared 19, admin 9; backend
232 + 146, 0 skipped. Lint and all builds clean, no new runtime dependency.

**Next:** the tester.

### 2026-09-23 (5) — W2-03 fix round 3: a blocker the tests were built to miss

Round-2 work verified on the wire. The tester then found a blocker that had been on the branch
since round 1 and that he had missed twice himself, because the tests could not see it.

**Blocker — a shared, bookmarked or reloaded deep listing rendered duplicates.**
`toRestoreRequests` shrank its last request to the rows still wanted, but the API's offset is
`(Page - 1) * PageSize` against *that request's own* page size (`ProductListQuery.Skip`), so a
smaller last page moved its window backwards instead of trimming it. `?pages=5` asked for rows
25–48 where it meant 97–120. Measured on a 399-product listing: `?pages=5` rendered 120 cards of
which 96 were distinct, `?pages=17` said "Showing 408 of 399". Only the restore path — which is the
entire reason `pages` is in the URL — and only when the page count was not a multiple of four.

One page size for the whole restore now, `min(wanted, MaxPageSize)` rather than always the cap: a
fresh listing wants 24 rows, and asking for 96 would fetch four times what it shows on the most
common page view in the storefront. Verified by hand at 1, 2, 4, 5, 6, 7, 17 and 20 pages: every
one distinct, every count line honest.

**The test lesson is the real finding.** `flushAll` answered every request with the rows the
component *expected* — 96 at a time, numbered from the request's index — regardless of the `page`
and `pageSize` actually sent, so it could only ever confirm the component's arithmetic against
itself. Worse, a unit test asserted the page sizes summed to exactly the rows wanted, which is not
a property of a correct restore: it is what *forced* the defect. The stub now answers from the
request, and the test asserts the honest property — the windows tile the listing with no gap and no
repeat. Both changes fail on the old code (5 tests red). **Lesson: a stub that fabricates what the
caller expected tests nothing; answer from the request, and assert a property the implementation
could not have chosen for itself.**

**Minors** — `usable()` now requires a department with a non-empty string `slug`, so
`[{"nope":1}]` falls back too; "Wishlist, 1 items" pluralises; the flaky ceiling test was
root-caused rather than guessed at a third time (see below).

**The ceiling test flake, measured.** The guess was that the `activated()` helper's polling loop
ran up to fifty change-detection passes. It does not: instrumenting it showed activation completes
on the first attempt every time, across three full runs. The real cost is the test itself —
~30 ms to navigate and **~650 ms to render 480 product cards** in jsdom, which is work the
assertion genuinely needs, against vitest's 5 s default. A machine running the backend suite
alongside ate that seven-fold margin twice in thirty-two runs. The polling loop is gone anyway
(it was the wrong shape and a latent hazard), and the three tests that render hundreds of cards
carry a stated 20 s budget with the measurement written next to it.

**Numbers:** 220 frontend unit tests (round 2: 212), 65 Playwright tests (60), backend 232 + 146,
0 skipped.

**Operational note — the `SeoTitle` fix does not reach already-seeded databases.** The seeder skips
a department whose slug exists, which is the right policy for an admin-editable field, so any
database seeded before this branch still reads `Kids's clothing`. That is every developer's
`SpencerDenim_Dev` (all three worktrees) and any seeded staging. Integration tests build a fresh
database per run and are unaffected. The one-off correction:

```sql
UPDATE catalog.Departments
SET    SeoTitle = N'Kids'' clothing | Spencer Denim'
WHERE  Slug = 'kids' AND SeoTitle = N'Kids''s clothing | Spencer Denim';
```

**Filed against ADM-11, not built:** when the departments feed is unhealthy the storefront falls
back to the seeded tree, so a category that exists only in the live catalogue 404s with `noindex`.
Nothing can diverge today — the fallback is an exact copy of the seed and ADM-11 is not built — but
once admin can add categories, `tree$` should say whether the tree is live or fallback and the
missing-page route should answer **503 with `Retry-After`** rather than 404 + noindex while it is
the fallback: a crawler retries a 503 and keeps the URL, it drops a 404.

**Next:** back to the tester.

### 2026-09-23 (4) — W2-03 fix round 2: one regression the guards introduced

The tester verified every round-1 fix as genuinely fixed and **backed the 404 reasoning** — he read
the Angular source and the wire himself and confirmed `ServerRoute.status` matches by path pattern,
so it would have 404'd every category page. He failed the branch on one regression.

**Major, a regression from round 1.** `usable()` guarded only the `departments` signal; the route
guards read `tree$`, the raw pipeline, and `catchError` does not catch a *successful* response with
a useless body. `200 []` — what the API legitimately answers for a freshly migrated database
before anyone runs the seeder, and the README says seeding is off by default — sent every listing
URL to the home page while the mega menu still listed all three departments and all 29 categories.
The first boot of a new staging or production environment would have looked like that. `200
{"oops":true}` threw inside a guard and served Express's own 144-byte error page on every listing
URL. `map(usable)` now sits at the end of `tree$`, so the menu and the routes cannot disagree by
construction; the redundant guard on the signal is gone, and `trailTo` no longer trusts that a
department carries a `categories` array. Verified against both shapes: 206 kB pages with real
headings instead of a redirect and an error page. **Lesson: the defensive read has to sit where
every reader passes, not on the one reader you were looking at.**

**Minors** — the test guarding the Load More blocker was flaky (1 run in 14 on a loaded machine)
because `canMatch` puts an async hop before activation; it now waits for the router to put a
component on screen, and *not* by polling `HttpTestingController.match`, which takes the requests it
matches off the queue. 16 consecutive clean runs with the backend suite running alongside. The
canonical tag leaked across client-side navigations (home inherited the listing's) — it moved to a
`CanonicalLink` service the page clears on destroy. `/sale` and `/new-arrivals` showed waist sizes
28–36 twice, under "Women waist" and "Men waist", bound to the same value: two checkboxes with one
accessible name that ticked each other; with no department they are one "Waist" group now.
`CatalogReferenceDataSeeder` built `SeoTitle` as `$"{Name}'s clothing"`, which read
**"Kids's clothing"** on every Kids listing — the title is seed data now, not a string operation.
That last one touches the catalogue branch's data and is flagged for the PR.

**Left as decided, not built:** a mistyped department redirecting home while an unknown category
404s (`/bag` and the footer paths share that `**` route until GLB-10 lands); the 1.77 MB document at
the Load More ceiling; `/wishlist` redirecting home until W2-08.

**Numbers:** 212 frontend unit tests (round 1: 203), 60 Playwright tests (57), backend 232 + 146,
0 skipped. Lint and all builds clean.

**Next:** back to the tester.

### 2026-09-23 (3) — W2-03 product listing: tester FAILED it, developer fix round 1

The tester rejected the branch on 2 Blockers, 4 Majors and a set of Minors. All fixed on
`feature/W2-03-product-listing`.

**Blockers**
- **Load More past the ceiling collapsed the listing.** `readLoadedPages` dropped an out-of-range
  value back to 1, and the button was still offered at the cap, so one more click took a listing of
  480 products down to 24. The count is now clamped at both ends and the button is hidden at the
  ceiling, with a line saying why.
- **`/:department` swallowed every single-segment path.** Eleven live header and footer links —
  `/bag`, `/account`, `/wishlist`, `/contact`, `/privacy` and the rest — rendered the listing's
  not-found state at HTTP 200, telling the shopper a *department* was missing when they had clicked
  "Bag". A `canMatch` guard now accepts only a slug the department tree knows, so everything else
  falls through to `**` and the home page as before.

**Majors**
- `/new-arrivals` and `/sale` are real listings now (DEPT-02/DEPT-03 happy path), each with its own
  heading, title and meta description, so no permanent navigation entry points at a missing page.
- An unknown category (`/women/shirts`, a Men's category) rendered as the department page with
  "0 products". It is now a genuine **404** — status set from the component through `RESPONSE_INIT`,
  because a server route's `status` is matched by path and no pattern can express "a category this
  department does not have". `noindex` with it, and listings now carry a relative `<link rel="canonical">`.
- Angular's HTTP transfer cache was still on and serialising every SSR response into the page under
  a key the browser cannot use — 23.8 kB of dead JSON on a department listing, each entry carrying
  `API_INTERNAL_URL`. Off (`withNoHttpTransferCache`); `ServerHandover` already hands the two
  catalogue calls over explicitly.
- The department feed had `catchError` but no timeout, so a 12 s API gave every page a 12 s TTFB.
  2 s, then the seeded fallback.
- The wishlist heart saved the product and left the header reading "Wishlist, 0 items". Wired, and
  taken up only after the first render so hydration is not asked to grow the header.

**Minors:** a reversed price range drops both bounds (no chip that contradicts the URL); no products
request at all on a 404; literal font sizes replaced with the type-scale tokens; the filter panel is
rendered once instead of once per container (202 inputs in the DOM, doubled in the HTML); sizes are
grouped into real `fieldset`/`legend` scales and only the scales a department stocks; a card's
colour summary names the family ("Ecru (Cream)"); README §4 documents that the built SSR server does
not proxy `/api`.

**Numbers:** 203 frontend unit tests (was 174), 57 Playwright tests (was 42), backend unchanged at
232 + 146, 0 skipped. Lint and all four builds clean.

**Still open, filed rather than built:** the facets endpoint (the filter option lists are still a
copy of the seed data, and the API cannot filter a size by scale), and the
interceptor/transfer-cache ordering in the shared SSR setup.

**Next:** back to the tester.

### 2026-09-23 (2) — W1-02 fix round 2: tester verified round 1, failed it on two more Majors

The tester confirmed all nine round-1 fixes as real and rejected the branch again.

**Fixed**
- **Major, a regression from round 1's relevance fix.** Moving `SoldQty30d` out of the additive score into its own `ThenByDescending` is what stops popularity crossing a band — keeping the cap of 9 as well made the key inert: every product selling 9+ clamped to the same value, so the real tie-break became `ThenBy(Id)` and `?q=denim` served 45 sales above 51. Cap deleted, the key is the raw column. Lesson: two changes that each solve the same problem, made in one step, and only one of them was needed.
- **Major, pre-existing and missed in round 1.** `IsActive` was honoured in the menu and in filter resolution but not in the product query, so deactivating a category left its products in the listing and in search with a breadcrumb into a category that answered nothing. A product is now on the storefront only while its **department and primary category** are active; secondary category links do not keep it visible. Written into doc 08 §7.1.
- **Minor** — the product slug rule moved to `Domain.Catalog.CatalogSlug` and guards `Product.Slug` on write, so a slug the PDP refuses cannot be persisted through the app (the matching `CK_Products_Slug` is a **W1-03** migration, on the board); the category-ambiguity guard now fires on the match count alone, since two categories under different parents of one department can share a slug and naming the department does not tell them apart; `MEMORY.md`'s Current state corrected; the seeder determinism test draws the real variant count instead of 15.

**Numbers:** build clean, 232 unit + 146 integration green, 0 skipped (round 1: 215 + 141).

**Next:** back to the tester.

### 2026-09-23 (1) — W1-02 catalogue foundation: tester FAILED it, developer fix round 1

**Context:** `feature/W1-02-catalogue-foundation` (worktree `worktrees/sd-catalog`) delivers the catalogue schema + demo seeder + the read-only storefront catalogue API. An independent tester rejected it with 3 major and 5 minor findings plus a board correction. All fixed on the branch; not pushed, no PR.

**Fixed**
- **Major — 500 on an unparseable query parameter.** `ProductListRequest` bound `int?`/`decimal?`/`bool?` straight from the query string, so `?page=abc`, `?minPrice=abc`, `?inStock=yes` and friends threw in model binding: a 500 with an ERROR log on a public endpoint. Those parameters bind as `string?` now and are parsed in `ProductListQuery.Create` with `InvariantCulture`, so an unreadable value is the same 400 naming the parameter that an out-of-range value already got. Flags accept `true`/`false` and `1`/`0`, and nothing else — a typo is named, not silently read as false.
- **Major — the "deterministic" seeder was not.** `DemoCatalogueSeeder` seeded its generator from `string.GetHashCode`, which .NET randomises per process, so seeded stock differed run to run (≈3% of CI runs failed on the partly-sold-out product) and `Math.Abs(int.MinValue)` could throw. The seed is now the first four bytes of the product code's SHA-256 digest, read unsigned — the same hash `DeterministicGuid` already used.
- **Major — an ambiguous `category` slug served one department silently.** Slugs are unique per `(DepartmentId, ParentCategoryId)` (doc 08 §3.2), so `?category=jeans` with no `department` matched Women's and Men's jeans and returned only the first. It is a 400 now, naming the departments to choose from. One match still works with no `department`.
- **Minor** — the product slug is pattern-validated (a malformed slug is a 400, and only the validated slug is echoed into the 404); all four catalogue endpoints answer HEAD as well as GET; `ProductListQuery.ToCacheKey` deleted with its self-comparing test (caching and facets belong to the listing-page task); search relevance rewritten to doc 08 §7.6's actual bands with the `SoldQty30d` term bounded to 9 so it can only break ties.
- **Minor** — doc 08 §1.6's permitted-cascade list now names `ProductStats`, `ProductSearchIndex` and `ProductColours` (all three were already in the model and the migration; only the first two were in the tester's report). No new migration. The model test now asserts the list and the model match **both ways**.
- **Board** — W1-02 was titled "Departments & categories (create/edit/reorder) — ADM-11", an admin write feature this branch never touched. Retitled to what it delivers; ADM-11's admin screens are now **W1-12 (TODO)**.

**Numbers:** build clean, 215 unit + 141 integration tests green, 0 skipped (baseline before the fixes: 180 + 111).

**Next:** back to the tester agent. Facet counts and the 60 s response cache of doc 08 §7.5 are deliberately **not** in this slice.

### 2026-09-21 (1) — Multi-session coordination, status review, W1-01 started
**Context:** first session on a **new machine** (global git identity `nuwank`/scienter.lk — different from the `SCIENTER\sachithi` PC used since 2026-09-15). Confirms this project is now being worked from more than one machine/session.

**Found**
- `MEMORY.md`/`TASKS.md` had drifted from the repo since 2026-09-17: W0-09 (scaffold) was committed and CI went green on 2026-09-17 but `TASKS.md` still read `TODO`; W0-08 was still `IN PROGRESS` although CI now exists. README defers Docker to a task "W0-15" that didn't exist on the board.
- This machine had **no repo-local git identity** (resolved to the machine's global `nuwank` identity) and **no push-safety hook active** (`core.hooksPath` unset) — both required by `docs/09` and would have let a push go out under the wrong identity/unchecked.
- No `gh` CLI on this machine, so `gh auth status` can't be verified here — per the non-negotiable git rule, **no push from this machine** until that's fixed.
- The `client-progress` source folder and the DPAPI FTP credential (both machine-local) aren't present here either — the client progress page can't be republished from this machine.

**Done**
- Fixed repo-local identity (`MASACHITH` / `sachithishara1@gmail.com`) and `core.hooksPath` (`tools/git-hooks`) on this machine — repo-local only, global config untouched and verified unchanged.
- Added [docs/14-multi-session-coordination.md](docs/14-multi-session-coordination.md): start/end-of-session steps, a machine-readiness check, and a known-machines log (both machines listed). Linked from the root `CLAUDE.md` and from this file.
- Corrected `TASKS.md`: W0-08 and W0-09 → `DONE` (with the branch-protection caveat noted), added **W0-15** (Docker packaging, still blocked on W0-05) and **W0-14** (this coordination fix). Claimed **W1-01** as `IN PROGRESS` (this session).
- Reviewed overall status for the user: Week 0 setup essentially done, no feature code yet, Week 1 (19–25 Sep) three days in with nothing started before today.
- Kicked off **W1-01 vertical slice 1** (admin login + RBAC foundation + audit log on auth events) via a developer agent, per doc 03 §3a's approved design: ASP.NET Core Identity, HttpOnly admin cookie scheme (no JWT), permission-based `RequirePermission` policies. Scope explicitly **excludes** from this slice: full Users/Roles management UI, 2FA enrollment (baselined for Super Admins, doc 08), and an audit log viewer — flagged as follow-up. A separate tester agent reviews it next, per the developer → tester rule; loop back to developer on any failure before this is marked done in `TASKS.md`.

**Next:** tester agent on the W1-01 slice → fix loop if needed → mark W1-01 (partially) DONE and open the follow-up tasks (Users/Roles CRUD, 2FA, audit log viewer) → W1-02 categories. Get `gh` installed on this machine (or continue pushing only from the original PC) before this machine pushes anything.

### 2026-09-21 (2) — Dev environment wired to a real SQL Server instance
The user gave a running SQL Server's connection details in chat (host/port/sa credentials) to set up as this machine's dev database, in place of LocalDB.

**Found:** no database-provisioning script existed anywhere in the repo. `AppDbContext`'s remarks and doc 08 §1.4/§10.1 (N12) require the collation to be set at `CREATE DATABASE` time and never altered by EF Core — `SqlServerDatabaseFixture` already does this itself for ephemeral integration-test databases, but nothing did it for a persistent named database.

**Done**
- Verified the server (SQL Server 2022) is reachable and the credentials authenticate, before storing anything.
- Added [tools/db/provision-database.sql](tools/db/provision-database.sql): idempotent `CREATE DATABASE ... COLLATE Latin1_General_100_CI_AS_SC`, with a hard failure (not silent) if a database of that name already exists with the wrong collation. Ran it to create `SpencerDenim_Dev` on the provided server; confirmed online with the correct collation via `sys.databases`.
- Set `ConnectionStrings:Database` via `dotnet user-secrets` (already scaffolded, `UserSecretsId` in `SpencerDenim.Api.csproj`) to point at that database — **not** written to any `appsettings*.json`, per the explicit warning in `DatabaseOptions.cs`. Never committed, never printed into a repo file.
- Set `TEST_SQL_CONNECTION` (server-only, no database — `SqlServerDatabaseFixture` picks a fresh database name per test run) as a Windows user-level environment variable on this machine, so the integration test suite runs for real here instead of skipping.
- Documented this as machine-specific state in [docs/14](docs/14-multi-session-coordination.md)'s known-machines table (host/port only, no password — a session on another machine sets its own).

**Not done:** did not run the full `dotnet build`/`dotnet test` suite against it, since the W1-01 developer agent was actively editing backend code at the same time — that verification happens once that slice lands (its migration should target this database next). LocalDB (150 and 170), `sqlcmd`, and `dotnet-ef` 10.0.12 all turned out to already be present on this machine, corrected from "not checked" in doc 11.

### 2026-09-21 (3) — W1-01 slice 1: developer done, tester FAILED it, back for a fix
**Developer agent finished** the login/RBAC vertical slice: ASP.NET Core Identity, admin cookie scheme, permission-based authorization (`RequirePermission` + cached-by-SecurityStamp handler), RBAC schema matching doc 08 exactly, audit log on login attempts, gated/idempotent Super Admin seeder, login/logout/whoami endpoints, Angular login page, one EF migration (`20260921160620_InitialIdentityAndAudit`). Its own numbers: build clean, 51/51 unit tests, 16/16 integration tests (LocalDB), 18/18 frontend tests, lint clean, prod build OK. It found `tools/db/provision-database.sql` (from session (2) above) already on disk, correctly left it alone per doc 11, and used it to fix its own LocalDB database's collation.

**Tester agent (separate, per the non-negotiable process) independently reproduced all of that** — including re-running the integration suite against **both** LocalDB and the real SQL Server at `127.0.0.1,11433` via `TEST_SQL_CONNECTION` — and additionally verified the migration schema against doc 08 line-by-line, the antiforgery/cookie/lockout/seeder logic by reading and exercising the code, and the layer-dependency test's real teeth. Verdict: **FAIL.**

**Defect (Major, confirmed by measurement, not just code reading):** `AdminAuthenticator` returns early for an unknown/non-staff/inactive account **before** running the password hash check, so response time leaks whether an email belongs to an admin account — measured ~10–30 ms for unknown emails vs ~150–220 ms for a real account with a wrong password. A username-enumeration timing oracle on the admin login endpoint.

Two Minor items also found: the antiforgery cookie doesn't force `Secure` (inconsistent with the auth cookie, masked behind Caddy/HTTPS in prod but worth fixing for symmetry), and no Angular route guard yet on the dashboard (explicitly a next-slice item, not a security issue since every real endpoint is server-side cookie-gated).

**Done (this session, orchestrator):**
- Fixed the `tools/db/provision-database.sql` `-v DbName` bug myself (unrelated to the developer's slice — a `:setvar` in the script always wins over a command-line `-v` override, so the hardcoded default was removed and the variable is now required). Verified with a throwaway database, then dropped it.
- Sent the Major defect (and the cheap-to-fold-in Minor antiforgery-cookie fix) back to the **same developer agent**, per the non-negotiable fix-loop rule. Told it to re-verify with an actual timing measurement, not just a functional test, and left the route-guard item for a later slice.
- Marked `TASKS.md` W1-01 as `FAILED TEST` rather than `IN PROGRESS`, so a later session sees the real state.

**Round 2:** developer fixed both — re-measured timing itself (now within noise) and added `CookieSecurePolicy.Always` to the antiforgery cookie to match the auth cookie. Sent to the tester for a focused re-check (not a full re-review).

**Tester round 2: FAIL again, but only on the second item.** Timing fix confirmed independently (own re-measurement, own methodology, varied usernames, all three early-return branches in `AdminAuthenticator.cs` checked) — genuinely fixed, doesn't need touching again. But the antiforgery `SecurePolicy = Always` change **regressed**: `GET /api/admin/auth/antiforgery-token` now throws (`AntiforgeryOptions.Cookie.SecurePolicy = Always` on a non-SSL request) whenever the API is reached over plain HTTP — which is this project's **own default dev launch profile** (`launchSettings.json` has no HTTPS profile) and, per doc 03 §4's deployment topology (Caddy terminates TLS, proxies to the API over plain HTTP internally, no `UseForwardedHeaders` configured in `Program.cs`), likely how it runs in production too. Since the Angular admin app fetches this token before calling `logout`, **logout breaks** under the project's own normal running conditions, not just in some edge case. The integration test suite didn't catch it because `AdminAuthEndpointTests.CreateClient()` hardcodes `BaseAddress = "https://localhost"`, which fakes `IsHttps = true` regardless of the real fix — the test harness itself was masking the exact bug class it needed to catch.

**Round 2 fix:** developer went further than a patch — added real `UseForwardedHeaders` handling (new `Api/Http/ForwardedHeadersSetup.cs`) plus an `AdminSecurityOptions.RequireSecureCookies` toggle (default `true`, only `appsettings.Development.json` relaxes it) so both cookies' secure policy is environment-correct, and added a genuine plain-HTTP end-to-end test closing the harness gap that hid the original bug.

**Tester round 3: FAIL again — a new, concretely-exploitable defect found by scrutinizing the trade-off, not just re-checking the fix.** The plain-HTTP round-trip is confirmed fixed live (real `curl`, real dev profile). But `ForwardedHeadersSetup.cs` clears `KnownIPNetworks`/`KnownProxies` entirely, trusting `X-Forwarded-For`/`X-Forwarded-Proto` from *any* direct caller, on the justification that "only Caddy can reach the API container" — a topology that **doesn't exist yet** (W0-15 Docker/Caddy is still TODO). The tester proved this is exploitable today, not hypothetical: sent a login request with a forged `X-Forwarded-For` header and confirmed the spoofed IP was written straight into `ops.AuditLogs.IpAddress` — defeating the forensic purpose of the very audit-log feature (ADM-03) this slice implements, with zero authentication required.

**Round 3 fix, and the developer improved on the suggested fix:** pinning `KnownIPNetworks` to loopback alone turned out not to close the hole — loopback is exactly how `curl`/`dotnet run`/CI/tests connect, so that "restriction" wasn't restrictive at all against the tester's own repro (the developer proved this empirically before deciding). Instead it **disabled `X-Forwarded-For` processing entirely** (kept `X-Forwarded-Proto`, which is the one the round-2 fix actually needs and never touches the audit trail), so `RemoteIpAddress` always reflects the real socket peer regardless of any header. Verified with the same before/after repro the tester used: forged header before → forged IP in `ops.AuditLogs`; after → real peer IP (`::1`) recorded, forged header ignored. Full plain-HTTP round trip re-confirmed still working. A regression-guard unit test was attempted but hit an unrelated machine-specific toolchain issue (`Microsoft.AspNetCore.HttpOverrides` types not resolving in the plain-SDK test project on this machine); reverted cleanly rather than land something fragile, relying on the manual repro instead.

**Tester round 4: PASS.** Independently re-ran the exact forged-header repro live (audit row now shows the real peer, not the forged one), re-ran the full plain-HTTP round trip by hand (200/200/200/204/401), rebuilt and re-ran the suite (51/51 unit, 17/17 integration, clean), and confirmed `git diff --stat` shows only `ForwardedHeadersSetup.cs` changed this round with no stray artifacts from the reverted test attempt. **W1-01 slice 1 is DONE** — no further rounds needed on this defect line.

**Closed out (this session, orchestrator):** marked W1-01 `DONE` in `TASKS.md` as slice 1 of the full original scope, with the 3-round fix history recorded inline so a later session doesn't need to re-read this whole log to know why it took 3 rounds. Opened four follow-up tasks for what slice 1 deliberately left out or carried forward: **W1-08** (Users/Roles management CRUD, wiring `RoleAssignmentPolicy` to a real caller, seeding the remaining 3 default roles), **W1-09** (audit log viewer), **W1-10** (2FA enrollment for Super Admins), **W1-11** (Angular route guard, cookie `SecurityStamp` revalidation loop, the forwarded-headers regression test blocked by the toolchain issue). Added a note to **W0-15** (Docker/Caddy, still TODO) that it must re-enable `X-Forwarded-For` and widen `KnownIPNetworks` to the real Docker bridge subnet once that topology exists — currently deliberately disabled/loopback-only.

**Overall shape of this task:** three fix-loop rounds, each catching a real, independently-confirmed Major defect (login timing side-channel → antiforgery regression breaking logout over plain HTTP → forgeable audit-log IP address) — the non-negotiable developer→tester process did exactly what it's for here. This took far longer than a typical first slice; the user/architect should weigh that against the Week 1 schedule when deciding whether to pick up W1-08–W1-11 now or move to **W1-02** (categories) first.

### 2026-09-18 — End of day: where to resume
**State**
- `main` = `W0-09 scaffold` + `fix(publish)` pin rotation (PR #1, #2 both merged, CI green).
- **Open branch `feature/W1-02-catalogue-foundation`** (pushed, no PR): 4 commits — catalogue domain, EF mapping + migration + seeders + read-only catalogue API, unit/integration tests, an SQL-command-count test. **Not yet tested by a tester agent** (the developer agent's final report never arrived; its work is committed and the tree is clean).
- **Open branch `docs/W0-14-business-processes`** (pushed, no PR): `docs/11-business-processes.md` (17 processes, WooCommerce-informed, 36 gaps), docs/06 §4 + docs/09 §5b updates, TASKS/MEMORY. **Independent review was still running when the day ended**; its findings are not yet applied.
- Client progress page published 2026-09-18: 6% overall, 2 done, 3 in progress, 49 planned.

**Resume tomorrow, in this order**
1. `git switch feature/W1-02-catalogue-foundation`; re-run the developer's verification (build, `dotnet test` with `TEST_SQL_CONNECTION`, migrate+seed `SpencerDenim_Dev`, exercise the catalogue endpoints), then hand it to a **tester agent**; fix → PR → squash merge.
2. Re-run the **doc 11 review** (the earlier run's findings were lost when the day ended), apply the fixes, then PR the docs branch and merge.
3. Then the next build task: **storefront pages** (home, listing with filters, product page) on the real catalogue API, or **admin auth + catalogue screens** — the user still has to choose the order.
4. Small tasks queued: **W0-16** update GitHub Actions to current majors (Node 20 deprecation warning on every run), **W0-15** Docker packaging (blocked by the office firewall).

**Notes**
- Publishing the task sheet broke because Hostinger rotated its FTPS certificate to a Let's Encrypt `*.hstgr.io` one. `$PinnedKey` now accepts a list; refresh with `-ShowPin` (docs/10 §4).
- `SpencerDenim_Dev` on LocalDB currently holds the scaffold-era schema; the catalogue migration may still need applying.

### 2026-09-17 (2) — Scaffold merged, branching workflow adopted, no Docker
**Decisions (user)**
- **No Docker for now** (company firewall). Development runs directly on Windows; Docker packaging = W0-15.
- **Git best practice:** branch per task, Conventional Commits, PR + squash merge after tester pass. Documented in docs/09 §5b; worktrees for parallel agents.
- **WooCommerce** is the reference for standard e-commerce operations; our baselined design wins where they differ.

**Done**
- Docker Desktop fixed (`wsl --update --web-download` → WSL 2.7.14) but **not used**; a stuck Docker instance was blocking the UI and was closed.
- **W0-09 scaffold merged to `main` (`4efe767`, PR #1)**: .NET 10 clean-architecture backend (`/api/ping`, `/health`, `/health/ready`, Swagger in Dev only, EF `AppDbContext` with configurable compatibility level), Angular 21 workspace (storefront SSR, admin, shared tokens library), 20 unit + 8 integration + architecture tests, GitHub Actions CI. **First CI run green.**
- Local dev database: **LocalDB** `(localdb)\MSSQLLocalDB`, databases `SpencerDenim_*` only. `SpencerDenim_Dev` exists (empty, collation `Latin1_General_100_CI_AS_SC`). The default office SQL instance is never touched (the dev account cannot even log in to it).
- Integration tests use `TEST_SQL_CONNECTION` (skip with a reason when unset); CI uses a pinned SQL Server service container with a login-based readiness gate. Testcontainers dropped until Docker returns.
- QA found and the developer fixed: stale README status/paths, broken npm scripts, EF warning noise, missing shared-library and SSR coverage in CI, repeated CI password, and 5 CI first-run risks.

**In progress**
- `docs/W0-14-business-processes` branch pushed: `docs/11-business-processes.md` (17 processes, WooCommerce-informed, 36 gaps) under independent review; docs/06 §4 and docs/09 §5b updated.

**Open question for the user:** storefront first with sample data, or admin catalogue first (current plan)?

### 2026-09-17 (1) — Docker guides, hosting, task sheet publishing
**Done**
- PDF guides in `docs/guides/`: DEV-GUIDE-01 Docker Desktop on Windows (v1.1, 7 pp) and DEV-GUIDE-02 Fix "WSL needs updating" (3 pp). HTML sources are kept; built with headless Chrome + a pypdf footer stamp.
- Dev PC: Docker Desktop 4.91 installed **per-user** (CLI 29.8.0). WSL features on, but the **WSL 2 kernel is missing**. The user must run `wsl --update` as admin (DEV-GUIDE-02). Account `SCIENTER\sachithi` is not a local admin; the PC is domain-joined (scienter.lk).
- Hostinger shared hosting bought for the task sheet: https://colombowardrobecompany-build-tracker.kt-wedding.online. FTP host 147.93.101.68:21, user `u765465138.colombowardrobecompany-build-tracker.kt-wedding.online`, opens in `/public_html` (contained only `default.php`).
- FTP password stored DPAPI-encrypted at `%USERPROFILE%\.spencer-denim\tracker-ftp-credential.xml` (never in repo or docs). The user shared it in chat, so recommend changing it and running `-SetPassword`.
- `tools/publish-client-progress.ps1` (allow-list of 4 files, node validation, FTPS with pinned Hostinger key `sha256//rRBXAmuSvqI5e99CZPWQDXLPWIlTIvu77x/aZc349JQ=`, cert expires 2027-01-17, size + live verification, never deletes) + `docs/10-client-progress-publishing.md`. Dry run OK; wrong pin correctly rejected (curl 90).
- `client-progress/progress-data.js`: lastUpdated 2026-09-17; "Hosting, domain & security setup" in progress 25%; log entries for 16 and 17 Sep; overall 6%.

**Publishing**
- First attempts failed: FTP data connections reset or timed out (control on port 21 worked). Recorded in doc 10 §5b.
- **Published successfully on a later single retry** (same PC): all 4 files uploaded, sizes verified, live `lastUpdated` 2026-09-17 at https://colombowardrobecompany-build-tracker.kt-wedding.online. Hostinger serves `index.html` ahead of `default.php`. Live check in Playwright at 390 px: no horizontal scroll, no overflow. Hostinger shows a short "Checking your browser" bot check on first visit.
- The data-connection problem is **intermittent**. If publishing fails, wait and retry once later, or use hPanel File Manager.

**Decision (user, 2026-09-17):** Docker work is **paused for now**. Focus is on the client progress page publishing only. The WSL 2 kernel update (`wsl --update`) is still pending whenever Docker resumes.

### 2026-09-16 (2) — Client-item defaults accepted; plan for tomorrow
**Decisions (user accepted recommended defaults)**
- Style tile: all 9 recommendations (Inter only, round swatches/radios, uppercase labels, icon buttons on phone cards, WhatsApp green, hero text over photo with fade, 880 px hero cap, placeholder delivery values). Client still supplies the logo. Recorded in docs/04.
- PayPal charges USD (+3% buffer). VAT default: not registered (accountant confirms before go-live). Delivery: placeholder zone rates, couriers and pickup locations (real values before go-live). PENDING PayPal hold: 5 days. Recorded in docs/01 §3b and docs/08 §11.

**PC readiness checked:** Windows 10 Pro 22H2, virtualization ON, 16 GB RAM, C: 60 GB / D: 105 GB free, OpenSSH client present, **WSL not installed**. User installs `wsl --install --no-distribution` → reboot → Docker Desktop (WSL 2 backend, disk image on D:).

**Tomorrow (2026-09-17)**
1. Verify Docker (`docker run --rm hello-world`) → W0-09 scaffold (.NET 10 layers, Angular 21 storefront SSR + admin + shared, docker-compose, Caddy, GitHub Actions) + spikes B6 (EF complex types, collation). Developer → tester loop.
2. User brings Hostinger KVM 2 details (IP, Ubuntu 24.04 plain OS, hPanel access) + domain/registrar DNS access. **No passwords in chat**: generate an SSH key locally, and the user adds the public key in hPanel.
3. W0-11 VPS hardening (SSH keys only, UFW 22/80/443, fail2ban, unattended-upgrades, Docker) + DNS A records (@, www, staging) + Caddy HTTPS.
4. Client progress: "Hosting, domain & security setup" → In progress.

### 2026-09-16 (1) — GitHub repo set up (personal account)
**Done**
- Private repo https://github.com/MASACHITH/spencer-denim-ecommerce created by the user; pushed planning docs, style tile, DB design, `.gitattributes`, and `reference/` archive (old demo, client documents, prototype prompt). All commits authored and pushed as MASACHITH.
- Added `docs/09-git-and-repository-rules.md`, a project `CLAUDE.md` (git rule at top), a git rule in the root `CLAUDE.md`, and a pre-push hook `tools/git-hooks/pre-push` (repo-local `core.hooksPath`). The hook blocks any push whose URL isn't MASACHITH/spencer-denim-ecommerce or whose active `gh` account isn't MASACHITH. The URL check was tested (pass and block). The account check wasn't tested, to avoid switching to the office account.

**Incident / lesson**
- L6: the user was alarmed to see the office account `destinity-sachithm` in command output (a `gh auth switch` back after the push; nothing was pushed there). Rule: never switch to the office account unless asked; always state which account a push went to.

### 2026-09-15 (5) — Database design baselined
**Done**
- W0-10 **DONE**: `docs/08-database-design.md` rev 4 baselined (65 tables, 10 schemas). Path: author → review r1 (4 Critical, 9 Major) → rev 2 (63 tables) → r2 (1 Critical SMTP settings, 4 Major) → rev 3 (65) → r3 (1 Critical late PayPal COMPLETED lost, 2 Major) → rev 4 → spot-check BASELINE.
- Key decisions: derived stock reservations (no StockReservations table); `Settings.Security` GrantsAll-only (SMTP, security, legal retention); `Settings.Payments` for bank/PayPal; `Orders.Refund` separate; subset rule for Users.Manage; 2FA for Super Admins; PayPal charges USD (pending client); capture claim + recovery table + `CapturedRequiresRefund`; PENDING capture hold 5 days, cut back on DENIED; light delivery-zone model (`Districts.DeliveryZone` + `DeliveryMethodZoneRates`); PDPA anonymisation via set-based updates + audit scrub; collation `Latin1_General_100_CI_AS_SC`; SQL Express buffer pool 1,410 MB → ~2 GB budget.
- Docs 02/03 aligned with doc 08 (roles/security rules, backups every 15 min off-server, private uploads volume, background jobs, stock rules).
- Verified the author agent only touched doc 08 when its safety classifier was unavailable.

**Lesson**
- L5: large design docs must be written to disk section by section (a single-response doc hit the output limit and was lost). Reviews should run in rounds until no Critical/Major remain, then a targeted spot-check.

**Waiting on user / client** (unchanged): Docker Desktop, GitHub account, style-tile decisions, D1 PayPal USD, D2 VAT, D8 delivery zone rates/couriers/pickup locations, D9 retention, D13 PENDING hold length.

### 2026-09-15 (4) — Style tile done, DB design drafted
**Done**
- W0-07 style tile **DONE**: QA r1 FAIL (5 Major: missing `[hidden]` rule, focus lost under reduced motion, invisible focus ring on desktop card actions), QA r2 PASS. Lessons L2–L4 added to docs/06. Confirmed rules + carry-forward items added to docs/04.
- W0-10 DB design written: `docs/08-database-design.md` (76 tables, 10 schemas). First attempt failed (agent output limit), rewritten section by section. Independent architect review running.
- Client progress: "Design style guide" set to In progress 90% (awaiting client review); log entry added.

**Waiting on user / client**
- Docker Desktop install; GitHub account.
- Style tile decisions (logo, headline font, round swatches, uppercase labels, mobile card actions, WhatsApp colour, hero text placement + hero cap, real delivery values).
- DB open questions: PayPal charge currency / exchange rate, VAT registration, bank-slip upload, bank-transfer expiry (48 h default), verified buyer = Delivered?

### 2026-09-15 (3) — Week 0 build started
**Done**
- W0-06 client progress page built by developer agent (`client-progress/`: index.html, styles.css, app.js, progress-data.js). Data-driven, works from file://, 4% real status.
- Tester agent round 1: all 12 acceptance criteria pass. Defects D1–D6 found (invalid data entries crash sections, bad milestone dates show "Completed", 8 client-visible features missing, README with internal notes would be publicly served, impossible `?today=` dates roll over, "Banana Republic" named on client page). Sent back to developer.

- W0-06 **DONE** after 3 QA rounds (r2 found N1 runtime typo blanking page, N2 missing-progress warning, N3 empty header labels; r3 PASS). Guide G1 wording (comma when adding after last group) fixed directly in docs/07.
- Lesson: data files edited by non-developers need defensive loading + a plain-language error guide. Apply the same to any admin-imported data later.

**In progress**
- W0-07 style tile (developer agent) → `spencer-denim-ecommerce/design/style-tile/`.
- W0-10 database design (architect agent) → `docs/08-database-design.md`.

**Decisions**
- Team-only instructions for updating the progress page live in `docs/07-client-progress-update-guide.md`, not in the uploaded `client-progress/` folder.
- No third-party brand names on client-facing pages.

### 2026-09-15 (2) — Decisions applied
**Done**
- Applied answers Q1–Q5 + recommendations Q6–Q16. Scope baseline v1.0 (all demo features kept, RBAC admin, media library with video, PayPal last).
- Rewrote docs 01, 02, 05, TASKS. Updated 03 (RBAC, media, payments, toolchain) and 04 (no accent colour).
- Replaced root `CLAUDE.md` with pointer to this project.
- Verified toolchain: .NET 10.0.201, Node 24.13.1, Angular CLI 21.1.4, git, gh. **Docker missing.**

**Next**
- Install Docker Desktop. Build client progress page, style tile, scaffold.

### 2026-09-15 (1) — Kick-off / discovery
**Done**
- Read signed proposal PDF, reference.txt (Banana Republic), content checklist, draft proposals, prototype brief, demo home/admin/checkout, Bug screenshots. Fetched Banana Republic homepage.
- Created `spencer-denim-ecommerce/` with README, docs 01–06, TASKS, MEMORY.

**Found**
- PayPal receive limitation in Sri Lanka (risk). Proposal date conflict (now resolved). Out-of-scope demo items. Proposal omissions (PDP/cart/search/emails/policies/delivery fees). Root CLAUDE.md outdated. No git repo.
