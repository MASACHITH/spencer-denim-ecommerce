# 2026-10-24 - #248 PR-B: the admin "Display currencies" panel

Branch `feature/248-currency-admin`, stacked on `feature/248-currency-rates` (PR-A, the backend). Frontend only, apart
from one C# test file that ties the rule to the shared table. Not pushed; an independent tester pass is owed.

## What was built

A panel on the admin Settings screen, **Display currencies**, where a staff member types how many rupees one US dollar,
pound, euro, Australian dollar and UAE dirham is worth. It is drawn only when the server sends the `Currency.Display`
group (`Settings.Edit`: the read model filters every row by its own permission, so the API is the boundary and hiding
the panel is convenience).

- Five rows in the API's order, each a labelled text box with a decimal keyboard, the saved rate's age beside it
  ("Last changed 3 days ago (21 Oct 2026)") and, after thirty days without a change, **"This rate has not changed for N
  days. Check it is still right."** An empty box says it is not shown to shoppers.
- One Save: only the boxes that changed are sent; an emptied box is sent as an empty string, which clears that rate; the
  same rate typed another way (399.50 over 399.5) is no change. "Rates saved." is **on the screen as well as announced**
  (the email panel's confirmation is screen-reader only).
- Per-box errors from the API map to their box; a stale version offers "Load the latest rates" and keeps what was typed;
  403 and a lost connection are said plainly. Typing in a box clears its error. Unsaved typing survives the page's
  re-read after any save on the screen.

## The rule is held in four places by one file

`e2e/support/currency-rate-cases.json` (46 cases, including Arabic-Indic, fullwidth, Tamil and Sinhala digits) is read by
`CurrencyRateRulesParityTests` (C#, the API's rule, also under four thread cultures), `currency-rate-rules.spec.ts` (the
screen's pre-check), `admin-currency-stub-parity.e2e.ts` (the e2e stub) and `admin-currency-rates.e2e.ts` (the screen
in a browser). A rule changed in one place and not the others fails one of them. The stub's save follows
`UpdateCurrencyRatesCommandHandler`: field errors, then the row version, then the diff, and it re-dates a rate only when
its number changes.

## One voice: the screen says the sentence the server says (after PR-A's wording change)

PR-A's handler stopped lower-casing the currency names, so "US dollar" and "UAE dirham" keep their capitals in the middle
of a sentence. The screen shows its own sentence before a request is sent and the server's after one, so the two must
never read differently for the same fault. The shared table now also holds the five currencies (field, code, name) and
the exact template for each kind of fault with `{name}` and `{code}` filled in:

- **C#:** `CurrencyRateRulesParityTests` asks the REAL `UpdateCurrencyRatesCommandHandler` for every currency and every
  kind of fault (15 answers) and compares each with the table, and checks `DisplayCurrencies.All` against the table.
- **The screen:** the pre-check (`currencyRateMessage`) uses the name as it stands, no case change; `DISPLAY_CURRENCIES`
  has the same names. A label that begins with a name is capitalised on its own ("Pound sterling (GBP)").
  `currency-rate-rules.spec.ts` and `currency-rates-panel.spec.ts` compare the sentences for all five currencies with
  the table, not just for pound sterling.
- **The e2e stub and the browser:** `admin-currency-stub-parity.e2e.ts` compares the stub's whole sentence (it was
  `toContain` on a prefix) and `admin-currency-rates.e2e.ts` compares what the screen shows, for every case in the
  table and for all five currencies.

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

## Not done, and why

- **Known gap: a rate cannot be re-confirmed.** A rate's age moves only when its number changes, so an admin who
  verifies a rate is still right cannot clear the 30-day warning. PayPal's rate has a `confirmRate` action for this; a
  follow-up for the backend if the owner wants it. The panel's help says the age counts from the last change. It needs a
  small change in PR-A (an action that re-dates the row without changing the number) and is the one thing I would add.
- `currency-rate-rules.spec.ts` reads the shared table with Node's `fs`, loaded by a computed module name, because the
  admin spec types do not include Node's and the shared tsconfig is not this change's to widen.
- No Lighthouse or full-suite Playwright run; only the specs this change touches and the existing settings ones.
