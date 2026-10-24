# 2026-10-24 — UAT feedback on the admin panel (#256)

Branch `fix/admin-uat-feedback`, on `main` at 057d6f3. **No migration.** Source: the client's eight-point UAT list plus "any choose file
button not works", and the owner's decisions on the open ones.

## Done

| # | Feedback | What changed |
|---|---|---|
| 1 | Alerts need attention; show them in an alert tab or something better | **A bell in the admin header** (`sda-alert-bell`, replaces the full-width strip `sda-alert-banner`): a red count, and a panel under it with the same alerts and the same "I have seen this" buttons. A disclosure, not a dialog: Escape (heard anywhere on the page) closes it and returns focus to the bell, a press elsewhere, tabbing out of it, or opening another page closes it; acknowledging does not. Drawn only for a user who may see alerts (a 403 draws nothing); an empty list draws a quiet bell; **a sign-in page draws nothing, even right after signing out**. Test ids `alert-bell`, `alert-count`, `alert-panel` (was `alert-banner`). |
| 2 | Reports: Export CSV does not work on UAT | The result of an export was announced to screen readers only, so a failure showed nothing. Each report tab now shows the outcome in view (`sda-export-note`: `sales-export-note`, `inventory-export-note`, `customers-export-note`), and clears it when the filters change. The export asks for a Blob, so an error body arrived as a Blob and the server's explanation was lost; `ReportingApi` now reads it back into JSON. A 400, 413 or 422 with a `detail` shows the server's own sentence; a 403 and a 5xx show fixed wording (the 5xx names its status number). |
| 3 | Tracking number should show as a link in another colour | `adm-link--external`: blue, bold, underlined, with an arrow and "opens in a new tab" for screen readers (order detail). |
| 5 | Customers: filter by other countries | `GET /api/admin/customers?countryCode=GB` keeps customers whose **default saved address** is in that country (the same fact the row's Country line shows). The page also returns `countries` (Sri Lanka plus every country a default address is in). The screen has a Country select; the district select is hidden while another country than Sri Lanka is chosen (a district is Sri Lankan) and a district already chosen is dropped. A customer with no saved address is in no country. |
| 6 | Edit button does not take you to the form | `focusEditForm` scrolls to the form and focuses its first box. Applied to couriers, promo codes, sale campaigns, announcements (Mega menu cards already did it). |
| 7 | Inventory buttons: better colours | Row buttons coloured by job (Stock blue, Threshold teal, History slate) and the status pills green / amber / red, using the dashboard's own `--status-*` and `--stock-*` tokens mixed with the page text colour so contrast holds (5.2:1 to 9.2:1 measured). |
| 8 | Product form: Choose file does not work | The photo and video panel shows, in view, what the last file choice is doing (`media-status`): uploading, added, or the video waiting for its poster. A 413 from the web server (HTML, no JSON) says the file is too large; a 5xx names its status number. `toCatalogFailure` is shared, so every screen that uses it gets the clearer wording. |

## Answered, not changed

- 3 (how many days until delivered is confirmed automatically): **7 days** after the order is marked shipped, by default. It is the
  setting `Orders.AutoDeliverAfterDays` (Settings, invoice and fulfilment; 1 to 90 days, empty turns it off). Pickup orders are never
  closed automatically (docs/08 G-18).
- 4 (order cancellation in admin): exists for **unpaid** orders only; paid orders are refunded, which is not built yet (docs/11 refunds).

## Not reproduced

Items 2 and 8 on UAT: checked on 2026-10-24 as a Super Admin in a plain browser, all three exports downloaded and a photo uploaded
(201). A browser driven by an automation tool swallows the file-picker window and saves downloads in the tool's own folder, which
looks exactly like both failures; ask the client whether they saw it in a normal window. A large phone photo (12 megapixels, 4.9 MB)
took 28.5 s to upload from that machine with nothing visible on the old screen; the new "Uploading..." line is the fix for that.

## Measured and accepted

The country filter is a correlated `EXISTS` on `CustomerAddresses`: at 30,000 customers and 40,000 addresses a filtered page reads
about 2,000 pages (the unfiltered page reads 228) and the options query about 630, run on every list request. The existing name search
already costs about 1,800. Acceptable at this scale; the plan tests still assert only the unfiltered statements. If the customer list
ever grows past what this handles, index `CustomerAddresses (CountryCode) WHERE IsDefault = 1`.

## Tests

Admin 73 files / 965 tests (the full suite flaked once or twice in each of several runs under load with 5-second timeouts in
unrelated specs, and passes alone), `ng lint admin` clean, `ng build admin` succeeds. Backend: 32 AdminCustomer unit tests, 69
AdminCustomer integration tests against SQL Server, brand-name convention. Browser suites for alerts, inventory and customers pass
including axe. An independent tester also ran the country filter against SQL Server with junk input, the Blob reader with non-JSON,
empty, 6 MB and invalid UTF-8 bodies, and the bell at 320 and 375 px; its findings are fixed except the ones listed above.
