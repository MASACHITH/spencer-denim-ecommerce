# 2026-10-24 — UAT feedback on the admin panel (branch `fix/admin-uat-feedback`)

Source: the client's eight-point UAT list plus "any choose file button not works". Nothing here is committed or pushed.

## Fixed in this branch

| # | Feedback | What changed |
|---|---|---|
| 2 | Reports: Export CSV does not work on UAT | The result of an export was announced to screen readers only, so a failure showed nothing. Each report tab (sales, inventory, customers) now shows the outcome in view (`sda-export-note`, test ids `sales-export-note`, `inventory-export-note`, `customers-export-note`). The export asks for a Blob, so the **error body came back as a Blob too** and the server's own explanation was thrown away; `ReportingApi` now reads it back into JSON. |
| 3 | Tracking number should show as a link in another colour | `adm-link--external`: blue, bold, underlined, with an arrow and "opens in a new tab" for screen readers (order detail). |
| 6 | Edit button does not take you to the form | `focusEditForm` scrolls to the form and focuses its first box. Applied to couriers, promo codes, sale campaigns, announcements (Mega menu cards already did it). |
| 8 | Product form: Choose file does not work | The product form's photo and video panel now shows, in view, what the last file choice is doing (`media-status`): uploading, added, or the video waiting for its poster. A 413 from the web server (HTML, no JSON) says the file is too large; a 5xx names its status number. `toCatalogFailure` is shared, so every screen that uses it gets the clearer wording. |

## Answered, not changed

- 3 (how many days until delivered is confirmed automatically): **7 days** after the order is marked shipped, by default. It is the setting `Orders.AutoDeliverAfterDays` (Settings, invoice and fulfilment; 1 to 90 days, empty turns it off). Pickup orders are never closed automatically (docs/08 G-18).
- 4 (order cancellation in admin): exists for **unpaid** orders only; paid orders are refunded, which is not built yet (docs/11 refunds).

## Decided by the owner later the same day, and done

| # | Feedback | What changed |
|---|---|---|
| 1 | Alerts need attention; show them in an alert tab or something better | **A bell in the admin header** (`sda-alert-bell`, replaces the full-width strip `sda-alert-banner`). A red count on the bell, and a panel under it with the same alerts and the same "I have seen this" buttons. Disclosure pattern: Escape closes it and returns focus to the bell, a press elsewhere or navigating closes it, acknowledging does not. Drawn only for a user who may see alerts (a 403 draws nothing); an empty list draws a quiet bell. Test ids `alert-bell`, `alert-count`, `alert-panel` (was `alert-banner`). |
| 5 | Customers: filter by other countries | `GET /api/admin/customers?countryCode=GB` keeps customers whose **default saved address** is in that country (the same fact the row's Country line shows). The page now also returns `countries` (Sri Lanka plus every country a default address is in). The screen has a Country select; the district select is hidden while another country than Sri Lanka is chosen (a district is Sri Lankan) and a district already chosen is dropped. A customer with no saved address is in no country. |
| 7 | Inventory buttons: better colours | Row buttons coloured by job (Stock blue, Threshold teal, History slate) and the status pills green / amber / red, using the dashboard's own `--status-*` and `--stock-*` tokens mixed with the page text colour so contrast holds in both themes. |

## Still open

- 2 and 8 root cause on UAT: unknown until someone presses the button on UAT and reads the new on-screen message. Ask the client for that message and which role they are signed in as (Reports.Export / Media.Create).

## Tests

Admin: 73 files, 954 tests pass; `ng lint admin` clean; `ng build admin` succeeds. New specs: `focus-edit-form.spec.ts`, four Edit-focus assertions, tracking link, three export-note cases, bare 413 and 5xx in `admin-catalog-api.spec.ts`, two upload-failure cases and upload status in `product-form-page.spec.ts`.

## Branch note

Based on c749181 (the blocked-countries fix). Rebase onto `main` once that has merged.
