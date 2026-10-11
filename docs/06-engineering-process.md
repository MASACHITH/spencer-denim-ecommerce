# 06 — Engineering Process

Last updated: 2026-09-15

## 1. Roles (15-year senior standard)

| Role | Owns |
|---|---|
| **Architect** | Design decisions, module boundaries, data model, security, Azure-portability. Reviews any change that crosses layers. |
| **Senior developer** | Implements tasks with SOLID and clean architecture. Writes unit and integration tests alongside the code. |
| **Tester (QA)** | Independent verification: acceptance criteria, E2E, responsive, accessibility, security smoke tests, regression. Can reject a task. |

When working with Claude Code, each role runs as a separate agent so the tester never grades its own work.

## 2. Task loop (every task)

```
TASKS.md item (with acceptance criteria)
        │
        ▼
 [Architect] ── only if the task touches schema / cross-module / security ──┐
        │                                                                  │
        ▼                                                                  │
 [Developer agent] implement + unit/integration tests + self-check  ◄──────┘
        │
        ▼
 [Tester agent] verify against acceptance criteria
        │
   ┌────┴────┐
 PASS       FAIL ─► defect report (steps, expected, actual, screenshot, breakpoint)
   │                  │
   │                  └─► back to Developer agent (with report) ─► Tester again
   ▼
 Update TASKS.md (Done) → MEMORY.md (what changed / decisions) → client progress data
```

A failure is never patched by the tester. The same defect failing **twice** triggers a root-cause review (architect) before a third attempt.

## 3. Definition of Done

A task is **Done** only when all of these hold:
- [ ] Acceptance criteria in `TASKS.md` met
- [ ] Unit + integration tests written and green. Build has no warnings-as-errors failures.
- [ ] Tester agent passed it, including:
  - [ ] Breakpoints 320 / 375 / 390 / 768 / 1024 / 1280 / 1440 / 1920: no horizontal scroll, no clipped/overlapping UI
  - [ ] Keyboard navigation + axe accessibility check (no serious/critical issues)
  - [ ] No browser console errors. No failed network requests. No broken images.
  - [ ] Error and empty states checked (not only the happy path)
- [ ] Security: authz on every admin endpoint, input validated, no secrets in code
- [ ] Deployed to staging and smoke-tested
- [ ] `TASKS.md` and `MEMORY.md` updated

## 4. Test strategy

| Level | Tooling | Covers |
|---|---|---|
| Unit | xUnit (backend), Angular test runner (frontend) | Domain rules: order state machine, pricing, promo/sale precedence, stock reservation |
| Integration | xUnit + `WebApplicationFactory` against a **real SQL Server** given by `TEST_SQL_CONNECTION` | API endpoints, EF mappings, auth, PayPal webhook handling (sandbox/mocked) |
| E2E | Playwright | Critical journeys: browse → filter → PDP → cart → checkout (PayPal sandbox, bank transfer) → admin approves → customer sees status + tracking link |
| Responsive | Playwright at all 8 breakpoints + screenshot diffs | Every storefront and admin page |
| Accessibility | axe-core in Playwright | WCAG 2.2 AA |
| Performance | Lighthouse CI on home, PLP, PDP | Mobile LCP < 2.5 s, CLS < 0.1 |
| Security | OWASP ZAP baseline on staging, dependency audit | Before go-live |

**Scratch scripts and ports during verification.** The temporary directory and the machine's
ports are shared by every session, and a script at a familiar path or a port that answers is not
proof that either is yours — both have already gone wrong on this project. Private uniquely named
directory, scripts written fresh and hash-checked immediately before running, per-session ports
verified free: [14](14-multi-session-coordination.md) "Scratch scripts and ports are shared too".

**Integration-test database (decided 2026-09-17, W0-09).** Testcontainers is **not** used: the office firewall blocks Docker. Instead:
- **Locally:** set `TEST_SQL_CONNECTION` to LocalDB (`Server=(localdb)\MSSQLLocalDB;Trusted_Connection=True;TrustServerCertificate=True`). The fixture creates a throwaway `SpencerDenim_Test_<guid>` database with collation `Latin1_General_100_CI_AS_SC` and drops it afterwards. Never point tests at the default SQL Server instance; it may hold office databases.
- **Without the variable:** SQL-backed tests **skip** with a clear reason instead of failing.
- **In CI:** GitHub Actions runs them against a SQL Server service container on GitHub's own runners, where the firewall doesn't apply.
- Testcontainers can return once Docker is available (task W0-15).

## 5. Coding standards

- SOLID + clean architecture ([03](03-architecture-and-tech-stack.md)). Feature folders. No business logic in controllers or components.
- Nullable reference types on. `async` all the way. `CancellationToken` passed through.
- Angular: standalone components, signals, strict TypeScript, OnPush, shared design tokens only (no hard-coded colours).
- Money is `decimal`, never `double`. Dates are UTC in the DB, displayed in Asia/Colombo.
- Configuration via environment variables only (Azure-ready).
- Small commits referencing task IDs (e.g. `ADM-08: approve bank transfer`). Branch per task, merge to `main` after tests pass.

### 5a. Adding a permission (issue #122, doc 08 §9 N13)

A new permission value reaches the default roles (Store Manager, Order Staff, Content Editor) on a **fresh database only**: the seeder never touches a role that already exists, so that a Super Admin's own edits are not overwritten on every deploy. On any database that already exists, including the client's live one, their staff silently lack it: the screen is there and the button refuses. Super Admin is unaffected, because it is evaluated rather than enumerated.

If a default role should hold the new permission:
1. Add it to that role in `DefaultRoleDefinitions` (what a fresh database is seeded with).
2. Add a `DefaultRolePermissionIntroduction` to `DefaultRoleSeedManifest.Introductions` at **`CurrentVersion + 1`** (what existing databases are brought up to). Never at a version that has shipped, and never by editing an entry that has. `DefaultRoleSeedManifestTests` fails if step 1 is done without step 2.
3. Say in the PR which roles gained it, and which did not and why.

On every start the reference-data phase runs `DefaultRolePermissionSync` (so it is **not** behind `Seed:Admin:Enabled`, which is switched off after go-live). It reads the version this database has been brought up to (`ops.Settings` key `Seed.Version`; absent means the baseline, version 1) and, in one transaction behind an application lock, adds to each default role that exists only the permissions introduced after it, then records the current version. It never removes a permission, never creates a role, never touches Super Admin or a custom role, and never re-adds what a Super Admin removed after the version that granted it. Each role that gains a permission gets a `PermissionChange` audit row (no acting user), and its holders' `SecurityStamp` is regenerated so the permission cache sees the change on the next request.

**What to look for in the log** (category `DefaultRolePermissionSync`, once per start, never per request):

| Line | Level | Meaning |
|---|---|---|
| `Default roles are in step with the shipped definitions: 3 of 3 default role(s) checked at default-role seed version N` | Information | Nothing differs. The normal line. |
| `Default role '…' was granted K permission(s) added after default-role seed version A (now at B): …` | Information | Expected once, on the first start after a release that bumped the version. |
| `Default role '…' does not hold K permission(s) its shipped definition includes: …` | Warning | The version that introduced them was already applied, so they were **not** granted again: they were removed after the fact. Grant them by hand if that was not intended. |
| `Default role '…' holds K permission(s) its shipped definition does not include …` | Information | A Super Admin added them. Left alone. |
| `Default role '…' does not exist …` / `A role named '…' exists but is not a seeded system role …` | Information / Warning | Not created / not touched. |
| `Could not reconcile the default roles' permissions with this build.` | Error | Nothing was changed. Retried on the next start; the shop is unaffected. |

**Launch-day check** (and after every release that bumped the version): after the first start on the live database, (1) `SELECT [Value] FROM ops.Settings WHERE [Key] = N'Seed.Version'` equals `DefaultRoleSeedManifest.CurrentVersion`; (2) the log has the first or second line above for the default roles, and no Error line; (3) sign in as a Store Manager and a Content Editor and open one screen guarded by the newest permission. If the step failed, the safety net is to insert the missing `identity.RoleClaims` rows (`ClaimType = 'perm'`) by hand from the release's manifest entries. The deployment runbook's launch-day list should carry these three checks.

A default role that is itself new is created only by the admin seed (`Seed:Admin:Enabled`), never by this step.

## 6. Lessons learned (apply to every future task)

| # | Lesson | Rule |
|---|---|---|
| L1 | Data edited by non-developers crashed a page on one typo (progress page QA r2) | Load editable data defensively and fail to a neutral message. Document the common mistakes in plain language. |
| L2 | A class with `display:` overrode the `hidden` attribute, so removed or empty-state elements stayed visible (style tile QA r1) | The global reset must include `[hidden]{display:none !important}`. Test every show/hide state visually, not only in the DOM. |
| L3 | Under reduced motion, a 0.01ms override on `visibility` meant `focus()` ran on a still-hidden drawer (style tile QA r1) | Move focus only after the element is visible (next frame or after transitionend). Test drawers and dialogs with reduced motion on. |
| L4 | A pseudo-element with `inset:0` painted over an inset focus outline (style tile QA r1) | Check the focus ring at every breakpoint. Avoid inset outlines on elements that have overlay pseudo-elements. |
| L5 | A large design document was produced in one agent response, hit the output limit and was lost (DB design, 2026-09-15) | Write long documents to disk section by section, never in a single response. |
| L6 | An architecture test that only inspected emitted assembly references missed an unused-but-declared dependency; the project-file check caught it (W0-09 QA) | Keep both checks: project references **and** assembly references. Prove a rule fails by deliberately breaking it in a scratch copy. |
| L7 | A permission added to a default role reached a fresh database only, so on every existing database the staff in that role silently lacked it (W2-11 added three; issue #122) | A permission added to a default role gets a seed version in `DefaultRoleSeedManifest` in the same change (§5a). The unit test that walks the definitions fails otherwise. |

## 7. Session discipline

Start of session: read `MEMORY.md` → `TASKS.md`.
End of session: update both, then update client progress data if any feature status changed.
