# 2026-10-13 — W3-05b, sale campaigns (ADM-14)

Branch `feature/W3-05b-sale-campaigns`, from `origin/main` at `62bd88e`. Not pushed, no PR.

**`origin/main` has moved on while this branch was being built** — it is `03a4feb` now, two commits
ahead of the base: `ddd199e` (W4-01 payment groundwork) and `03a4feb` (its review fix). No new migration
came with them, so `20261013040644_MarketingSaleCampaigns` is still last and the snapshot diff below is
measured against the base, `62bd88e`, not against today's `origin/main`. Four files are touched by both
sides and will want a small merge: `Infrastructure/DependencyInjection.cs`,
`Infrastructure/Sales/OrderPlacementStore.cs` (one line here — `SaleCampaignId = line.Priced.SaleCampaignId`),
`IntegrationTests/Sales/SalesSchemaTests.cs` and `UnitTests/Application/Sales/PlaceOrderCommandHandlerTests.cs`
(one `null` added to a line-builder helper). None of them is in the pricing logic.

**There is no CI.** GitHub Actions has been dead since 9 Oct 09:21, so every figure below comes from a
run on this machine, in this worktree. Where a figure is an estimate or an inference rather than a
measurement, it says so.

---

## What this task was

Doc 02 line 119, **ADM-14**: "Sale campaigns: discount on categories/products for a date range."
Specified in full already — doc 08 §3.6 (`marketing.SaleCampaigns`, `marketing.SaleCampaignTargets`),
doc 08 §6 step 3 (the pricing rule), doc 08 §7.4 (the stored summary and its five-minute staleness),
doc 11 P3 section A (the staff flow) and doc 11 line 450 (the worked example).

The job was to follow it, not to design it.

## Order of work

Built in the order that leaves the most working if it stops early, which it did not:

1. the pricing rule as a pure function, with its unit tests;
2. the two tables, the foreign key the orders migration deferred, and the schema and model guards;
3. the **live** read, wired into the bag, the checkout quote and the placement — the money path;
4. the integration tests, including doc 11 line 450 placed as a real order;
5. the plan test for the filtered index;
6. the batched recompute of the stored summary and the five-minute job;
7. the admin API;
8. a defect found on the way: a product saved during a live campaign.

The admin **screen** (Angular) is not built. See *What is not done*.

---

## The pricing rule

`CampaignPricing.Best(offers, listPrice)` is the whole of doc 08 §6 step 3: "Percent →
`round(list × (1 − v/100))`; Fixed → `list − v`. Lowest wins, floor at Rs. 1.00, no stacking."

Three things about it were worth being careful with.

**"Lowest wins" is about the result, not the discount.** A 30%-off campaign and a Rs. 500-off campaign
change places at a list price of Rs. 1,666.67, so one pair of campaigns is won by different campaigns on
two products — and on two variants of one product, because a variant may carry a `PriceOverride`. That is
why `Best` takes a list price and not a product, and why nothing caches a winner per product.

**Specificity does not win.** Nothing in doc 08 §3.6, doc 08 §6 or doc 11 P3 ranks a product target above
a department target. `CampaignOffer` therefore does not carry what targeted it: the resolver cannot
prefer a target type because it is never told one. Tested in both directions, on real rows.

**Ties are broken by the lower id.** Two campaigns can resolve to the same price; which one wins is
arbitrary, but it has to be the *same* arbitrary one on the bag, on the quote and at placement, because
`sales.OrderLines.SaleCampaignId` is stored.

## The live path and the lagging path

Doc 08 §7.4 gives `ProductStats`' pricing columns a staleness of "≤ 5 min at campaign boundaries" and
says in the same row that "checkout always prices live". Both halves are built, and a test pins them
against each other:

- **Live** (`ICampaignOfferReader`): the bag, the checkout quote, the placement, and — added for the
  reason doc 08 §7.5 gives — the product detail page.
- **Lagging** (`ICampaignPricingRefresher`): listings, the Sale page (DEPT-03) and the Sale homepage
  block (HOME-07), all of which read `ProductStats.HasDiscount` and `MinSalePrice`.

`CheckoutCampaignBehaviourTests.The_bag_prices_live_while_the_stored_summary_still_lags` asserts the
*disagreement*: the bag charges Rs. 4,392.00 while `MinSalePrice` still says Rs. 5,490.00. Asserting only
the bag would go on passing if somebody later pointed checkout at the summary, which is the change this
pins against.

## The recompute: failure, overlap, and racing an edit

Doc 08 §7.4 requires each batch to be **one `UPDATE … FROM`** of ≤ 1,000 products computing straight from
the source tables with `WITH (READCOMMITTEDLOCK)` on them. The lock hint is the part that is easy to drop
and hard to notice: without it, a database with `READ_COMMITTED_SNAPSHOT ON` would let the statement price
from a snapshot taken before a product save that commits during it, and then write that over the save.

- **Fails halfway** — one `UPDATE` is atomic, so the batches that ran are correct and the rest are stale.
  Nothing needs repairing: every statement recomputes the whole answer, so the next pass or the next save
  converges. The job does not advance its cursor on a failure, so the window widens rather than skipping.
- **Two instances overlap** — both compute from the same source rows and write the same values. No lease,
  and `ops.JobLeases` still does not exist (the same argument `ExpireUnpaidOrdersService` and
  `AutoDeliverShippedService` make for themselves).
- **Racing an edit** — the statement reads the campaign rows inside itself, so it sees the campaign before
  or after the edit, never halfway; and the edit's own save path recomputes the same products. Neither is
  an increment, so the order does not matter. The worst outcome is one interval of lag on a listing.

All three are tested as properties rather than as timing.

**It is a second implementation of `CampaignPricing`, in T-SQL.** That cannot be avoided — §7.4 requires
the arithmetic inside one statement — so it is managed instead:
`CampaignPricingRefreshTests.The_stored_summary_agrees_with_the_live_price_for_every_product` drives a
matrix through both paths and compares all four stored columns. The expectation is computed from the live
reader, never typed.

## Two things the equivalence test did not catch until they were measured

Both are now in the arrangement rather than in a comment.

1. **Comparing only the targeted products made an over-wide T-SQL rule invisible.** An `UPDATE` writes
   only the ids it is given, and the ids came from the C# targeting — so the compared set was exactly the
   set both rules agree about. Widening the T-SQL's subtree test to "same department" was **green**. The
   test now refreshes and compares the whole catalogue, and that mutation fails.
2. **With no inactive or out-of-window campaign in the matrix, deleting `c.IsActive = 1` from the
   statement was green.** The matrix now carries three campaigns that must price nothing — inactive, not
   started, already ended — so each of the statement's three exclusions is covered.

## The filtered index, and a correction

`IX_SaleCampaigns_Window` is filtered `WHERE IsActive = 1 AND IsDeleted = 0`, so it needed a plan test
that observes the real query. `CampaignOfferReader.Query` is a separate method for exactly that: the test
takes `ToQueryString()` of the production query rather than rebuilding it, which is the trap this
repository has already fallen into (a plan test that stayed green with its index dropped, 20,101 logical
reads against 18).

**The correction: the live campaign read measurably does not seek that index, and should not.** It asks
"which of these products does a live campaign target", so the selective side is `SaleCampaignTargets`; the
plan drives from `PK_SaleCampaignTargets` and `IX_ProductCategories_ProductId` and reaches each candidate
campaign by `PK_SaleCampaigns`. Measured with 3,000 ended campaigns in the table and statistics updated:
the plan is unchanged, so it is the join order and not the row count.

The index is justified by the other query shape — "which campaigns are live now", which is
`RefreshCampaignPricing`'s (doc 08 §7.4) — and that test seeks it. Two further measurements behind it:

- with the instant as a `DECLARE`d variable, the estimated plan **scans** even on 3,000 rows: an estimated
  plan cannot sniff a local variable, so an inequality is costed at a ~30% guess and the key lookups lose.
  A real parameterised statement is sniffed on first compile, so this is an artefact of reading an
  estimated plan rather than a prediction about the job.
- the plan names the index under `<UnmatchedIndexes>` **as well as** in the seek, because the lookup back
  to the clustered index for `DiscountType` and `DiscountValue` is a part the filtered index cannot serve.
  So only the positive half is asserted, with the reason written down.

## A defect this branch opened and fixed

`ProductDerivedData.RefreshStats` computes `ProductStats`' pricing columns from the product and its
variants alone — it is a pure function over the aggregate and cannot see a campaign — so a product edited
**during** a live campaign had its stored sale price written back up to its list price. And
`RefreshCampaignPricing` would not have corrected it: that job looks only at campaigns whose *boundary*
has just passed, and a product save moves no boundary. The undiscounted figure would have stood on every
card, listing and Sale page until the campaign ended.

The money path was never affected, which is what would have made it hard to spot: the card and the bag
would have disagreed, in the shopper's favour.

Fixed by `CatalogWriteSupport.WithCampaignPricingAsync`, which runs the refresher for that one product
after the save commits and only on a save that wrote something.

## Comments this branch made false, and corrected

W3-05 wrote four claims that ADM-14 has falsified. Each is corrected rather than left:

| Where | Said | Now |
|---|---|---|
| `PromoEvaluator.IsEligible` | `ExcludeSaleItems` "excludes nothing in this build" | it excludes a real line, with the test that proves it |
| `PromoRefusal.NoEligibleItems` | "cannot happen until sale campaigns exist" | a reason a shopper can now be given |
| `AdminPromoCodeDto` | the checkbox is "deliberately absent" | a **gap**, not a decision — see below |
| `OrderLine.SaleCampaignId` | "no migration has created it" | a real constraint |

## Numbers, all measured here today

| Run | Result |
|---|---|
| `dotnet build SpencerDenim.sln -c Release` | **0 warnings, 0 errors** |
| Unit suite | **2,106 / 2,106** (main was 2,077; +29) |
| Integration suite | **1,446 tests**; whole-assembly runs of 1,444 (two cross-suite assertions, fixed), 1,442 (four `CartStaleItemTests`, a real fixture collision, fixed) and 1,445 (one `CheckoutPromoRaceTests`, see below). 21 m 2 s / 19 m 19 s / 21 m 5 s. Main was 1,401; +45 |
| `dotnet ef migrations has-pending-model-changes`, after a rebuild | "No changes have been made to the model" |
| Snapshot diff vs the branch base `62bd88e` | **+187 lines, 0 deletions** — purely additive |
| `dotnet ef migrations remove` | never run; the one empty migration was removed with `rm` and regenerated |
| Size, counted from `git diff --stat` against the base | production 3,581 added / 296 changed over 37 files; tests 3,097 added over 13 files; the migration and its designer and snapshot 5,103 added over 3 |

`TEST_SQL_CONNECTION` was set to `(localdb)\MSSQLLocalDB` for every integration run. The one empty
migration was the `--no-build` stale-binary trap the W1-03 journal records: it generated an empty `Up()`
because `SpencerDenim.Api`'s copy of the Infrastructure assembly was stale.

## Mutations

Nothing below is a claim that a test "would" fail. Each mutation was applied, the suite run, and the
killing test recorded.

**The pricing rule** (`CampaignPricingTests`, 27 tests):

| Mutation | Killed by |
|---|---|
| floor removed | `A_fixed_amount_never_prices_below_one_rupee`, `The_floor_bites_only_below_one_rupee`, `A_campaign_that_cannot_beat_the_list_price_does_not_win` |
| banker's rounding instead of away-from-zero | `A_half_rupee_cent_rounds_away_from_zero` |
| tie-break `<` → `<=` | `A_tie_is_broken_by_the_lower_id_whatever_the_order` |
| tie-break by higher id | the same |
| first offer always wins | the two crossover tests, the specificity test, the tie test |
| `>= listPrice` → `> listPrice` | `A_campaign_that_cannot_beat_the_list_price_does_not_win` |
| percentage inverted (`v/100`) | 14 of 27 |
| window end made inclusive | `A_campaign_is_live_from_its_start_until_its_end` |
| window start made exclusive | the same |

**The live path** (`CheckoutCampaignBehaviourTests`, 11 tests):

| Mutation | Killed |
|---|---|
| campaign end bound dropped | 2 |
| `IsActive` dropped | 1 |
| soft-delete filter bypassed (`IgnoreQueryFilters`) | 8 |
| category target widened to the department | 2, incl. the worked example |
| placement stops recording the campaign id | 4 |
| checkout ignores campaigns | 8 |
| `ExcludeSaleItems` compares the markdown instead of the campaign | 1 — the new G-07 test, and only it |

**The T-SQL recompute** (`CampaignPricingRefreshTests`, 7 tests): the fixed-amount floor removed, the
percent value rounded to 0 dp, the percentage inverted, `MIN` → `MAX`, `FLOOR` → `ROUND` on the discount
percent, the subtree flag ignored, and each of `IsActive` / the start bound / the end bound deleted in
turn — nine mutations, all killed. Two of them only after the arrangement was widened; see above.

**The plan test**: dropping `IX_SaleCampaigns_Window` and re-running turns the plan to `PK_SaleCampaigns`
and fails the seek assertion.

**The product-save fix**: removing the `WithCampaignPricingAsync` call leaves
`A_saved_product_has_its_campaign_pricing_recomputed` red.

## Decisions this task made that no document states

Listed so a reviewer can disagree with them.

- **`CK_SaleCampaigns_DiscountValue` has a lower bound of 0** for a percent campaign. Doc 08 §3.6 gives
  only the 90 ceiling. A campaign of 0% is a sale the shop advertises that discounts nothing.
- **At most 200 targets per campaign.** Neither document bounds the collection. What the bound stops is a
  mistyped request making the validator do hundreds of thousands of existence checks.
- **A duplicate target is refused, not de-duplicated.** It changes no price, but a staff member who added
  Women twice has made a mistake, and tidying it silently would also tidy the case where they meant two
  different categories.
- **A tie between two campaigns is broken by the lower id**, not by "ends soonest" — which would change
  the recorded winner as a clock moved.
- **`WhichTargetsExistAsync` does not filter departments by `IsActive`.** A campaign may legitimately be
  set up for a department the client has temporarily switched off.

## The fixture collision, which only a whole-assembly run showed

The second whole-assembly run reddened **four of `CartStaleItemTests`' twelve tests**, and that class
passes 12/12 on its own. Reproduced with the two classes alone in sixteen seconds, and the error names
itself: *"The UPDATE statement conflicted with the CHECK constraint CK_StockLevels_ReservedLeOnHand"*.

`CheckoutTestBase`'s own remarks describe the mechanism. Those tests set `OnHand` to 0 or 1 on variants
from the **low** end of the catalogue; this suite has to use the linen shirt and the jeans, because doc 08
§6's worked example is about those two products, and they are `SD-W-0001` and `SD-W-0009` — also at the
low end. A placed order holds its reservation for ever, so `Reserved` stayed above zero and their `UPDATE`
was refused. The promo suites never hit it because `CheckoutVariantIdsAsync` hands out the *highest* ids
for precisely this reason; a campaign suite cannot use it, because it needs named products.

**The fix gives the stock back rather than restoring a number.** `ArrangeStockAsync` records every variant
the suite touches, and the teardown cancels each order still holding one through the real
`IOrderCancellationStore` — a transaction with its status change and history row, not a hand-written stock
update, which would leave an order claiming a reservation it no longer has (the state `ReconcileStock`
exists to alert on). It runs *before* the base's restore, so `held` is zero by then and `OnHand` goes back
to exactly the seeded figure.

**Why it matters beyond this branch:** the first whole-assembly run passed `CartStaleItemTests`, so a
filtered run — or one lucky full run — would have let this through to a tester as "flaky". It is not
flaky; it is deterministic given the class order.

## The teardown, and the mistake that cost four classes

Worth reading before anything else in this file, because it is the biggest thing this task got wrong.

The campaign suites place orders on the **linen shirt** and the **jeans**, because doc 08 §6's worked
example is about those two products. They are `SD-W-0001` and `SD-W-0009`, at the **low** end of the
catalogue — which is where `CartStaleItemTests` takes its variants from, and that class sets `OnHand` to 0
or 1. A placed order holds its reservation for ever, so `Reserved` stayed above zero and
`CK_StockLevels_ReservedLeOnHand` refused its `UPDATE`. The promo suites never hit this because
`CheckoutVariantIdsAsync` hands out the *highest* ids for exactly this reason; a campaign suite cannot use
it, because it needs named products.

**The first fix made it much worse.** It released the stock by cancelling the orders through the real
`IOrderCancellationStore` — which looked more honest, a release with its status change and history row.
But the query could not tell this test's orders from any other order holding the same variant, so a test
that placed nothing at all tried to cancel an order a previous test had placed and whose stock the base
had already rebalanced. `OrderStockRelease` refused it, **correctly**, and the exception came out of
`DisposeAsync` *before the campaigns were removed*. A Rs. 99,000-off campaign then stayed live for the
rest of the assembly: fifteen failures across four classes, several of them reading Rs. 1.00 where a real
price was expected.

Two lessons, and the second is the one that generalises:

1. The stock goes back with one statement per variant — `OnHand = OnHand − Reserved, Reserved = 0`, which
   yields exactly the seeded pair and **cannot throw**. The orders keep a reservation the stock row no
   longer reflects; in a throwaway test database, for orders this suite placed itself, that is the right
   trade.
2. **A teardown must be ordered by danger, and every step must be in its own `try`/`finally`.** A
   campaign left live reprices the catalogue for every class that follows, so it is removed first and
   nothing that can throw is allowed before it. The first version had the fragile step first, and that
   single ordering mistake turned one broken teardown into fifteen red tests with no visible connection
   to campaigns.

## The one failure I could not reproduce, stated as measured

The third whole-assembly run failed **one** test:
`CheckoutPromoRaceTests.Four_simultaneous_checkouts_take_only_the_last_use_a_code_has`. It is a promo-code
test from W3-05, not a campaign test.

Everything measured about it:

| Observation | Result |
|---|---|
| Whole-assembly run 1 | pass |
| Whole-assembly run 2 | pass |
| Whole-assembly run 3 | **fail** |
| That class alone, three times | pass, pass, pass (5/5 each) |
| That class together with all three campaign suites, twice | pass, pass (23/23 each) |

Seven of eight observations pass, and **the failure message was not captured** — the run's output was
piped through a filter that dropped it, which is a mistake in how the run was invoked rather than a fact
about the test. The fourth run was therefore invoked unfiltered.

**What is honestly known about the cause: not enough.** Two things are worth the next person's attention
rather than a conclusion:

- The test's own remarks say the number of contenders that reach the contended statement is "an assertion
  about scheduling", and it settles for `>= 2` of 4 for exactly that reason. Its guard is a measurement of
  the scheduler, so it is sensitive to machine load, and a 21-minute whole-assembly run is the loudest
  load this suite ever sees.
- W3-05b **does** add one query to the checkout read on the placement path (the live campaign read),
  before the promo section. That is a plausible contributor to the timing and it is unproven: the same
  code passed the test in two full runs and in five targeted ones.

It is **not** called flaky here. It is one failure in eight observations that I could not reproduce in
five attempts, on a test whose own assertion is about scheduling, and that is all the evidence there is.

## What is not done

Stated plainly, because two days before the deadline this matters more than the rest of this file.

1. **There is no admin screen.** The API is complete and tested (list, create, edit, remove, with
   targets, validation, permissions, concurrency and the recompute), but no Angular page calls it. A staff
   member cannot create a campaign from the admin app today; the feature is reachable only by an
   authenticated HTTP client. This is the largest remaining piece of ADM-14 and it is a front-end task:
   a page in `projects/admin/src/app/pages/marketing/` beside `promo-codes-page.ts`, an API client, a
   route, and its Vitest and e2e suites. `promo-codes-page.ts` is 837 lines, so take that as the scale.
2. **`ExcludeSaleItems` is still off the promo-code form.** W3-05 left the checkbox off because the flag
   could not change any total; it can now, and the evaluator honours it, so a staff member cannot set a
   rule the shop applies. One field, one reader in the endpoint, one validator line — plus the Angular
   control.
3. **No storefront UI work.** Listings, the Sale page (`onSaleOnly`) and the homepage Sale block already
   read `ProductStats.HasDiscount` and `MinSalePrice`, so they pick campaigns up through the refresher
   with no change — that is inference from reading `ProductCatalogReader`, **not** something a browser
   test confirmed. No campaign banner is shown anywhere: doc 11 P3 step 1 makes the banner optional and
   `BannerAssetId` is stored and returned by the API, but nothing renders it.
4. **Nothing is seeded.** No demo campaign, which is the client's decision.
5. **No browser test.** #126: Playwright never starts the .NET API, so a screen test cannot observe any
   of this.

## Notes for whoever picks this up

- `CampaignWindow` delegates its window, its status and its Colombo conversions to `ContentSchedule` and
  `PromoCodeSchedule` rather than restating them. `PromoCodeStatus` on a campaign reads oddly and is
  deliberate: doc 11 P3 gives both features one derived status, and the only way to guarantee the two
  screens agree is for it to be the same function.
- The category subtree predicate goes through `dbContext.ProductCategories` rather than the
  `CategoryLinks` navigation. Translation is identical; the navigation's `Any` is a delegate lambda nested
  in an expression tree, and CA1310 fires on the `StartsWith` that every other subtree predicate uses.
- `AdminJsonBody` is the admin body readers, extracted from `AdminPromotionEndpoints` so both forms use
  one copy. `AdminSaleCampaignEndpointTests` is the first backend test to exercise them — the promo-code
  endpoints had no backend test at all.
- The campaign integration suites remove their campaigns **and** put the affected products' stored
  summaries back. A campaign is global state in a way a promo code is not: the first run of the behaviour
  suite left one Rs. 99,000-off campaign live and seven of eleven tests asserted Rs. 1.00.
