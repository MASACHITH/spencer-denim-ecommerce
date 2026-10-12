using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SpencerDenim.Domain.Marketing;
using SpencerDenim.Domain.Sales;
using SpencerDenim.Infrastructure.Persistence;
using SpencerDenim.IntegrationTests.Infrastructure;

namespace SpencerDenim.IntegrationTests.Sales;

/// <summary>
/// The two promo-code limits under genuine concurrency (doc 08 §3.6; ADM-13).
/// <para>
/// <b>These are the two ways a promo code loses the shop money</b>, and they are different races with
/// different mechanisms:
/// </para>
/// <list type="number">
/// <item><description>
/// <b>The total limit.</b> Two checkouts for the last use of a code. The mechanism is the conditional
/// <c>UPDATE … WHERE UsageCount &lt; TotalUsageLimit</c>, which re-evaluates its predicate after waiting
/// for the other's row lock — the same shape as the stock reservation, and safe for the same reason.
/// </description></item>
/// <item><description>
/// <b>The per-customer limit.</b> Two checkouts <i>by the same email</i>, for a code that allows each
/// customer one use. There is no row to lock: the limit is a <c>COUNT</c> over <c>sales.Orders</c>, and
/// under <c>READ COMMITTED</c> neither transaction can see the other's uncommitted insert, so both read
/// zero and both pass. The only thing standing between them is doc 08 §3.6's
/// <c>sp_getapplock 'promo:{Id}:{sha256(EmailNormalized)}'</c>.
/// </description></item>
/// </list>
/// <para>
/// <b>Proving the second race is a race, and what that cost.</b> The first version of
/// <see cref="Four_simultaneous_checkouts_by_one_email_take_one_use_of_a_per_customer_code"/> gave the
/// four shoppers <b>one</b> variant, and it passed with <see cref="OrderPromoUse"/>'s app lock deleted.
/// Instrumented with <see cref="PromoStatementCounter"/> it was obvious why: the four conditional stock
/// <c>UPDATE</c>s contend for one <c>StockLevels</c> row, so each shopper waited for the previous one to
/// <i>commit</i> before reaching the promo section at all, and the per-customer count was then correct
/// because of a row lock on an unrelated table. The attempts reaching the contended statement were
/// serialised before they got there, so the guard had nothing to do and its deletion changed nothing.
/// </para>
/// <para>
/// So each shopper now holds a <b>different</b> variant, and the test asserts the instrumentation as well
/// as the outcome: all four attempts must reach the per-customer statement. That number is what makes the
/// deletion of the app lock go red rather than merely unproven, and the measured figures are in the method's
/// own remarks.
/// </para>
/// <para>
/// <b>Why the per-customer count runs before the conditional UPDATE in production code.</b> The same
/// instrumentation showed that with doc 11 P6's literal ordering — total limit first — the
/// <c>PromoCodes</c> row lock taken by the <c>UPDATE</c> serialises every contender for the code, so the
/// per-customer count is again correct for a reason nobody wrote. <c>OrderPromoUse.TakeAsync</c> therefore
/// reads first and writes second, which costs a sentence of ordering and buys a guard that can be tested.
/// </para>
/// </summary>
public sealed class CheckoutPromoRaceTests(SqlServerDatabaseFixture sqlServer)
    : CheckoutPromoTestBase(sqlServer)
{
    /// <summary>
    /// How many shoppers race. Four is enough for both races here, unlike the double-submit race's
    /// twenty-four: the window these aim at is the whole promo section of the transaction rather than a
    /// few milliseconds between two statements, and the instrumentation proves all four arrive in it.
    /// </summary>
    private const int Contenders = 4;

    /// <summary>
    /// How many times each race is replayed. A race that resolves correctly once has not been tested;
    /// these are high enough to catch an interleaving and low enough to keep the suite in minutes.
    /// </summary>
    private const int Rounds = 6;

    /// <summary>
    /// How many submissions of one checkout key the double-submit test sends. Eight rather than
    /// <c>CheckoutConcurrencyTests</c>' twenty-four: that test is hunting an interleaving, and this one
    /// reproduces a refusal the app lock makes deterministic.
    /// </summary>
    private const int DoubleSubmissions = 8;

    /// <summary>
    /// <b>The per-customer limit, which is the one place this feature leaks money.</b> Four shoppers, one
    /// email, a code that allows one use each, and four <i>different</i> variants so that nothing but the
    /// app lock can serialise them. Exactly one order exists afterwards, and <c>UsageCount</c> is 1.
    /// </summary>
    /// <remarks>
    /// <b>Measured on this machine, 2026-10-12, six rounds each.</b>
    /// <list type="bullet">
    /// <item><description>
    /// As written: 4 of 4 attempts per round reached the per-customer statement; 1 order placed, 3 refused
    /// with 409 <c>AlreadyUsedByCustomer</c>; <c>UsageCount</c> = 1.
    /// </description></item>
    /// <item><description>
    /// With the <c>sp_getapplock</c> statement deleted from <c>OrderPromoUse.TakeAsync</c>: 4 of 4
    /// attempts reached it and <b>4 orders were placed</b> in five rounds of six and 3 in the sixth — red
    /// on the first round, every run.
    /// </description></item>
    /// <item><description>
    /// With the app lock restored but the four shoppers sharing <b>one</b> variant (the first version of
    /// this test): still 4 of 4 attempts reaching the statement, 1 order — and still 1 order with the app
    /// lock deleted, because the stock row lock had already serialised them. That is the test that could
    /// not fail, and it is why the variants differ.
    /// </description></item>
    /// </list>
    /// </remarks>
    [Fact]
    public async Task Four_simultaneous_checkouts_by_one_email_take_one_use_of_a_per_customer_code()
    {
        var variantIds = await CheckoutVariantIdsAsync(Contenders);
        var colombo = await DistrictIdAsync("Colombo");
        var standard = await DeliveryMethodIdAsync(DeliveryMethod.StandardCode);
        var email = UniqueEmail("percustomer");

        for (var round = 1; round <= Rounds; round++)
        {
            // No total limit, deliberately: if there were one, the conditional UPDATE could be the thing
            // refusing the losers and this test would not be about the per-customer limit at all.
            var code = await NewPromoCodeAsync(
                discountType: PromoDiscountType.FixedAmount,
                discountValue: 100m,
                perCustomerLimit: 1,
                totalUsageLimit: null);

            var shoppers = new List<(CheckoutApiClient Api, object Body)>();

            for (var contender = 0; contender < Contenders; contender++)
            {
                // A different variant each, so the conditional stock UPDATEs take four different row
                // locks and nothing serialises these four before the promo section. See the class remarks.
                await SetStockAsync(variantIds[contender], 10);

                var (_, _, api) = await NewBagAsync(variantIds[contender]);
                var (_, session) = await api.GetAsync(colombo, standard, Token, code.Code);

                Assert.Equal(code.Code, session!.AppliedPromoCode);

                shoppers.Add((api, PromoBody(session, standard, colombo, email, code.Code)));
            }

            using var counter = CountStatements();

            var results = await Task.WhenAll(
                shoppers.Select(shopper => shopper.Api.PlaceAsync(shopper.Body, Token)));

            var created = results.Count(result => result.Response.StatusCode == HttpStatusCode.Created);
            var conflicts = results.Count(result => result.Response.StatusCode == HttpStatusCode.Conflict);

            // **The instrumentation, asserted before the outcome.** Without this, a run in which three
            // attempts short-circuited before the contended statement would look like a pass — which is
            // exactly the shape of the test this replaced.
            Assert.Equal(
                Contenders,
                counter.PerCustomerCountExecutions);

            Assert.Equal(1, created);
            Assert.Equal(Contenders - 1, conflicts);

            foreach (var refused in results.Where(
                result => result.Response.StatusCode == HttpStatusCode.Conflict))
            {
                Assert.Equal(
                    nameof(PromoRefusal.AlreadyUsedByCustomer),
                    await PromoRefusalOfAsync(refused.Response));
            }

            Assert.Equal(1, await UsageCountAsync(code.Id));

            // And the cache agrees with the orders, which is the invariant doc 08 §3.6 defines
            // `UsageCount` as a cache of.
            Assert.Equal(1, await HeldUsesAsync(code.Id, email));
        }
    }

    /// <summary>
    /// <b>The total limit.</b> Four shoppers with four different emails and a code with one use in it.
    /// Exactly one order, and the three losers are told the code is fully used — doc 11 P3's own
    /// exception: "Last use of a code taken by two customers at once → one succeeds, the other is told the
    /// code is fully used and can place the order without it."
    /// </summary>
    /// <remarks>
    /// <b>Different emails, so the per-customer limit cannot be what refuses them</b> — and in fact the
    /// code has no per-customer limit at all, so the only statement that can say no is the conditional
    /// UPDATE. Four different variants again, so the stock rows do not serialise the four before they get
    /// there.
    /// <para>
    /// <b>The instrumentation here is a floor and not an equality, and the instrumentation is what showed
    /// why.</b> The total limit is one of <c>PromoEvaluator</c>'s checks, so a contender that starts after
    /// the winner has committed is refused by the pre-transaction re-validation (doc 11 P3 step 12) and
    /// never reaches the transaction. Measured on this machine: 2 of 4 attempts reached the promo section
    /// and the other two short-circuited — correct, and cheaper, and it means an equality of four here
    /// would be an assertion about thread scheduling. Two is still a contended UPDATE: both read
    /// <c>UsageCount = 0</c> before either committed.
    /// </para>
    /// <para>
    /// <b>Measured, six rounds:</b> ≥ 2 attempts reached the section every round; 1 created, 3 × 409
    /// <c>FullyUsed</c>; <c>UsageCount</c> = 1 = the total limit.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Four_simultaneous_checkouts_take_only_the_last_use_a_code_has()
    {
        var variantIds = await CheckoutVariantIdsAsync(Contenders);
        var colombo = await DistrictIdAsync("Colombo");
        var standard = await DeliveryMethodIdAsync(DeliveryMethod.StandardCode);

        for (var round = 1; round <= Rounds; round++)
        {
            var code = await NewPromoCodeAsync(
                discountType: PromoDiscountType.FixedAmount,
                discountValue: 100m,
                perCustomerLimit: null,
                totalUsageLimit: 1);

            var shoppers = new List<(CheckoutApiClient Api, object Body)>();

            for (var contender = 0; contender < Contenders; contender++)
            {
                await SetStockAsync(variantIds[contender], 10);

                var (_, _, api) = await NewBagAsync(variantIds[contender]);
                var (_, session) = await api.GetAsync(colombo, standard, Token, code.Code);

                shoppers.Add((
                    api,
                    PromoBody(
                        session,
                        standard,
                        colombo,

                        // A different email each: this race must be the total limit's, not the
                        // per-customer limit's.
                        UniqueEmail($"total{round}-{contender}"),
                        code.Code)));
            }

            using var counter = CountStatements();

            var results = await Task.WhenAll(
                shoppers.Select(shopper => shopper.Api.PlaceAsync(shopper.Body, Token)));

            // **Not four here, and the instrumentation is what showed why.** Unlike the per-customer
            // limit, the total limit *is* one of `PromoEvaluator`'s checks, so a contender that starts
            // after the winner commits sees `UsageCount >= TotalUsageLimit` in the pre-transaction
            // re-validation (doc 11 P3 step 12) and is refused there, without reaching the transaction at
            // all. Measured on this machine: 2 of 4 attempts reached the contended section, the other two
            // short-circuited — which is correct behaviour and a cheaper refusal, and it means an
            // assertion of four here would be an assertion about scheduling.
            //
            // Two is still a contended UPDATE, which is what this test is about: both read
            // `UsageCount = 0` before either committed, and one of them was refused by the statement
            // rather than by the pre-check. With the `AND (TotalUsageLimit IS NULL OR UsageCount <
            // TotalUsageLimit)` clause deleted, both succeed and `created` is 2 or more.
            Assert.True(
                counter.PerCustomerCountExecutions >= 2,
                $"Only {counter.PerCustomerCountExecutions} attempt(s) reached the promo section, so the "
                + "conditional UPDATE was never contended and this test proves nothing about it.");

            Assert.Equal(1, results.Count(result => result.Response.StatusCode == HttpStatusCode.Created));

            var conflicts = results
                .Where(result => result.Response.StatusCode == HttpStatusCode.Conflict)
                .ToList();

            Assert.Equal(Contenders - 1, conflicts.Count);

            foreach (var refused in conflicts)
            {
                Assert.Equal(nameof(PromoRefusal.FullyUsed), await PromoRefusalOfAsync(refused.Response));
            }

            Assert.Equal(1, await UsageCountAsync(code.Id));
        }
    }

    /// <summary>
    /// Doc 11 line 494: "Guest uses a per-customer code again with a different email → <b>allowed by
    /// design</b> (limit is per email)." Asserted because it is a rule somebody will read as a hole, and
    /// a later change that "fixed" it would break the shop's own stated behaviour.
    /// </summary>
    [Fact]
    public async Task A_per_customer_code_may_be_used_again_from_a_different_email()
    {
        var variantIds = await CheckoutVariantIdsAsync(2);
        var colombo = await DistrictIdAsync("Colombo");
        var standard = await DeliveryMethodIdAsync(DeliveryMethod.StandardCode);

        var code = await NewPromoCodeAsync(
            discountType: PromoDiscountType.FixedAmount,
            discountValue: 100m,
            perCustomerLimit: 1,
            totalUsageLimit: null);

        foreach (var variantId in variantIds)
        {
            await SetStockAsync(variantId, 10);
        }

        var first = await PlaceWithPromoAsync(
            variantIds[0], colombo, standard, UniqueEmail("one"), code.Code);
        var second = await PlaceWithPromoAsync(
            variantIds[1], colombo, standard, UniqueEmail("two"), code.Code);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        Assert.Equal(2, await UsageCountAsync(code.Id));
    }

    /// <summary>
    /// The same email in a different case is the <b>same</b> customer, because the limit is per
    /// <c>EmailNormalized</c> and <see cref="OrderPromoUse"/> hashes that for its lock resource.
    /// </summary>
    /// <remarks>
    /// Sequential on purpose — this is about the normalisation and not about the race — and it is the
    /// test that would catch a lock resource built from <c>placement.Email</c> rather than from
    /// <c>order.EmailNormalized</c>: two contenders would then take two different locks, and this one
    /// would still pass. So it is paired with the four-way race above rather than standing for it.
    /// </remarks>
    [Fact]
    public async Task A_per_customer_limit_counts_one_email_whatever_its_case()
    {
        var variantIds = await CheckoutVariantIdsAsync(2);
        var colombo = await DistrictIdAsync("Colombo");
        var standard = await DeliveryMethodIdAsync(DeliveryMethod.StandardCode);

        var code = await NewPromoCodeAsync(
            discountType: PromoDiscountType.FixedAmount,
            discountValue: 100m,
            perCustomerLimit: 1,
            totalUsageLimit: null);

        foreach (var variantId in variantIds)
        {
            await SetStockAsync(variantId, 10);
        }

        var email = UniqueEmail("MixedCase");

        var first = await PlaceWithPromoAsync(variantIds[0], colombo, standard, email, code.Code);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        var second = await PlaceWithPromoAsync(
            variantIds[1], colombo, standard, email.ToUpperInvariant(), code.Code);

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal(nameof(PromoRefusal.AlreadyUsedByCustomer), await PromoRefusalOfAsync(second));
        Assert.Equal(1, await UsageCountAsync(code.Id));
    }

    /// <summary>
    /// Doc 11 P6's double-submit rule — "with the same checkout key returns the same order, never a
    /// second one" — <b>when the order carries a per-customer-limited code</b>.
    /// </summary>
    /// <remarks>
    /// <b>This is the one case where the per-customer guard and the idempotency guarantee point in
    /// opposite directions, and it is not the stock race in another costume.</b> The two submissions carry
    /// one checkout key, so <c>PlaceOrderCommandHandler</c>'s step-1 pre-check cannot see the winner (it
    /// has not committed yet) and the loser goes on into the transaction. There it reaches
    /// <see cref="OrderPromoUse.TakeAsync"/>, waits on the winner's <c>sp_getapplock</c>, and then counts
    /// the winner's own order as a held use for its own email — so a per-customer limit of one refuses
    /// the loser <b>deterministically</b>, before <c>UX_Orders_CheckoutIdempotencyKey</c> is ever
    /// consulted.
    /// <para>
    /// That is the exact shape <c>RefuseUnlessAlreadyPlacedAsync</c> exists for, and its own remarks name
    /// it: "a reserving UPDATE that matches no row … a correct description of the database and the wrong
    /// answer to the question asked, which was 'place my order'." The promo refusal was the one refusal
    /// not routed through it, on the argument that the transaction rolled back so "there is nothing to
    /// replay" — which is true of <i>this</i> request and says nothing about the key.
    /// </para>
    /// <para>
    /// <b>Eight submissions and no rounds, because the refusal is not a race to lose.</b> The app lock
    /// guarantees the losers are serialised behind the winner and therefore guarantees every one of them
    /// sees its order, so one attempt reproduces it every time.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task A_double_submission_with_a_per_customer_code_is_replayed_the_order_it_placed()
    {
        var variantIds = await CheckoutVariantIdsAsync(1);
        var colombo = await DistrictIdAsync("Colombo");
        var standard = await DeliveryMethodIdAsync(DeliveryMethod.StandardCode);

        var code = await NewPromoCodeAsync(
            discountType: PromoDiscountType.FixedAmount,
            discountValue: 100m,
            perCustomerLimit: 1,
            totalUsageLimit: null);

        await SetStockAsync(variantIds[0], 10);

        var (token, _, api) = await NewBagAsync(variantIds[0]);
        var (_, session) = await api.GetAsync(colombo, standard, Token, code.Code);
        var body = PromoBody(session, standard, colombo, UniqueEmail("doublesubmit"), code.Code);

        // A client each, so the submissions are genuinely simultaneous — a double-clicked button on a
        // flaky connection, which is what doc 11 P6's rule is written about.
        var clients = Enumerable.Range(0, DoubleSubmissions)
            .Select(_ => NewCheckoutVisitorWithoutCookieJar())
            .ToList();

        var results = await Task.WhenAll(
            clients.Select(client => client.PlaceWithCookieAsync(token.ToString("D"), body, Token)));

        // **Asserted first and by name, because the count cannot say what went wrong.** A submission
        // refused for a code its own key already spent is the defect.
        foreach (var refused in results.Where(result => !result.Response.IsSuccessStatusCode))
        {
            using var problem = await CheckoutApiClient.ProblemAsync(refused.Response, Token);

            Assert.Fail(
                $"One of {DoubleSubmissions} submissions of a single checkout key was refused with "
                + $"{(int)refused.Response.StatusCode} instead of being replayed the order its own key "
                + $"placed. Body: {problem.RootElement.GetRawText()}");
        }

        Assert.Equal(1, results.Count(result => result.Response.StatusCode == HttpStatusCode.Created));
        Assert.Equal(
            DoubleSubmissions - 1,
            results.Count(result => result.Response.StatusCode == HttpStatusCode.OK));

        // One order, and one use of the code — the replays must not take a second one.
        Assert.Single(results.Select(result => result.Order!.OrderNumber).Distinct(StringComparer.Ordinal));
        Assert.Equal(1, await UsageCountAsync(code.Id));
    }

    /// <summary>
    /// Doc 08 §3.6's definition of a held use, counted from <c>sales.Orders</c> rather than from the
    /// cache — so the assertion is about the thing <c>UsageCount</c> is a cache <i>of</i>.
    /// </summary>
    private async Task<int> HeldUsesAsync(int promoCodeId, string email)
    {
        var normalised = email.ToUpperInvariant();

        using var scope = CreateScope();

        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().Orders
            .AsNoTracking()
            .CountAsync(
                order => order.PromoCodeId == promoCodeId
                    && order.EmailNormalized == normalised
                    && (order.Status == OrderStatus.PendingPayment
                        || order.Status == OrderStatus.Paid
                        || order.Status == OrderStatus.Processing
                        || order.Status == OrderStatus.Shipped
                        || order.Status == OrderStatus.Delivered
                        || (order.Status == OrderStatus.Refunded && order.PaidAtUtc != null)),
                Token);
    }
}
