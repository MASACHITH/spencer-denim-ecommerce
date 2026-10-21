# 2026-10-21 — Sri Lanka plus worldwide delivery; PayPal the only payment method

Branch `feature/intl-delivery-paypal-only`, worktree `worktrees/sd-intl`, cut from `main` at `6cece38`.
**One migration: `20261021114304_InternationalDeliveryPayPalOnly`.** Not pushed. Developer pass; the
independent tester pass is next.

## 1. The owner's decisions (2026-10-21, recorded in doc 02 §I2)

1. **Delivery is Sri Lanka plus worldwide.** Sri Lanka unchanged (district, `DeliveryMethods`,
   `DeliveryMethodZoneRates`). A country is chosen first at checkout and on saved addresses. Overseas orders go
   by an international service (EMS, DHL) at a fee per region or per country, all admin-editable, every country
   switchable.
2. **Bank transfer switched off; PayPal the only payment method** — "block only for now", behind a Super Admin
   switch. Existing bank-transfer orders and the approval queue keep working.
3. **Prices stay in LKR.** PayPal charges the USD equivalent at the admin rate (#155). Multi-currency stays out
   of scope (doc 02 §J).

## 2. Design

**Schema** (doc 08 §1.2, §3.1, §3.5, §9 updated):
- `ref.Countries` [A][RV]: ISO 3166-1 alpha-2 PK `char(2)`, English name (UX), `RegionCode` (FK, NULL for LK
  only — CK), `PostalCodeRequired`, `RegionRequired`, `IsShippingEnabled` (LK cannot be switched off — CK),
  `DeliveryFeeOverride`, a days override pair (both or neither — CK), `SortOrder` (LK 0, others 1).
- `sales.DeliveryRegions` [A][RV]: six regions, `Fee` (NULL = not priced), days, `IsEnabled`.
- `sales.DeliveryMethods` gains seeded `INTERNATIONAL` (no zone rates). Its `IsEnabled` is the "all overseas"
  switch.
- `sales.Orders`: `DeliveryCountryCode` (FK, NOT NULL), `DeliveryCountryName` (snapshot), `Ship_Region`;
  `DeliveryDistrictId/Name/Zone` become nullable; **`CK_Orders_Destination`**: LK ⇔ district+zone present and
  method ≠ INTERNATIONAL; overseas ⇔ all three NULL and method = INTERNATIONAL (so no overseas pickup).
  `IX_Orders_Paid` now includes `DeliveryCountryCode` (dashboard stays covered).
- `identity.CustomerAddresses`: `CountryCode` (FK, NOT NULL), `Region`; `DistrictId` nullable;
  `CK_CustomerAddresses_Destination`.

**One fee function** — `OverseasDeliveryRate.Resolve` (Domain): available only when INTERNATIONAL, the country
and its region are all on; fee = country override ?? region fee (none or negative → `NotPriced`); days = the
country's pair when both set, else the region's. Checkout, the bag/header estimate (`/api/delivery/countries`),
the admin screen and the order all use it. No free-above threshold overseas.

**Checkout** (`GetCheckoutQuery`, `PlaceOrderCommandHandler`, `PlaceOrderValidator`, `DestinationRules`):
`countryCode` on the quote and the order; omitted = LK, so every existing client is byte-for-byte unchanged.
Overseas: one option (International at the country's quote), district ignored, new blocker `CountryNotServed`;
address rules — state where the country always has one (US, CA, AU), postcode where it always has one (42
countries; letters/digits/space/hyphen ≤ 10, no per-country format), phone with country code (`+`/`00`). Method
must fit the country (`MethodMismatch`). A refused country/method writes nothing.

**Payments**: `PaymentProviders.IsEnabled` is now an admin switch — `GET/PUT /api/admin/settings/payment-providers`,
`Settings.Payments` on the route *and* in the handler, audited (`SettingChange`, before/after), raising
`SensitiveSettingsChanged` in the same transaction, optimistic on `RowVersion`. Switching on requires the provider
to be ready (`IPaymentProvider.IsAvailableAsync` — PayPal needs credentials + a fresh rate). Switching off is
always allowed, the last one included. Checkout with no provider enabled and ready answers **409
`NoPaymentMethod`** and writes nothing (new `PaymentChoice.NoneEnabled`); a request naming a disabled provider is
400 `paymentMethodCode`. The bank-transfer email/instructions/order panel only exist for bank-transfer orders, so
none appear for new orders while it is off; existing ones are untouched.

**Seed rule for the switch** (`SalesReferenceDataSeeder.ApplyPaymentProviderIncrementsAsync`, marker
`Seed.PaymentProvidersVersion = 1`): fresh databases insert BankTransfer **off**; an existing database is switched
off **once**, unless `UpdatedByUserId` is set (an admin saved the row); the update and the marker commit together
and the step never runs again — so a Super Admin who turns it back on keeps it on. Reasoning: before this change no
switch existed, so every existing "on" was the seeder's, not a choice, and the owner's decision is newer.

**PayPal**: the create-order payload now carries `purchase_units[0].shipping` (name, lines, `admin_area_2` city,
`admin_area_1` state, postcode, ISO `country_code`) with `SET_PROVIDED_ADDRESS`; pickup keeps `NO_SHIPPING`. If
PayPal refuses the address itself (`PayPalAddressRefusal`), the flow asks once more without it under a different
request id (`create-{id}-ns`), so a placed order can always be paid. Capture, refund and webhook logic untouched.
The USD amount was already computed from the order's grand total, which now includes the overseas fee.

**Readers/rendering**: order confirmation, admin order list/detail/invoice, customer addresses (account + admin),
customers list (country of the default address), emails (Country row instead of District), dashboard "customers
by district" (overseas buyers one row per country; `districtId` null, `countryCode`), customers report (country in
the district column for overseas).

**Admin Overseas delivery screen**: `GET /api/admin/delivery/overseas` (`Settings.View`),
`PUT …/international`, `PUT …/regions/{code}`, `PUT …/countries/{code}` (`Settings.Edit`), antiforgery, row
version, audited before/after; bounds in `OverseasDeliveryRules` (fee 0–1,000,000, 2 dp; days 1–90, min ≤ max;
override both or neither).

**Frontend** (two forks, my review): storefront `DeliveryCountryService` (`/api/delivery/countries`, SSR
transfer, `sd.delivery-country`), header country select beside the district select (hidden overseas), bag overseas
estimate, checkout country-first with the international address form, address book, order page; admin Overseas
delivery page (`/settings/overseas-delivery`), Payment methods panel, Destination/Country columns, international
address rendering, dashboard Overseas tag. Footer, fallback announcement and the seeded announcement/benefit copy
now read "Delivery across Sri Lanka and worldwide" (#33 handover; seeded copy applies to new databases only).

## 3. Migration

Hand-ordered after generation (the generated one would have failed twice): create the two tables; **insert the LK
country row**; drop `CK_Orders_DeliveryZone`, `IX_Orders_DeliveryDistrictId`, `FK_Orders_Districts_…`,
`IX_Orders_Paid`, `IX_CustomerAddresses_District`, `FK_CustomerAddresses_Districts_…` (SQL Server Msg 5074 refuses
ALTER COLUMN under them), make the district columns nullable, put them back; add country columns **nullable →
UPDATE to LK / Sri Lanka → NOT NULL**; then the new CKs, indexes and FKs. Explicit names throughout; string
columns take the database collation `Latin1_General_100_CI_AS_SC` (provisioning script, never `UseCollation`, doc
08 N12). `Down` refuses (THROW 50001) once any overseas row exists. `has-pending-model-changes`: none.

**Coordination:** `feature/78-118-search` also has a migration in flight. If it merges first, regenerate this
one on top before merging (never `migrations remove`; delete the files, re-add, and re-apply the hand-ordering
above — the backfill and the drop/re-add of dependents are not generated by EF).

## 4. Owner decisions taken as the safe choice (to confirm)

1. All countries enabled, except the seven ISO entries with no street addresses (AQ, BV, GS, HM, IO, TF, UM).
   Sanctioned destinations (e.g. KP) are enabled because the owner said "any country"; the owner may want to
   switch some off.
2. Region fees are **placeholders**: Asia 6,500 · Middle East 7,500 · Europe 9,500 · North America 10,500 ·
   Oceania 9,500 · Rest of world 12,500 LKR.
3. A free-delivery promo code waives Sri Lankan delivery only (the checkout says so).
4. Postcode required in 42 countries, state in 3; no format checks; overseas phone must carry its country code.
5. PayPal address-refusal fallback (one retry without the address).
6. The bank-transfer seed rule above (existing databases switched off once).
7. Saved addresses may be in a switched-off country; checkout refuses.
8. Customers list "country" = the default saved address's country.

## 5. Product page (#33)

No product-page template change here. `feature/33-delivery-returns` adds a delivery panel with an
`[sdDeliveryRegion]` slot. **At merge time**: inject `DeliveryCountryService` in that slot and, when
`isOverseas()`, render "Delivery to {selected().name}" with `overseasEstimate()` (fee, working days) or "We cannot
deliver to {name} at the moment"; otherwise keep #33's district estimate.

## 6. Tests

**Results (developer pass):**
- Unit: 4,012 / 4,012.
- Integration (LocalDB, filtered): the five new classes 58/58; the broad regression filter (Sales, Api.Admin,
  Api.Accounts, Api.Catalog, Content, Ops) 2,093 passed / 13 failed on its first run — every failure an expectation
  this change deliberately moves (schema column/index/table lists, fresh deployment now seeding both providers
  off, the new `Seed.PaymentProvidersVersion` key, and three of my own tests assuming no leftover providers from
  other suites). Fixed; the affected 93 re-run green.
- Vitest: storefront 2,183, admin 907, shared 24 — all pass. `ng lint` clean. Storefront initial **454.16 kB**
  (main 449.83 kB, +4.33 kB: the header country selector and its service). Admin build 316.40 kB.
- Playwright (forks, ports 6450–6489): new `intl-delivery.e2e.ts` 11/11 and `admin-overseas-delivery.e2e.ts`
  17/17 (axe + no sideways scroll at 320–1920); checkout, bag, account, shell, header-height, accessibility,
  bank-transfer, admin settings/orders/customers/dashboard/bank-transfers regressions all green.
- Mutation (scratch `mutate.py`, one mutant at a time, file restored byte-for-byte): fee calculation 8/8, country
  validation 12/12, provider gating 7/7, seed rule 6/6 — **33/33 killed**. One mutant (the switch handler's own
  `Settings.Payments` check) first survived the HTTP suite because the route guard answers first; a handler-level
  test (`PaymentProviderSwitchHandlerTests`) now kills it.
- Release build: 0 warnings. `has-pending-model-changes`: none.

New suites:
`OverseasDeliveryRateTests`, `DestinationRulesTests`, `OverseasCheckoutRulesTests`, email/PayPal-gateway cases;
integration `InternationalCheckoutTests`, `PaymentProviderSwitchEndpointTests`,
`AdminOverseasDeliveryEndpointTests`, `PayPalInternationalTests`, `InternationalDeliverySeedAndMigrationTests`
(fresh seed, the once-only switch-off rule in four shapes, no-overwrite, and the backfill migrated from
`20261020181219_MegaMenuPromoCards`). The shared test database switches bank transfer back **on** after seeding
(documented in `SqlServerDatabaseFixture`), so every pre-existing bank-transfer suite keeps proving "switched on:
works as before".

**Environment note:** two `testhost` processes not started by this session (one PID constant for hours) were
present for every run, so each run proceeded after the 20-minute wait with overlap noted.
