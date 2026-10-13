# 2026-10-13 — W3-05c, the sale-campaign admin screen (ADM-14)

Branch `feature/W3-05c-campaign-admin`, from `origin/main` at `9576d66`. Not pushed, no PR.

**There is no CI.** GitHub Actions has been dead since 9 Oct 09:21, so every figure below comes from a run
on this machine, in this worktree, today. Where something is an inference rather than a measurement, it
says so.

---

## What this task was

W3-05b merged ADM-14's backend — the pricing rule, the two tables, the live read, the stored summary and
its five-minute job, and the admin API — with **no Angular page**, so no staff member could create a
campaign. The owner chose this over starting PayPal. The task, in the tester's recommended order:

1. `ExcludeSaleItems` on the promo-code form, which W3-05 left off for a reason ADM-14 had falsified;
2. the campaign screen, departments and categories only;
3. the category-move desync, if time allowed.

All three are done. A fourth thing was found on the way and is the most urgent item in this file.

---

## The defect this task found, which is on `main` today

**`PromoCodeAdminReader` made the whole ADM-13 promo-code screen answer 500.** It projected every row
into a positional record and then filtered and ordered on *that* record:

```csharp
Projection().FirstOrDefaultAsync(candidate => candidate.Id == promoCodeId)   // FindAsync
Projection().OrderByDescending(row => row.Id)                               // ListAsync
```

EF Core cannot see through a positional record's constructor, so both answer *"The LINQ expression … could
not be translated"* and the request becomes a 500. `PromoCodeWriteHandlers.CommitAsync` re-reads through
`FindAsync` after every save, so since W3-05 merged on **12 Oct**:

| Route | What it did |
|---|---|
| `GET /api/admin/marketing/promo-codes` | **500** — the screen could not list anything |
| `POST` / `PUT` | wrote the row, then **500** |

**Measured on pristine `origin/main` 9576d66**, in a throwaway worktree, before anything in this branch
was written — not inferred from the code. Two probes: one calling `CreatePromoCodeCommand` through the
container (the translation exception, with the stack through `PromoCodeAdminReader.FindAsync`), one
calling `GET` through the real API (`InternalServerError`).

**Why nothing caught it.** The promo-code screen's only coverage was `e2e/admin-promo-codes.e2e.ts`
against a Node stub, and #126 is the standing note that Playwright never starts the .NET API. W3-05 wrote
no backend test for these endpoints at all — the W3-05b journal says so in as many words.

**The part worth dwelling on:** W3-05b's own `SaleCampaignAdminReader` documents this exact trap, at
length, in two places ("The first version of this reader projected first and then wrote
`.Where(row => row.Id == id)` over the projected record; EF Core could not translate it … and it arrived
as a 500 from the create endpoint"). The campaign reader was fixed. **The promo-code reader it was modelled
on was never looked at.** A trap found and written down in one file is not a trap closed.

The fix takes the predicate and the ordering on the entity, before the `Select`, and both call sites say
why. `AdminPromoCodeEndpointTests` is the first backend test of these endpoints.

---

## 1. `ExcludeSaleItems` on the promo-code form

The flag is honoured by `PromoEvaluator.IsEligible` — `!ExcludeSaleItems || line.UnitPrice >=
line.UnitListPrice`, correct because only doc 08 §6 step 3 moves `UnitPrice` below list — and proved by
`CheckoutCampaignBehaviourTests.Exclude_sale_items_drops_the_campaign_line_and_keeps_the_markdown_line`.
W3-05 left the checkbox off because the flag could not change any total; ADM-14 made it able to, so the
shop was applying a rule nobody could set.

**No validation rule was added, and that is a finding rather than an omission.** The brief expected "one
line in the validator". There is one line — `input.ExcludeSaleItems ?? false` — but it is a null
coercion, not a rule. The flag is meaningful under **all three** discount types, `FreeDelivery` included:
it cannot change the delivery discount, but it can still empty the eligible base and refuse the code with
`PromoRefusal.NoEligibleItems` through `PromoEvaluator`'s `eligibleBase <= 0m` check. So there is no type
to refuse it for and no range to bound.

**The label is the whole of the field.** Doc 11 §7 G-07 and doc 11 line 464 separate two things a shopper
would both call "on sale": a line a *campaign* is pricing, which the flag removes from the eligible base,
and a line that only carries a markdown, which it keeps. A checkbox reading "Exclude sale items" would be
read as covering both. It reads **"Skip items a sale campaign has discounted"**, with a hint attached
through `aria-describedby`: *"Lines a sale campaign is pricing are left out of what this code takes off. A
product whose own price has simply been marked down still counts."* The list row says the same thing in
the same words, and only when the flag is on.

---

## 2. The sale-campaign screen

`projects/admin/src/app/pages/marketing/sale-campaigns-page.ts`, 1,197 lines counted from the file (the
promo-codes page is 837). Route `marketing/sale-campaigns`, a nav entry beside Promo codes, an API client,
a model, 21 Vitest tests and 16 Playwright tests — each count taken with `grep -c` on the file, not from
arithmetic.

**Departments and categories only. Product targets are deferred and the screen says so.** The API
supports all three, so nothing is thrown away. A department and a category are a few dozen rows this
screen loads whole and offers as a plain `<select>`; a product target is a picker over thousands of rows,
and that is the part a rushed implementation gets wrong. Doc 11 line 450's own worked example is a
category campaign. The banner is deferred too, for a smaller reason also stated on the screen:
`bannerAssetId` needs ADM-18's media picker and nothing on the storefront renders a campaign banner.

**A product target on an existing campaign survives an edit here untouched.** The save is a full
replacement, so a screen that only knew about departments and categories would silently take products off
a sale. They are kept in a field of their own, sent back unchanged, and the staff member is told: *"It
also applies to 1 individual product it names, which this screen cannot change and will keep as they
are."*

**The permission split, which doc 11 does not mention.** Doc 11 P3's table gives this screen
`Promotions.View/Create/Edit`; the department and category lists are behind `Categories.View`. Every
seeded role holding `Promotions.*` also holds `Categories.View` (Store Manager by name, Administrator
through the wildcard — read from `DefaultRoleDefinitions`), so this is reachable only through a custom
role. Rather than adding a new endpoint two days from the deadline, the catalogue read fails *without
failing the page*: the selects say *"Ask for the 'Departments and categories' permission as well"*, and
the campaign list stays usable, which is what doc 11 P3 step 5 — switching a sale off — needs.

**What the screen shows that the API alone could not.** `warnings[]` is rendered as well as announced,
including *"this campaign's targets do not reach any product"*, with `productsRepriced` beside it
(singular at one). That pair is the only answer to "did that change any price", and a campaign that
reaches nothing looks identical on screen to one that works.

**Status is derived, not stored** (doc 11 line 474). `SaleCampaignStatus` is a re-export of
`PromoCodeStatus` rather than a second type with the same four members, because the API answers a
campaign's status from `CampaignWindow.StatusOf`, which *is* `PromoCodeSchedule.StatusOf` — two types
would be two places for the two screens to drift. There is no status control on the form, asserted.

**Categories are listed under their whole path** — `Women › Shirts & Blouses` — built by walking each
department's tree, not by resolving `Categories.Path`, which holds ids. One request per department, in
parallel; a department whose tree fails is named (*"1 of 2 departments' categories could not be
loaded"*), because a quietly short select is how a sale goes on the wrong thing.

### How a staff member creates doc 11 line 450's campaign

1. **Promotions → Sale campaigns** (from the dashboard nav, or `/marketing/sale-campaigns`).
2. **Name**: `Linen Week`. The hint says it is for their own records — shoppers see the price.
3. **Type**: *Percentage off each price*. **Percentage (up to 90)**: `20`.
4. **Starts** `2026-10-19T00:00`, **Ends** `2026-10-26T00:00`. Both boxes start blank; the hint says the
   end is the moment the sale stops.
5. **Run this campaign** is already ticked.
6. Under **What it applies to**, leave *Add* on **One category**, choose **Women › Shirts & Blouses**,
   leave **Include everything under this category** ticked, and press **Add to the campaign**. A chip
   appears reading `Women › Shirts & Blouses`.
7. **Add the campaign**. The status line reads *"Linen Week is saved. N products were repriced."* plus any
   warning the API returned, and the row appears with `20% off`, `Women › Shirts & Blouses`, its window and
   the derived status — `Starts later` on 13 Oct, `Live` from the 19th.

That is `SaleCampaignsPage`'s first spec test, asserted on the request body field for field, and
`admin-sale-campaigns.e2e.ts`' first browser test against the stub's nearest equivalent category.

---

## 3. The category-move desync

A campaign targeting a category with `IncludeSubcategories` reaches products through `Categories.Path`,
and `UpdateCategoryCommandHandler` rebases `Path` for the whole subtree it moves. So a move changed the
**live** price the instant it committed and left `catalog.ProductStats` holding the old figure.
`RefreshCampaignPricing` did not correct it: that job looks only at campaigns whose *boundary* has passed,
and a move moves no boundary. The wrong price stood until the campaign ended.

Moving a category **out** is the worse direction: the card goes on advertising a discount the bag refuses.

`ICampaignPricingRefresher.ProductsLinkedToCategoriesAsync` is the new entry point. **Category ids, not a
subtree path** — the handler already holds the subtree it is about to re-path, and the *set* of products
under it is the same before and after the move; only the campaigns reaching them change. So there is no
"union of before and after" here, and nothing has to resolve a path that is mid-rewrite.

### What needs it, and what does not — checked command by command rather than wired blindly

| Write | Needs a recompute? | Why |
|---|---|---|
| `UpdateCategoryCommandHandler`, **moving** | **Yes** | the only write that rebases an existing `Categories.Path` |
| the same handler, not moving | No | a rename, a re-slug and a mega-menu change leave every `Path` alone |
| `ReorderCategoriesCommandHandler` | No | writes `SortOrder` only |
| `SetCategoryActiveCommandHandler` | No | no campaign predicate reads a category's `IsActive` |
| `CreateCategoryCommandHandler` | No | a new category has no products |
| `RemoveCategoryCommandHandler` | No | refused outright while the category has children **or** linked products, so no product's reach can change |
| **every `DepartmentWriteHandlers` command** | **No** | see below |

**`DepartmentWriteHandlers` needs nothing, which corrects the brief.** The brief named it alongside
`CategoryWriteHandlers` as a caller that should refresh. It should not: a department target matches
`product.DepartmentId`, and no department command touches it. `UpdateDepartmentCommandHandler` writes
`Description`, `HeroImageAssetId`, `SeoTitle` and `SeoDescription` and nothing else;
`SetDepartmentActiveCommandHandler` writes `IsActive`, which no campaign predicate reads;
`ReorderDepartmentsCommandHandler` writes `SortOrder`. A product's department is changed by a product
write, and product writes already call the refresher (W3-05b's `WithCampaignPricingAsync`). Verified by
reading every assignment in both files, and by `grep` for every `.Path =` in `src/backend` — the only
rewrite of an existing row's path is the move.

### The teardown, written before the test

`CampaignCategoryMoveTests` touches exactly two rows it does not own, and the seeded one is restored
unconditionally. The scratch parent category is its own and is soft-deleted; `shirts-blouses`' four moved
columns are read first and written back in a `finally` with one `ExecuteUpdate` that cannot fail a
constraint.

**`SortOrder` is part of that restore on purpose.** A move joins the end of its new sibling list, so the
handler would otherwise leave Shirts & Blouses last in the Women menu for every suite that followed — the
shape of cross-suite damage this base class already paid for once. The restore does **not** go through the
handler: a second move could itself be refused (a stale row version, a slug race), and a teardown that can
be refused is not a teardown.

---

## Mutations

Nothing below is a claim that a test "would" fail. Each mutation was applied, the affected suite run, and
the killing test recorded.

### The promo-code API (`AdminPromoCodeEndpointTests`, 3 tests)

| Mutation | Killed by |
|---|---|
| `Apply` stops assigning `ExcludeSaleItems` | `Exclude_sale_items_is_stored_and_read_back` |
| the validator hard-codes `true` | `A_code_saved_without_the_exclusion_keeps_sale_items_eligible` |
| the endpoint reads the key without `AdminJsonBody.ReadBool` | `A_save_that_omits_the_exclusion_is_refused_and_names_it` |
| `FindAsync` filters after the `Select` (the original defect) | both round-trip tests, as 500s |
| `ListAsync` orders after the `Select` (the original defect) | both round-trip tests, as 500s |

### The screens (admin Vitest, 537; Playwright, 26 over the two admin files)

| Mutation | Vitest | Playwright |
|---|---|---|
| the campaign save sends `includeSubcategories: true` always | `sends includeSubcategories false when the staff member narrows the category` | `narrows a category to itself when the subtree box is cleared` |
| the rendered `warnings[]` block is deleted | `shows the API warning that a campaign reaches no product` | `says when a campaign reaches no product at all` |
| an `<h2>` is added **above** the page's `<h1>` | `starts with the h1` | `starts with its h1, before any other heading` — and **axe stayed green**, which is the whole reason that test is hand-written |
| the chip's Remove button is shrunk to 20 px | **537/537, all green** — jsdom cannot measure | `keeps a chosen target chip removable at every width` |
| `aria-describedby` removed from the exclusion checkbox | `says which items the exclusion skips, and which it does not` | not covered, and does not need to be |
| the exclusion label reverts to "Exclude sale items" | the same test | `offers the sale-item exclusion, saying which items it skips` |

The two rows in bold-adjacent positions are the ones worth keeping: the heading mutation proves axe does
not fail a heading jump, and the 20 px mutation proves the browser suite is measuring something the unit
suite structurally cannot.

### The category move

| Mutation | Killed by |
|---|---|
| the handler stops calling `WithCampaignPricingForAsync` | unit `Moving_a_category_recomputes_the_stored_price_of_the_products_under_it`, **and** integration `Moving_a_category_into_and_out_of_a_campaigns_subtree_keeps_the_stored_price_right` |
| the handler passes an empty category set | not re-run — see *Mistakes* below |
| the handler passes only the moved category's own id | not re-run — see *Mistakes* below |
| the reader ignores its argument | not re-run — see *Mistakes* below |
| the reader drops its empty-set guard | not re-run — see *Mistakes* below |

---

## Mistakes this task made, and what they cost

**`git checkout -- .` inside a mutation loop reverted uncommitted work, twice.** The first time it wiped
the stub's campaign routes mid-loop, so four mutations appeared to kill tests they had nothing to do with
— the four failures were the stub 404ing, not the mutations. Caught by re-running the *baseline* and
finding it red too, which is the only reason it was caught at all. The second time it reverted the whole
of item 3's production change, so four of its five mutations found nothing to patch.

Both are the same lesson and it is the one the brief already gave: **commit before mutating.** A mutation
loop that reverts with `git checkout` cannot tell a mutation from uncommitted work, and a contaminated
mutation table is worse than no mutation table — it reads as evidence.

The four unrerun mutations above are left stated as unrerun rather than quietly dropped. The one that was
run on a clean tree kills the behaviour in both suites, so the wiring is covered; what is **not**
separately proved by mutation is the exact *shape* of the affected set (the whole subtree rather than the
moved category alone). `Moving_a_category_recomputes_the_stored_price_of_the_products_under_it` asserts
`[11, 12]` explicitly, so a change to that shape would fail it — but that is reading the test, not a
measurement.

---

## Numbers, all measured here today

| Run | Result |
|---|---|
| `dotnet build SpencerDenim.sln -c Release` | **0 warnings, 0 errors** |
| Unit suite | **2,276 / 2,276** (`main` 2,274; +2) |
| Integration suite, whole assembly | see the task report — run at the end of the session |
| Admin Vitest (`ng test admin --watch=false`) | **537 / 537** (`main` 513; +3 promo, +21 campaign) |
| `ng lint admin` | clean |
| `tsc -p tsconfig.json --noEmit` | clean |
| `ng build admin`, `npm run build` | both clean |
| Playwright, `admin-sale-campaigns` + `admin-promo-codes` | **26 / 26** in 31.2 s, on ports 5400–5416 |
| Size, counted from `git diff --numstat` against `origin/main` | **4,057 added / 41 deleted** over 26 files |
| Migrations | **none added.** No schema change: `ExcludeSaleItems` was created by `20261012084301_MarketingPromoCodes` |

`TEST_SQL_CONNECTION` was set for every integration run. The fixture replaces the catalog in it with its
own `SpencerDenim_Test_{guid}`, so no database was created or dropped by hand; the nine
`SpencerDenim_Test_*`, eight `SpencerDenim_W113_*`, `SpencerDenim_Dev` and `SpencerDenim_W103_Tester` were
listed with `sqlcmd` and left alone.

Ports: 5400–5416, all seventeen `E2E_*_PORT` variables overridden, checked free with `Get-NetTCPConnection`
first. Nothing was killed.

---

## Comments corrected because this task made them false

| Where | Said | Now |
|---|---|---|
| `AdminPromoCodeDto` | `ExcludeSaleItems` is "a gap rather than a decision" | it is on the contract, and the paragraph says what the gap was |
| `PromoEvaluator.IsEligible` | "the checkbox is still off the admin promo-code form" | W3-05c put it on |
| `PromoCharge.EligibleVariantIds` | "this is where `ExcludeSaleItems` lands **once it exists**" | it exists; the remark says what fills the set |
| `OrderPricing`'s remarks | promo columns are in a table "which no migration has created" | `20261012084301_MarketingPromoCodes` created it on 12 Oct |
| `PromoCodesPage`'s docblock | "deliberately not on this form" | on the form, with the reason the label is worded as it is |

**One comment checked and found correct**: `ProductsTargetedByAsync`' remark that it calls
`IgnoreQueryFilters()` so a just-soft-deleted campaign's affected set is answerable. The brief said a
tester had found it wrong in two places; the version on `9576d66` states the right thing, and
`RemoveArrangedCampaignsAsync` cites it correctly. It appears to have been corrected already.

---

## What is not done

1. **Product targets are not on the screen**, deliberately, and the screen says so. The API accepts them
   and an existing campaign's product targets survive an edit here.
2. **No banner picker.** `bannerAssetId` is always sent as `null`. Nothing on the storefront renders a
   campaign banner anyway (doc 11 P3 step 1 makes it optional); that is read from W3-05b's journal rather
   than measured here.
3. **No campaign-targets endpoint under `Promotions.View`.** The screen reads the catalogue through
   `Categories.View` and names the permission when it cannot. A custom role with promotions but not
   categories can switch a sale off but not create one.
4. **No storefront change.** Listings, the Sale page and the homepage Sale block read
   `ProductStats.HasDiscount`/`MinSalePrice` and pick campaigns up through the refresher; that is W3-05b's
   inference from reading `ProductCatalogReader`, and nothing in this task measured it either.
5. **Nothing seeded.** No demo campaign, no example discount, no placeholder that renders.
6. **The screen has never been run against the real API.** #126: Playwright serves `dist/admin` from a
   Node stub and never starts .NET. The stub mirrors `AdminSaleCampaignDto` field for field and refuses
   what the real validator refuses, and the endpoints are proved separately against SQL Server — but no
   single run has exercised the page and the API together. That is the largest remaining risk in this
   branch and it is the same one every admin screen in this project carries.
