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
| Integration | xUnit + Testcontainers SQL Server + WebApplicationFactory | API endpoints, EF mappings, auth, PayPal webhook handling (sandbox/mocked) |
| E2E | Playwright | Critical journeys: browse → filter → PDP → cart → checkout (PayPal sandbox, bank transfer) → admin approves → customer sees status + tracking link |
| Responsive | Playwright at all 8 breakpoints + screenshot diffs | Every storefront and admin page |
| Accessibility | axe-core in Playwright | WCAG 2.2 AA |
| Performance | Lighthouse CI on home, PLP, PDP | Mobile LCP < 2.5 s, CLS < 0.1 |
| Security | OWASP ZAP baseline on staging, dependency audit | Before go-live |

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

## 7. Session discipline

Start of session: read `MEMORY.md` → `TASKS.md`.
End of session: update both, then update client progress data if any feature status changed.
