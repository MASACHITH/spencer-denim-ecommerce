# 2026-10-20 — #191 ADM-14 sale campaigns: individual product targets and a campaign banner picker (developer pass)

Branch `feature/191-campaign-targets` (worktree `worktrees/sd-camptargets`), based on `main` a478fa8. **Not pushed. Not tester-passed, so not Done.** No migration.

## What was built

### 1. Individual product targets
- **Admin screen**: "Add" now offers *Individual products*, which shows `CampaignProductPicker`: an ARIA 1.2 editable combobox with list autocomplete (`role="combobox"`, `aria-controls`, `aria-expanded`, `aria-activedescendant`; options are `role="option"` with `aria-selected` on the highlight). Search-as-you-type (250 ms pause, one `switchMap` so a keystroke cancels the pending pause *and* any search in flight), 10 per page, server-paged, sorted by name. Keys: ↑/↓ (wrap), Enter adds (and never submits the form), Page Up/Down turns the page, Esc closes and a second Esc clears. Click adds without taking focus out of the box. Unpublished products are offered and labelled; one already on the campaign is marked and refused with the reason.
- **Endpoint reused, none added**: `GET /api/admin/products` (term matches name, code, slug, any SKU; paged; soft-deleted products excluded; `Products.View`). Every seeded role with `Promotions.*` also holds `Products.View`; a custom role without it is told which permission is missing.
- **Chips**: every target — department, category, product — is a chip with its own Remove button. After a removal focus moves to the chip that took its place, or to the "Add" select when none are left. The old "product targeting isn't available / kept as they are" text and the hidden kept-product-targets path are gone: editing shows product targets as chips and the save sends exactly the chips.
- **Pricing rules unchanged.** A product named by a product target *and* reached by a department/category target on the same or another campaign gets the lower price once (`CampaignPricing.Best`); verified end to end (category 20% = Rs. 4,392.00 vs product Rs. 1,500 off = Rs. 3,990.00 → 3,990.00; same campaign naming it twice → discounted once).

### 2. Banner picker and the storefront banner
- **Admin contract change (breaking, admin-only consumer):** request `bannerAssetId: int` → **`bannerPublicId: Guid | null`** (canonical `D` form only; braces/bare digits refused); payload `bannerAssetId` → **`banner: { publicId, url, altText } | null`**. The internal asset id no longer reaches a client, like the product form and the logo setting.
- **Validation tightened**: the banner must be a **ready, public, non-deleted image**. The old check accepted any ready asset — a video, or a *private* bank-slip image, which would then have been shown to every shopper. A banner without a description saves with a **warning** (not a refusal: a file re-uploaded without a description dedups to the old asset, so a refusal could make a picture unchoosable).
- **MediaUsages**: new owner type `SaleCampaign` / role `Banner`. Every save rewrites the campaign's usage row in the same transaction (edit/delete: one `SaveChanges`; create: two inside one transaction under the execution strategy, retry resets generated keys). A removed campaign releases the row. The media library refuses to delete a live banner and labels the usage with the campaign name.
- **Admin picker** (`CampaignBannerPicker`): reuses the media library upload (`AdminProductApi.uploadImage`), asks for a description before uploading, previews, Remove moves focus to the file box.
- **Storefront contract**: `GET /api/content/sale-banner` → `{ banner: { imageUrl, imageSrcSet, width, height, altText } | null }` (public, HEAD too). `altText` is empty, never null.
  - **Live only, in shop time**: `IsActive`, not deleted, `StartsAtUtc <= now < EndsAtUtc` (start inclusive, end exclusive — `CampaignWindow`'s convention), boundaries pinned by test.
  - **Read live, not cached**, so it appears/disappears on the campaign's own boundaries; listing prices may lag ≤ 5 min (doc 08 §7.4). Same answer for every visitor; no per-user data.
  - `large` rendition as `src`, srcset up to `zoom` with true widths; the client supplies `sizes`.
- **Storefront template**: `SaleBannerService` (SSR fetch + `ServerHandover` of the whole payload so `{banner:null}` is not refetched; 2 s timeout; any failure = no banner) and the banner in the Sale page's header slot (`.plp__promo`, 16:5), with `alt`, `srcset`, `sizes`, `width`/`height`, `fetchpriority="high"`. Only the Sale page asks.

### 3. #158's two surviving category-move mutants — killed
`CampaignCategoryMoveTests` gained a **nested fixture** (target ← parent ← child, shirt linked to the child only): moving the parent under the target must reprice the shirt, and back out. Plus a **command-count test**: `ProductsLinkedToCategoriesAsync([])` runs no statement.

### 4. Homepage eviction (#192)
`SaleCampaignWriteHandlers` now calls `IHomePageCache.Evict()` after every create/update/delete, **after the recompute and in a `finally`**. Only the campaign write path was touched; the cache implementation was not.

## Owner decisions (safe choice taken)
1. **Banner placement: the Sale page header** (the PLP-01 promo slot). docs/04 and doc 11 do not place a sale campaign's banner; HOME-05 "Campaign banner" is homepage content with its own image, not a sale campaign's. The homepage was not touched (and #192 owns it).
2. **Two live campaigns with banners: the most recent start wins**, then the higher id.
3. **The banner shows only the picture**, not the campaign name: the admin form promises "Shoppers see the price, not the campaign's name".
4. **A banner without a description is allowed with a warning**, and is drawn decorative (`alt=""`). The admin picker still requires a description for a new upload.
5. **Banner read live, not cached** (see above); the 5-minute lag stays on prices only.
6. **No new product-search endpoint**: the picker uses `GET /api/admin/products` under `Products.View`.
7. Contract rename `bannerAssetId` → `bannerPublicId` (admin only; deployed together).

## Tests
- **Unit**: new `SaleCampaignWriteHandlerTests` (24): product targets (shape, mixed types, duplicate by type+id, unknown, missing id, two ids, cap 200/201), banner (publicId → internal id, no lookup without banner, refused, empty Guid, no-description warning ×3, cleared on edit), eviction order on create/edit/delete, eviction on a failing recompute, none on refused or stale saves.
- **Integration (filtered, LocalDB)**: new `AdminSaleCampaignTargetsAndBannerTests` — product target priced on listing, PDP and bag; neighbour untouched; overlap with a category; edit drops a product and restores its price; unknown / removed / duplicate product; >200 targets; banner end to end (payload, usage row, Sale page, 409 with label, clear, release); replace + delete releases; document/private/processing/deleted/unknown asset refused; bad GUID shapes; old field name refused; no-description warning + empty alt; scheduled/ended/inactive show no banner; window boundaries; most-recent-start wins; public endpoint/HEAD; homepage eviction on create/edit/delete through the real API. Plus the two `CampaignCategoryMoveTests` additions and the renamed field in `AdminSaleCampaignEndpointTests` and `CampaignStorefrontVerificationTests`. Filtered run of those four classes: **57/57**; final filtered run (those four plus `AdminSettingsEndpointTests`, `HomeEndpointTests` and every `Media` class): **284/284**.
- **Vitest**: shared 24, storefront **1,941**, admin **764** — all green. New: `campaign-product-picker.spec.ts` (14), `campaign-banner-picker.spec.ts` (6), `sale-banner.service.spec.ts` (8), listing page +6, campaign page rewritten/extended (34).
- **Playwright** (ports 6500–6516): `admin-sale-campaigns.e2e.ts` 25 (10 new: keyboard search/paging/Enter/Esc, duplicate refusal, no match, keyboard chip removal with focus, save+edit with products, banner upload/save/remove, description required, axe with list open, widths with list open), new `sale-banner.e2e.ts` 8 (SSR HTML, placement, no refetch after hydration, none, decorative, failure, Sale page only, axe, 8 widths with 16:5 and no spill). **33/33**. Regression over the suites using the changed stubs (admin-products, product-listing, content-pages, accessibility, admin-settings, seo): **331/331**. No page-level sideways scroll at 320/375/390/768/1024/1280/1440/1920.
- **Builds**: storefront initial **446.31 kB, unchanged** (service lives in the listing chunk, now 21.34 kB); admin initial 314.82 kB; `ng lint` clean; Release build of the solution **0 warnings, 0 errors**; full unit suite **3,203/3,203**.
- **Stubs** mirror the validators field for field: `bannerPublicId` (present, canonical GUID or null, an uploaded *image*), 200 cap, two-id target, unknown product; product list pages with the real bounds (24/96, `sort=name`).

## Mutation testing
28 hand-written mutants, each applied, run against its narrowest suite and reverted (`scratchpad/sd191-dev/mutate.py`). **28/28 killed**, one only after a test was strengthened.

| Area | Mutants | Killed by |
|---|---|---|
| Handler: no eviction; evict before the recompute; empty GUID accepted; no-description warning inverted; duplicate key ignoring the type | 5 | unit |
| Store: banner type, visibility, readiness unchecked; removed campaign keeps its usage row; create skips the usage row | 5 | integration |
| Reader: end inclusive; start exclusive; oldest start wins; inactive shown; null alt passed through | 5 | integration (boundary, two-live, inactive, no-description tests) |
| Media library label for `SaleCampaign` dropped | 1 | integration (409 `ownerLabel`) |
| `ReadGuid` relaxed to `Guid.TryParse` | 1 | **survived first**: the braced-GUID case was still refused, as an unknown picture, so the test passed. The test now asserts the shape message; re-run killed |
| Category move passes the moved id only (#158 survivor 1) | 1 | the new nested-fixture test |
| Empty-set guard dropped (#158 survivor 2) | 1 | the new command-count test |
| Picker: Enter submits when closed; stale answer shown; chosen product not refused | 3 | Vitest |
| Page: banner never sent; chip-removal focus lost; product id not sent | 3 | Vitest |
| Storefront: banner fetched on every listing; alt not normalised; no SSR handover | 3 | Vitest |

Found by the tests during development rather than by mutation: the first debounce (`of(x).pipe(debounceTime)`) did not debounce at all — it completes at once, which flushes immediately. Two picker specs failed on it; it is now one `switchMap` over `timer` and the request.

## Screenshots
`2026-10-20-191-campaign-targets/`: `admin-campaign-picker-1440.png`, `admin-campaign-picker-390.png` (product list open on page 1 of 26 matches, a product chip, a banner chosen), `sale-page-banner-1440.png`, `sale-page-banner-390.png` (the stub serves a 1×1 rendition, so the screenshot run drew a stand-in picture for the banner only).

## Process notes
- One filtered integration run started while another agent's testhost was up (the brief says wait); it passed and did not touch shared rows beyond its own campaigns. Every later run waited for 60 s clear.
- The full integration suite was not run (other agents run backend tests); the tester should.

## Round 2: after the tester's conditional pass (tester commit d702b10)

- **MEDIUM fixed: a pre-#191 banner could be deleted while its campaign was live.** Such a campaign has no usage row. `MediaService.DeleteAsync` now also checks every non-deleted campaign's `BannerAssetId` directly, as it already did for the video poster and the payment slip, and refuses with 409, naming the campaign as `SaleCampaign` / `Banner`. The tester's skipped test is un-skipped and now also checks the 409 body and that the asset was not deleted.
- **LOW fixed: a media delete racing a campaign save.**
  - Both paths now run in one transaction each and take `UPDLOCK, READCOMMITTEDLOCK, ROWLOCK` on the asset's `media.MediaAssets` row first.
  - The save re-checks the banner under that lock: not deleted, ready, an image, public. If the delete won, the save is refused (`SaleCampaignSaveOutcome.BannerUnavailable`, a 400 on `bannerPublicId`) and writes nothing. If the save won, the delete then sees the usage row and answers 409.
  - Lock order is "the asset row first" in both paths, and it is recorded in doc 08 §5.3.
  - Edits now accept changes only after the commit, so a retried attempt still writes them.
  - `SaleCampaignBannerRaceTests` holds the lock from its own connection, starts the first operation, waits until SQL Server reports it waiting, then starts the second, waits for two waiters, and releases. It covers both orders.
- **LOW fixed: a long unbroken product name widened the page.** `campaign-said` now has `overflow-wrap: anywhere`, and the tester's `test.fail` line is removed.
- **Stale Enter fixed.** The picker clears the previous term's list the moment the term changes, so Enter and the arrow keys do nothing until the new results arrive. Covered by a Vitest.
- **Dropped banner made visible.** The admin payload has a new field, `bannerUnavailable` (true when the column names a picture the shop can no longer show). The form shows "The banner previously chosen is no longer available… saving this campaign will remove it", and the list says "No longer available in the media library". Covered by Vitest and by the tester's integration theory, which now asserts the flag.

**Mutation testing, round 2: 8/8 killed.**
- Delete ignores the campaign foreign key: killed by the legacy test.
- Delete takes no lock: killed by the race test.
- Save takes no lock: killed by the race test.
- Save lock ignores `IsDeleted`: killed by the delete-first race test.
- Save refusal treated as saved: killed by the race test.
- Stale list kept while searching: killed by Vitest.
- Unavailable banner not flagged: killed by Vitest.
- Status line does not wrap: killed by the tester's long-name e2e.

**Round 2 numbers:**
- Unit: 3,203/3,203.
- Filtered integration (SaleCampaign, Campaign, Media, Home, Catalog, Pricing, Content): 615/615.
- Release build: 0 warnings, 0 errors.
- Vitest: shared 24, storefront 1,941, admin 769. `ng lint` clean.
- Builds: storefront initial 446.31 kB (unchanged), admin 314.82 kB.
- Playwright: sale-campaign and sale-banner suites 39/39.
