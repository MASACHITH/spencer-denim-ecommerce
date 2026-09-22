## What and why

<!-- One or two sentences. What changes, and why. -->

Closes #

## Scope

- [ ] This is inside the scope baseline (`docs/02`), feature ID(s): <!-- e.g. PLP-02, ADM-10 -->
- [ ] Area(s) touched: <!-- backend / storefront / admin / shared / docs / infra -->
- [ ] No unrelated changes bundled in

## Testing

- [ ] `dotnet build -c Release` clean, `dotnet test` green (with `TEST_SQL_CONNECTION` set)
- [ ] Frontend: `npm run lint`, `npm run test:ci`, production build
- [ ] **Tester agent passed** — what it checked:
      <!-- e.g. all filters, empty state, 320-1920 px, keyboard, error cases -->
- [ ] UI work: checked at 320 / 768 / 1280 px, no horizontal scroll
- [ ] CI is green on this branch

## Risk and coordination

- [ ] Database migration included? If yes, say so — **only one in flight at a time**
- [ ] Dependency added or version changed (`package.json`, `Directory.Packages.props`)? If yes, say why
- [ ] Shared library, design tokens, `angular.json` or CI touched? If yes, the other developer has been told
- [ ] No secrets, no `.env`, no credentials in code or config

## Notes for the reviewer

<!-- Anything tricky, decisions taken, follow-ups deliberately left out. -->
