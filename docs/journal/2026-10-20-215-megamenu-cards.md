# 2026-10-20 — #215 (ADM-17 follow-up): mega-menu promo cards, admin-editable

Branch `feature/215-megamenu-cards`, worktree `worktrees/sd-megamenu`, cut from `main` at `35ee504` (includes #214
and #216). **One migration: `20261020181219_MegaMenuPromoCards`.** Not pushed. Developer pass; the tester pass is
next.

## 1. What was built

**Table `content.MegaMenuPromoCards`** (doc 08 §3.7, updated): `DepartmentId` (FK, NO ACTION), `Title`
nvarchar(100), `Subtitle` nvarchar(200) null, **`ButtonLabel` nvarchar(40)** (new: the storefront had hard-coded
"Shop now"), `LinkUrl` nvarchar(500), **`ImageAssetId` null** (FK, NO ACTION), `SortOrder`, `IsActive`,
`StartsAtUtc`/`EndsAtUtc`, audit columns, **`RowVersion`** (new, [RV]). Checks: `CK_…_ActiveImage` (`IsActive = 0
OR ImageAssetId IS NOT NULL`), `CK_…_Schedule` (end after start), and non-blank `Title`, `ButtonLabel`, `LinkUrl`.
Indexes, explicitly named: `IX_MegaMenuPromoCards_Department_SortOrder`, `IX_MegaMenuPromoCards_ImageAssetId`.
Collation is the database's (`Latin1_General_100_CI_AS_SC`, set by provisioning, not per column — doc 08 N12).
`has-pending-model-changes`: none after the migration.

**One card or several?** Doc 08 keys the table to a department with a sort order and a schedule, and doc 11 P13
step 10 says "per department adds a promo card … order, schedule". So **several per department (at most 6), one
drawn**: the first by `SortOrder, Id` that is switched on, inside its window, with a ready public image.

**Admin API** (`AdminHomeContentEndpoints.MegaMenu.cs`, a `partial` of #214's class so it shares its plumbing):
`GET /api/admin/content/mega-menu` (Content.View), `POST …/cards` (Content.Create), `PUT …/cards/{id}`
(Content.Edit), `DELETE …/cards/{id}` (Content.Delete, `If-Match`). Antiforgery on every write; whole-form bodies
(a missing key is a 400 naming it); row version in body or `If-Match`; 409 `stale_row_version`,
`too_many_mega_menu_cards`. Field rules are #214's `ContentFieldRules` (plain text, no control characters; link =
site path or http(s) without credentials) and `ContentMediaRules` (ready, public image **with alt text**). A card
may be saved switched off without a picture, not switched on. Audit rows (`EntityType = MegaMenuPromoCard`) are
field diffs; an untouched resave writes nothing. `MediaUsages` rows (`MegaMenuPromoCard` / `Image`) are rewritten
in the same transaction as the card (`ContentAdminStores.SaveWithUsageAsync`), and removed with it.

**Cache.** The mega menu is served by `GET /api/catalog/departments`, which has **no server cache** (it is read on
every request; the SSR render hands the tree to the browser). So a save, removal or schedule boundary is on the next
page request. Every card write still evicts `IHomePageCache`, as every ADM-17 write does. Recorded as a new §7.4
row in doc 08.

**Storefront.** `DepartmentDto.promoCards` (only drawable cards, best first; `MegaMenuCardVisibility` is the one
rule, shared with the editor's "On the menu now" marker). `toPromo` draws the first card it can (non-blank heading
and button, a picture, a link `toLink` accepts), else **today's card exactly** ("New in {name}", the description,
the department picture with `alt=""`, "Shop now" → `/new-arrivals`). A site path goes through the router with its
query split off; an http(s) address is an anchor with `target="_blank" rel="noopener noreferrer"`. All text is
interpolated. The picture uses the card + large renditions with the existing `sizes`, and the media library's alt
text. The phone drawer is unchanged (it never showed the card). `TODO(ADM-17)` in `navigation.mapper.ts` is gone.

**Admin screen** `/content/mega-menu` ("Mega menu cards", dashboard link): #192's announcement-bar layout — one
form (department, heading, line, button, link, media picker, order, dates, switch), then each department with its
cards, status ("On the menu now" / "Waiting its turn" / "Scheduled" / "Off"), Edit, two-press Remove, "Add a card
for …" (pre-filled with today's words, hidden at 6), and a sentence saying what the menu shows when no card is on.

**Seeding.** `HomeContentSeeder.SeedMegaMenuCardsAsync`: one card per department **switched off**, in today's
words, with the department's picture when it is a ready public image with alt text (else none), plus its usage row.
Offered **once** under its own marker `Seed.MegaMenuCardsVersion = 1` (not by raising `Seed.HomeContentVersion`,
which would re-offer #63's artwork rows to every database at version 2). Never over a department that already has a
card; cards, usage rows and marker in one transaction. Works on existing databases (no marker → offered).

**Media delete.** `MediaService.DeleteAsync` is **untouched**: it already refuses an asset with any `MediaUsages`
row, and the card's row is written in the card's own transaction. The only `MediaService` change is a label case
(`"Women menu: Linen days"`) in `LoadOwnerLabelsAsync`, placed away from #191's hunk so the branches merge cleanly.

## 2. To do after #191 merges

#191 (`feature/191-campaign-targets`) makes `DeleteAsync` one transaction that takes `UPDLOCK, READCOMMITTEDLOCK,
ROWLOCK` on the `media.MediaAssets` row first, and documents that lock order in doc 08 §5.3. **The card save should
adopt the same pattern**: lock the chosen asset's row first inside `SaveWithUsageAsync`, then re-check it is not
deleted, then write the card and its usage row. Until then a card save and a media delete racing on the same
picture within milliseconds could both succeed (the save's `ContentMediaRules` check reads before the delete
commits). The same gap exists today for #192's homepage items and size guides, which share this store.

## 3. Tests

| Suite | Result |
|---|---|
| Unit (`SpencerDenim.UnitTests`) | **3,666 passed**, 0 skipped. New: `MegaMenuCardWriteHandlerTests` (50), `MegaMenuCardSeedDataTests`, the content seed-marker theory, card parity through the handler in `ContentFieldRulesParityTests` |
| Integration, filtered (#126) | see §3a |
| Vitest storefront | **1,960 passed** (new `navigation.promo.spec.ts`) |
| Vitest admin | **814 passed** (new `mega-menu-page.spec.ts`, `admin-mega-menu-api.spec.ts`) |
| `ng lint` | clean (all three projects) |
| `npm run build` | storefront initial **448.74 kB** (brief's main figure 446.80 kB; **+1.94 kB**). A shared `ng-template` + `NgTemplateOutlet` version measured 450.75 kB, so the card's insides are written out twice instead |
| `npm run build:admin` | clean, 313.98 kB initial (page lazy-loaded) |
| Playwright (ports 6700–6718) | new: `storefront-mega-menu` 9, `admin-mega-menu` 14, card parity 1; regression set with them (accessibility, storefront-shell, home-page, header-height, admin home/announcements/size-guides/dashboard, parity): **282 passed** |
| Release build | see §3a |

Integration classes: `AdminMegaMenuCardEndpointTests` (permissions on all 4 routes for no-permission and anonymous
callers, per-action permissions, antiforgery on each write, whole-form, 15 bad values by field, picture/schedule
rules, markup stored as text, the seeded fallback, lifecycle with storefront feed + MediaUsages + media delete
refusal/free + stale versions + audit, live/order/schedule with times relative to now, the limit, cache eviction on
create/update/delete) and `MegaMenuCardSeedAndSchemaTests` (fresh, existing, marker, unusable pictures, five CHECKs,
named indexes; throwaway databases).

E2E found one real defect: **a long unbroken heading pushed the page 293 px sideways at 1024 px** (the card's grid
item had no `min-width: 0`). Fixed with `min-width: 0; overflow-wrap: anywhere` on `.mega__promo`; the test failed
before and passes after.

### 3a. Numbers filled in after the background runs

(see the end of this file)

## 4. Mutation testing

`scratchpad/sd215-dev/mutate.py` (apply, build, run, revert; sources verified identical afterwards). Mutants that
did not compile under warnings-as-errors were rewritten so they compile, not counted as kills.

- Backend handler (unit): **17 of 17 killed** — picture rule removed; limit `>=`→`>`; move check reversed / disabled;
  link may not leave the site; blank link accepted; no eviction after save / after delete; usage rows never written;
  delete keeps usage rows; edit / delete skip the row version; title / subtitle bound +1; unchanged resave saves;
  unknown department accepted; image not checked against the library.
- Frontend (Vitest): **10 of 10 killed** — mapper draws a card without a picture / with an unsafe link / with a
  blank heading, ignores cards, fallback alt or link changed; admin page drops the row version or the picture,
  offers Add past the limit; status marks a waiting card as shown.
- Integration mutants: see the end of this file.

## 5. Owner decisions (safe choice taken)

1. **Several cards per department, one drawn** (first by order that is on, in its window and has its picture), at
   most 6. Doc 08 and doc 11 support a list; the design has one slot.
2. **`ButtonLabel` and `RowVersion` added** to doc 08's table (the brief asks for both; doc 08 updated).
3. **`ImageAssetId` nullable**, with a CHECK that a switched-on card has one — so the seeder can offer today's card
   before a picture exists (#192's `needsArtwork` arrangement). Doc 08 said NOT NULL; updated.
4. **Seeded switched off**, in today's words, under a separate marker `Seed.MegaMenuCardsVersion`.
5. **Links may leave the site** (http(s)), as the brief says; such a card opens in a new tab. Announcement-bar
   style, not homepage-tile style.
6. **The card shows the image's alt text** from the media library; the fallback keeps `alt=""` as before.
7. A card create is audited with `EntityId = "new"`, as every ADM-17 create is (pre-existing pattern, not changed).

## 6. Not done

- #191 lock order for card saves (§2), after both merge.
- MEMORY.md, TASKS.md and `client-progress/progress-data.js` are left to the lead.
