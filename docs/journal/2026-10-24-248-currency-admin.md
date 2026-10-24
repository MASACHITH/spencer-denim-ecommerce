# 2026-10-24 - #248 PR-B: the admin "Display currencies" panel

Branch `feature/248-currency-admin`, stacked on `feature/248-currency-rates` (PR-A, the backend). Frontend, plus C# tests
that tie the rule, the sentences and the field names to the shared table. Not pushed. The first independent tester pass
failed it (no blockers, two Majors, thirteen minors, three surviving mutants); the round that fixed that is the last
section.

## What was built

A panel on the admin Settings screen, **Display currencies**, where a staff member types how many rupees one US dollar,
pound, euro, Australian dollar and UAE dirham is worth. It is drawn only when the server sends the `Currency.Display`
group (`Settings.Edit`: the read model filters every row by its own permission, so the API is the boundary and hiding
the panel is convenience).

- Five rows in the API's order, each a labelled text box ("Rupees for one US dollar (USD)") with a decimal keyboard, the
  saved rate's age beside it ("Last changed 3 days ago (21 Oct 2026)") and, after thirty days without a change, **"This
  rate has not changed for N days. Check it is still right."** An empty box says no rate is set and the currency is left
  out.
- One Save: only the boxes that changed are sent; an emptied box is sent as an empty string, which clears that rate; the
  same rate typed another way (399.50 over 399.5) is no change.
- The result of Save (**"Rates saved."**, "nothing to save", and the 401/403/409/5xx/offline alerts) is drawn **directly
  under the Save button** and scrolled into view (see the last section).
- Per-box errors from the API map to their box; a stale version offers "Load the latest rates" and keeps what was typed.
  Typing in a box clears its error. Unsaved typing survives the page's re-read after any save on the screen, including
  typing done while a save was in flight.

**What the panel says is only what is true today.** There is no currency switcher on this branch, and the order page, the
PayPal step and the price-range filter stay in rupees whatever is typed here. So the text says: the number is rupees per
unit, typed by hand and never updated automatically, PayPal still charges the rupee total converted to dollars, an empty
box leaves that currency out, and the shop's currency switcher will use these rates when it is released (said once). It
lists "Rates set: USD, GBP." or "No rates set yet." A unit test reads every state of the panel and forbids "shopper",
"every price", "approximate", "can choose" and "converted at your rate".

## The rule is held in places by one file

`e2e/support/currency-rate-cases.json` holds the 46 rate cases (Arabic-Indic, fullwidth, Tamil and Sinhala digits among
them), the five currencies (field, code, name), the exact sentence for each kind of fault, the handler's two 409
sentences, and how a field NAME is read. It is read by:

- `CurrencyRateRulesParityTests` (C#): the rule under four thread cultures; the REAL `UpdateCurrencyRatesCommandHandler`
  for every currency and every fault (15 sentences) and for both 409 sentences; `DisplayCurrencies.All` against the table.
- `AdminCurrencyFieldNameParityTests` (C# integration, a real SQL Server): `{"Usd":"333"}` saves the dollar, `jpy` and
  `dollar` are ignored, as the table says.
- `currency-rate-rules.spec.ts` and `currency-rates-panel.spec.ts` (the screen's pre-check and what it draws, all five
  currencies), `admin-currency-stub-parity.e2e.ts` (the e2e stub, sentence for sentence and name for name) and
  `admin-currency-rates.e2e.ts` (the screen in a browser).

A rule or a sentence changed in one place and not the others fails one of them.

## A defect found on the way, in my own panel

Checking a box when it loses focus put its error under it, which pushes Save down. If that lands between the mouse going
down on Save and coming up, the pointer is no longer over the button, no click is made, and the first press does
nothing. It showed once as an intermittent failure in a loaded run, then I reproduced it on purpose with a slow click
(red without the fix, green with it, at 390 and 1280 px). The panel now skips the blur check when focus is moving to
Save, because Save runs the same check. A first version of that test was itself wrong: the button was below the fold,
so the pointer pressed the page. Worth knowing for the next slow-click test: scroll the control into view first.

## A hygiene defect the mutation run found in my own spec

With "a second press while saving sends again" mutated in, 31 tests failed instead of one. The first failure left a
request open, `controller.verify()` threw in `afterEach`, and that skipped Angular's test-module reset, so the next 19
tests died with "Cannot configure the test module when the test module has already been instantiated". The cleanup now
restores the clock and resets the module even when verify throws; the same mutation now fails exactly one test. (The
existing panel specs use the same `afterEach(() => controller.verify())` shape and have the same hazard.)

## Tester round 1: what failed and what changed

**Major 1: the outcome of Save was out of view.** It was drawn above the form, so after pressing Save its bottom edge was
60-160 px above the window at 390x844, 1280x900 and 1440x900. The earlier browser tests used `toBeVisible()`, which is
true for an element scrolled out of sight. Now the status and the alert are directly under Save, in the same container,
and scrolled into view after a save or a failure (`block: 'nearest'`, smooth unless the visitor asked for less motion,
with `scroll-margin` so it is not flush against the window edge). Focus is never moved to it: a wrong rate still focuses
its own box (and does not scroll away from it), and a failed save leaves focus on Save. The new browser tests measure the
box against `window.innerHeight` at 390x844, 768x1024, 1280x900 and 1440x900, for saved, nothing to save, a rejected
rate, conflict, server error and offline, with Save pressed in two places: on the window's bottom edge and where a click
leaves it. **Lesson:** the second staging is the one that shows the old defect. With Save on the bottom edge, a message
above the form is still inside the window (the form is shorter than the window); the tester's numbers come from Save
sitting about half-way down.

**Major 2: the text promised what the shop does not do** ("every price converted at your rate and marked as
approximate", "Shoppers can choose to see prices in"). Reworded as above, with the forbidden-words test.

**Minors.** (1) "Load the latest rates" clears the conflict alert and its button. (2) Typing again in a box while a save
is in flight is kept: on success only a box still holding exactly what was sent counts as saved. (3) A 400 that names no
box the panel has says "The server did not accept these rates. Check each one and try again." (4) The status region is
always in the page (screen-reader only) and every message is a new element, so a second identical one announces again.
(5) The label is a sentence. (6) `enterkeyhint="done"`. (7) One age rule for the words and the warning, in
`currency-rate-age.ts`: whole calendar days in Asia/Colombo, stale when more than 30; a stamp ahead of the computer's
clock says "just now" with no date; none, unreadable or before 2000 says "No change date recorded" with no day count;
month names are fixed ("Sep", not the browser's "Sept"); a table of 24 instants is the test. (8) The two JSDoc blocks in
`settings-page.ts` are on their own handlers. (9) The stub's 409 says the real handler's words (both sentences, from the
table, checked against the real handler in C#); the stub reads field names without regard to case like the server, and
refuses one name written twice in two cases (400). (10) A stored value the public feed would skip shows "Stored value is
not a valid rate." and is not counted as set or dated; row keys match without regard to case. (11) A 401 says "Your session
has ended. Please sign in again." (the words the sign-in and two-factor calls use). (12) A stored value that is not text
no longer takes the screen down: it is unreadable, shown empty, not counted.

**Survivors killed:** M13 (a stamp ahead of the clock), M25 (the page's `(reload)` binding, now a unit test in
`settings-page.spec.ts`), M30 (the date read in UTC: a stamp at 19:16 UTC reads as the next day in Colombo).

## Not done, and why

- **Known gap: a rate cannot be re-confirmed.** A rate's age moves only when its number changes, so an admin who
  verifies a rate is still right cannot clear the 30-day warning. PayPal's rate has a `confirmRate` action for this; a
  follow-up for the backend if the owner wants it. The panel's help says the age counts from the last change. It needs a
  small change in PR-A (an action that re-dates the row without changing the number) and is the one thing I would add.
- `currency-rate-rules.spec.ts` and the panel spec read the shared table with Node's `fs`, loaded by a computed module
  name, because the admin spec types do not include Node's and the shared tsconfig is not this change's to widen.
- No Lighthouse or full-suite Playwright run; only the specs this change touches and the existing settings ones.
- A real browser other than Chromium was not used, so the smooth scroll and `scroll-margin` are unproved in Safari.
