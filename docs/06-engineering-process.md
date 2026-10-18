# 06 — Engineering Process

Last updated: 2026-10-17

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
| Security | Dependency audit gate (npm and NuGet advisories, on every PR, below); OWASP ZAP baseline on staging | Audit gate: every PR. ZAP: before go-live |

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

**Dependency audit gate (decided 2026-10-17, #84).** A published advisory sat in the npm tree with nothing
watching for it (the same gap #65 recorded for `axe-core`: listed in this document, not enforced anywhere).
The gate is `tools/audit/audit-gate.mjs`; it runs as the `Dependency audit` job in `.github/workflows/ci.yml`
on every PR and every push to `main`, in about a minute and separate from the 35-minute backend job.

| Run it yourself | Does |
|---|---|
| `node tools/audit/audit-gate.mjs` (repository root) | Both ecosystems. Needs network (npm registry, api.nuget.org). The NuGet half runs `dotnet restore` first, with NuGet's own audit switched off, so it explains a finding instead of failing the restore. |
| `node tools/audit/audit-gate.mjs npm` or `npm run audit:gate` (in `src/frontend`) | npm only, from `package-lock.json`; no `npm ci` needed. |
| `node tools/audit/audit-gate.mjs nuget [--no-restore]` | NuGet only. |
| `node --test tools/audit/audit-gate.test.mjs` | The gate's own tests: every way it goes red, no network. |

It **fails** (exit 1) on any **high or critical** advisory (a severity it does not recognise counts as blocking)
that is not in [`tools/audit/audit-allowlist.json`](../tools/audit/audit-allowlist.json). Moderate and low are
printed, not gated. It also fails when an allow-list entry
- is malformed: advisory, package, a reason of at least 30 characters, who accepted it, `acceptedOn` and `reviewBy`
  are all required, both dates must be real, and `reviewBy` must be after `acceptedOn` by no more than 90 days;
- is **past its `reviewBy` date** (the last day it stands is `reviewBy` itself);
- is **stale**: its advisory is no longer reported at high or critical (fixed, withdrawn or re-rated). Delete it;
  a leftover entry would pre-approve the next advisory that reuses the id.

It **fails closed** (exit 2, never green) when an audit could not run or its report is not understood: offline,
a registry error, an unfamiliar report version, a NuGet restore or `list package` problem. `GITHUB_ACTIONS=true`
adds `::error` annotations.

When it goes red:
1. **Fix the dependency.** Look for a newer patch in the same major (`npm view <pkg> dist-tags`; `npm audit fix`
   without `--force`; a bumped floor in `package.json`). The Angular framework packages move as a set and the tooling
   (`@angular/build`, `cli`, `ssr`) is a separate release train. Then run both builds and the unit tests.
2. **Never** take `npm audit fix --force`, and read any suggested "fix" that crosses a major version before
   believing it: for the 2026-10 `piscina` advisory it was `@angular/build@19.2.27`, a downgrade from 21.
3. Only if no safe fix exists, add an entry (the file documents the format). The reason must say why it cannot
   reach a shopper or a staff user (build-time only, say) and what fix you are waiting for. Set `reviewBy` no more
   than 90 days out. A review means: fix it, or re-accept with a new reason and new dates. The entry is a decision
   with an owner and an end date, not an exemption; ask the project owner before adding one.
4. NuGet is stricter at restore: with `TreatWarningsAsErrors`, NuGet's own audit fails the **build jobs'** restore
   on an advisory of any severity (that is how 2026-10-14 broke `main`). Accepting a NuGet advisory therefore needs
   a `NuGetAuditSuppress` item as well, a coordinated change to `Directory.Build.props` or `Directory.Packages.props`.
   The suppression alone does not satisfy this gate: `dotnet list package --vulnerable` still reports it.

What it does **not** cover: an advisory published between runs (it runs on PRs and pushes to `main`, not on a
schedule, so a quiet week finds nothing new); Docker base images and the SQL Server image; anything outside the
npm lockfile and the NuGet graph. `projects/shared/package.json` is audited through the root lockfile. A scheduled
run is the obvious next step once GitHub Actions billing is restored.

## 5. Coding standards

- SOLID + clean architecture ([03](03-architecture-and-tech-stack.md)). Feature folders. No business logic in controllers or components.
- Nullable reference types on. `async` all the way. `CancellationToken` passed through.
- Angular: standalone components, signals, strict TypeScript, OnPush, shared design tokens only (no hard-coded colours).
- Money is `decimal`, never `double`. Dates are UTC in the DB, displayed in Asia/Colombo.
- Configuration via environment variables only (Azure-ready).
- Small commits referencing task IDs (e.g. `ADM-08: approve bank transfer`). Branch per task, merge to `main` after tests pass.

## 6. Lessons learned (apply to every future task)

| # | Lesson | Rule |
|---|---|---|
| L1 | Data edited by non-developers crashed a page on one typo (progress page QA r2) | Load editable data defensively and fail to a neutral message. Document the common mistakes in plain language. |
| L2 | A class with `display:` overrode the `hidden` attribute, so removed or empty-state elements stayed visible (style tile QA r1) | The global reset must include `[hidden]{display:none !important}`. Test every show/hide state visually, not only in the DOM. |
| L3 | Under reduced motion, a 0.01ms override on `visibility` meant `focus()` ran on a still-hidden drawer (style tile QA r1) | Move focus only after the element is visible (next frame or after transitionend). Test drawers and dialogs with reduced motion on. |
| L4 | A pseudo-element with `inset:0` painted over an inset focus outline (style tile QA r1) | Check the focus ring at every breakpoint. Avoid inset outlines on elements that have overlay pseudo-elements. |
| L5 | A large design document was produced in one agent response, hit the output limit and was lost (DB design, 2026-09-15) | Write long documents to disk section by section, never in a single response. |
| L6 | An architecture test that only inspected emitted assembly references missed an unused-but-declared dependency; the project-file check caught it (W0-09 QA) | Keep both checks: project references **and** assembly references. Prove a rule fails by deliberately breaking it in a scratch copy. |

## 7. Session discipline

Start of session: read `MEMORY.md` → `TASKS.md`.
End of session: update both, then update client progress data if any feature status changed.
