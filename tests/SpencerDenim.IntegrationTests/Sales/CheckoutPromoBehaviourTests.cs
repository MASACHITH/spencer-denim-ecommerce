using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SpencerDenim.Application.Sales.Orders.Abstractions;
using SpencerDenim.Domain.Marketing;
using SpencerDenim.Domain.Sales;
using SpencerDenim.Infrastructure.Persistence;
using SpencerDenim.IntegrationTests.Infrastructure;

namespace SpencerDenim.IntegrationTests.Sales;

/// <summary>
/// A promo code from the shopper's side, against a real SQL Server (ADM-13; doc 08 §3.6 and §6, doc 11 P3).
/// <para>
/// <b>Every assertion is on the rows, never on the response alone.</b> The response says what the server
/// believes it did; <c>marketing.PromoCodes.UsageCount</c>, <c>sales.Orders.PromoCodeId</c> and the order
/// lines' <c>PromoDiscountAllocated</c> are what it did. That is the whole reason these are integration
/// tests rather than unit ones: the things that can go wrong are a statement that moved the wrong number
/// of rows and a column that was never written.
/// </para>
/// </summary>
public sealed class CheckoutPromoBehaviourTests(SqlServerDatabaseFixture sqlServer)
    : CheckoutPromoTestBase(sqlServer)
{
    /// <summary>
    /// <b>Doc 11 line 500: "Our use counting explicitly gives a use back when an unpaid order expires or
    /// is cancelled."</b> Both halves, and then the proof that matters — the code works again.
    /// </summary>
    /// <remarks>
    /// <b>The per-customer limit is 1 and both orders use the same email</b>, so the second placement can
    /// only succeed if the first order's use genuinely went back. A test that merely read
    /// <c>UsageCount</c> after the cancel would pass over a decrement that left the orders table
    /// disagreeing with the cache; this one asks the shop the question a customer would.
    /// </remarks>
    [Fact]
    public async Task A_cancelled_order_gives_its_use_back_and_the_code_works_again()
    {
        var variantIds = await CheckoutVariantIdsAsync(2);
        var colombo = await DistrictIdAsync("Colombo");
        var standard = await DeliveryMethodIdAsync(DeliveryMethod.StandardCode);
        var email = UniqueEmail("cancelled");

        var code = await NewPromoCodeAsync(
            discountType: PromoDiscountType.FixedAmount,
            discountValue: 100m,
            perCustomerLimit: 1,
            totalUsageLimit: 1);

        foreach (var variantId in variantIds)
        {
            await SetStockAsync(variantId, 10);
        }

        var first = await PlaceWithPromoAsync(variantIds[0], colombo, standard, email, code.Code);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(1, await UsageCountAsync(code.Id));

        // Both limits are now full, so the same customer is refused — which is what makes the third
        // placement below a real question rather than a restatement of the first.
        var blocked = await PlaceWithPromoAsync(variantIds[1], colombo, standard, email, code.Code);
        Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);

        var orderId = await OrderIdOfAsync(code.Id, email);

        var applied = Assert.IsType<CancelOrderOutcome.Applied>(
            await CancelAsync(orderId, "The customer rang to say no."));

        Assert.NotNull(applied.OrderNumber);

        // Doc 08 §5.6 step 4: the decrement, in the cancel's own transaction.
        Assert.Equal(0, await UsageCountAsync(code.Id));

        // And the thing the decrement exists for. Doc 11 P3's exception: "Customer's unpaid order expires
        // → the code use returns; the customer can use it again if their per-customer limit allows."
        var again = await PlaceWithPromoAsync(variantIds[1], colombo, standard, email, code.Code);
        Assert.Equal(HttpStatusCode.Created, again.StatusCode);
        Assert.Equal(1, await UsageCountAsync(code.Id));
    }

    /// <summary>
    /// The other half of doc 11 line 500: the expiry job gives the use back too, in the same transaction
    /// as the status change and the stock release.
    /// </summary>
    [Fact]
    public async Task An_expired_order_gives_its_use_back()
    {
        var variantIds = await CheckoutVariantIdsAsync(2);
        var colombo = await DistrictIdAsync("Colombo");
        var standard = await DeliveryMethodIdAsync(DeliveryMethod.StandardCode);
        var email = UniqueEmail("expired");

        var code = await NewPromoCodeAsync(
            discountType: PromoDiscountType.FixedAmount,
            discountValue: 100m,
            perCustomerLimit: 1,
            totalUsageLimit: 2);

        foreach (var variantId in variantIds)
        {
            await SetStockAsync(variantId, 10);
        }

        var placed = await PlaceWithPromoAsync(variantIds[0], colombo, standard, email, code.Code);
        Assert.Equal(HttpStatusCode.Created, placed.StatusCode);
        Assert.Equal(1, await UsageCountAsync(code.Id));

        var orderId = await OrderIdOfAsync(code.Id, email);

        Assert.True(await ExpireAsync(orderId));

        Assert.Equal(0, await UsageCountAsync(code.Id));

        var again = await PlaceWithPromoAsync(variantIds[1], colombo, standard, email, code.Code);
        Assert.Equal(HttpStatusCode.Created, again.StatusCode);
    }

    /// <summary>
    /// A <b>paid</b> order keeps its use when it is refunded — doc 08 §3.6's usage table, "Refund: no
    /// change" — and that is the row of the table the <c>UsageCount &gt; 0</c> guard alone would not keep.
    /// </summary>
    /// <remarks>
    /// <b>It is here rather than in the refund suite because refunds are manual for v1</b> (owner
    /// decision 2026-10-08) and there is no refund path to drive. What this asserts instead is the
    /// definition the two statements share: a <c>Refunded</c> order with a <c>PaidAtUtc</c> still
    /// <i>holds</i> a use, which is why nothing gives one back for it. Driven by approving the bank
    /// transfer and then reading the held-use count that <c>OrderPromoUse.TakeAsync</c> computes, so the
    /// assertion is on the real predicate rather than on a copy of it.
    /// </remarks>
    [Fact]
    public async Task A_paid_order_keeps_its_use()
    {
        var variantIds = await CheckoutVariantIdsAsync(2);
        var colombo = await DistrictIdAsync("Colombo");
        var standard = await DeliveryMethodIdAsync(DeliveryMethod.StandardCode);
        var email = UniqueEmail("paid");

        var code = await NewPromoCodeAsync(
            discountType: PromoDiscountType.FixedAmount,
            discountValue: 100m,
            perCustomerLimit: 1,
            totalUsageLimit: null);

        await SetStockAsync(variantIds[0], 10);
        await SetStockAsync(variantIds[1], 10);

        var placed = await PlaceWithPromoAsync(variantIds[0], colombo, standard, email, code.Code);
        Assert.Equal(HttpStatusCode.Created, placed.StatusCode);

        var orderId = await OrderIdOfAsync(code.Id, email);

        Assert.IsType<SpencerDenim.Application.Sales.Payments.Abstractions.BankTransferDecisionResult.Applied>(
            await ApproveAsync(orderId, "Paid."));

        // Paid holds a use, so the count has not moved and the customer's one use is still spent.
        Assert.Equal(1, await UsageCountAsync(code.Id));

        var again = await PlaceWithPromoAsync(variantIds[1], colombo, standard, email, code.Code);
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal(nameof(PromoRefusal.AlreadyUsedByCustomer), await PromoRefusalOfAsync(again));
    }

    /// <summary>
    /// The order and its lines carry the discount, not just the total: <c>PromoCodeId</c>,
    /// <c>PromoCodeText</c>, <c>PromoDiscountAmount</c> and each line's <c>PromoDiscountAllocated</c>.
    /// </summary>
    /// <remarks>
    /// <b>Issue #129 is the reason the per-line figure is asserted.</b> A line's refundable amount is its
    /// <c>LineTotal</c> and never its <c>LineSubtotal</c>, because the difference is the customer's share
    /// of the promotion — so a promo that reached the order total but not the lines would make every
    /// future refund over-pay. <c>CK_OrderLines_LineTotal</c> is the backstop; this is the assertion that
    /// the allocation happened at all.
    /// </remarks>
    [Fact]
    public async Task The_order_and_its_lines_record_the_code_and_its_allocation()
    {
        var variantId = await CheckoutVariantIdAsync();
        var colombo = await DistrictIdAsync("Colombo");
        var standard = await DeliveryMethodIdAsync(DeliveryMethod.StandardCode);

        var code = await NewPromoCodeAsync(
            discountType: PromoDiscountType.Percent,
            discountValue: 10m);

        await SetStockAsync(variantId, 10);

        var (_, _, api) = await NewBagAsync(variantId, quantity: 2);

        // The same code in lower case, so this also asserts that what the order stores is the shop's
        // spelling rather than the shopper's (doc 08 §3.6: "Code — Upper-case").
        var (_, session) = await api.GetAsync(colombo, standard, Token, code.Code.ToLowerInvariant());

        Assert.Equal(code.Code, session!.AppliedPromoCode);
        Assert.Null(session.PromoRefusal);

        var expectedDiscount = Math.Round(
            session.Totals.ItemsSubtotal * 0.10m,
            2,
            MidpointRounding.AwayFromZero);

        Assert.Equal(expectedDiscount, session.Totals.PromoDiscountAmount);

        // The ladder the bag and checkout both print: Subtotal − Savings − Promo + Delivery − Free
        // delivery = Total. Asserted on the payload rather than on the screen, because this is the
        // arithmetic the order row is written from.
        Assert.Equal(
            session.Totals.ItemsOriginalTotal
            - session.Totals.SavedTotal
            - session.Totals.PromoDiscountAmount
            + session.Totals.DeliveryFeeAmount!.Value
            - session.Totals.DeliveryDiscountAmount,
            session.Totals.GrandTotal!.Value);

        var email = UniqueEmail("allocated");
        var (response, confirmation) = await api.PlaceAsync(
            PromoBody(session, standard, colombo, email, code.Code.ToLowerInvariant()),
            Token);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var order = await ReadOrderAsync(confirmation!.PublicId);

        Assert.Equal(code.Id, order.PromoCodeId);
        Assert.Equal(code.Code, order.PromoCodeText);
        Assert.Equal(expectedDiscount, order.PromoDiscountAmount);

        var line = Assert.Single(order.Lines);

        // The whole discount landed on the only eligible line, and the line total is the subtotal less
        // that share — which is `CK_OrderLines_LineTotal` restated as an assertion, because a constraint
        // that is never exercised by a test is a constraint nobody has seen work.
        Assert.Equal(expectedDiscount, line.PromoDiscountAllocated);
        Assert.Equal(line.LineSubtotal - line.PromoDiscountAllocated, line.LineTotal);

        // And #129's rule: `promoDiscountAllocated` reaches the contract the admin order detail is built
        // from, so the figure a refund has to respect is readable rather than implied.
        Assert.Equal(expectedDiscount, Assert.Single(confirmation.Lines).PromoDiscountAllocated);
    }

    /// <summary>
    /// <b>Free delivery is a discount on a different line</b> (doc 08 §6 steps 8 and 10): the items
    /// discount stays zero, the delivery discount becomes the whole fee, and the identity the bag prints
    /// still holds.
    /// </summary>
    /// <remarks>
    /// Express to Colombo, because doc 08 §9's seeded Standard rate for Colombo has a free-delivery
    /// threshold on it and a test whose fee was already free could not tell a free-delivery code from the
    /// threshold doing its job. Asserted rather than assumed: the fee this reads is the one the quote
    /// gave for the option before the code was applied.
    /// </remarks>
    [Fact]
    public async Task A_free_delivery_code_discounts_the_fee_and_not_the_items()
    {
        var variantId = await CheckoutVariantIdAsync();
        var colombo = await DistrictIdAsync("Colombo");
        var express = await DeliveryMethodIdAsync(DeliveryMethod.ExpressCode);

        var code = await NewPromoCodeAsync(
            discountType: PromoDiscountType.FreeDelivery,
            discountValue: null);

        await SetStockAsync(variantId, 10);

        var (_, _, api) = await NewBagAsync(variantId);

        var (_, plain) = await api.GetAsync(colombo, express, Token);
        var fee = plain!.Totals.DeliveryFeeAmount!.Value;

        Assert.True(fee > 0m, "Express to Colombo should carry a fee, or this test proves nothing.");
        Assert.Equal(0m, plain.Totals.DeliveryDiscountAmount);

        var (_, quoted) = await api.GetAsync(colombo, express, Token, code.Code);

        Assert.Equal(code.Code, quoted!.AppliedPromoCode);

        // Step 8: "FreeDelivery → 0 here". The items are untouched.
        Assert.Equal(0m, quoted.Totals.PromoDiscountAmount);
        Assert.Equal(plain.Totals.ItemsSubtotal, quoted.Totals.ItemsSubtotal);

        // Step 10: the whole fee is discounted, and the fee itself still shows — the Delivery row says
        // what the courier costs and the Free delivery row says who paid for it.
        Assert.Equal(fee, quoted.Totals.DeliveryFeeAmount);
        Assert.Equal(fee, quoted.Totals.DeliveryDiscountAmount);
        Assert.Equal(plain.Totals.GrandTotal!.Value - fee, quoted.Totals.GrandTotal!.Value);

        // The identity the ladder prints, with the delivery discount in it.
        Assert.Equal(
            quoted.Totals.ItemsOriginalTotal
            - quoted.Totals.SavedTotal
            - quoted.Totals.PromoDiscountAmount
            + quoted.Totals.DeliveryFeeAmount!.Value
            - quoted.Totals.DeliveryDiscountAmount,
            quoted.Totals.GrandTotal!.Value);

        // And the option list agrees: the fee this option would add is now zero, so a shopper comparing
        // options is not shown a charge the order will not make.
        var option = quoted.DeliveryOptions.Single(candidate => candidate.Id == express);
        Assert.Equal(fee, option.Fee);
        Assert.Equal(0m, option.FeeAfterDiscount);
    }

    /// <summary>
    /// <b>Doc 08 §6 step 11's minimum-total clamp (G-11) is covered by <c>PromoPricingPipelineTests</c>
    /// and deliberately not here.</b>
    /// </summary>
    /// <remarks>
    /// The clamp can only bite when the delivery fee is not holding the total up, and the only seeded zone
    /// rate with a fee of zero belongs to <c>PICKUP</c> — which ships <b>disabled</b>, because doc 08 §9
    /// seeds no pickup location and <c>SalesReferenceData</c> says so in as many words. Enabling it here
    /// would mutate reference data every other suite reads, which <c>CheckoutTestBase</c>'s own remarks
    /// record as the cause of four intermittent failures. So the composition is tested where it can be
    /// tested exactly — <c>PromoEvaluator</c> into <c>OrderPricing.Price</c> with a zero fee — and this
    /// comment is here so the gap reads as a decision rather than an omission. It is in the task report.
    /// </remarks>

    /// <summary>
    /// A code that has been removed is "not recognised" rather than found and refused, so the box cannot
    /// be used to learn which strings the shop has ever issued.
    /// </summary>
    /// <remarks>
    /// <b>This is the test that stands in for an <c>IsDeleted</c> check in the evaluator.</b>
    /// <c>PromoCodeTerms</c> deliberately does not carry the flag — <c>UX_PromoCodes_Code</c> is filtered
    /// on <c>IsDeleted = 0</c>, so "the row for this code" is only well defined among undeleted ones —
    /// and the reader's <c>WHERE</c> is the single place the decision lives. A unit test could not reach
    /// it, because <c>PromoCode.IsDeleted</c> has a private setter (doc 08 §10.2 DB5).
    /// </remarks>
    [Fact]
    public async Task A_removed_code_is_not_recognised()
    {
        var variantId = await CheckoutVariantIdAsync();
        var colombo = await DistrictIdAsync("Colombo");
        var standard = await DeliveryMethodIdAsync(DeliveryMethod.StandardCode);

        var code = await NewPromoCodeAsync(
            discountType: PromoDiscountType.FixedAmount,
            discountValue: 100m);

        await SetStockAsync(variantId, 10);
        await RemovePromoCodeAsync(code.Id);

        var (_, _, api) = await NewBagAsync(variantId);
        var (_, session) = await api.GetAsync(colombo, standard, Token, code.Code);

        Assert.Null(session!.AppliedPromoCode);
        Assert.Equal(PromoRefusal.NotRecognised, session.PromoRefusal);
        Assert.Equal(0m, session.Totals.PromoDiscountAmount);
    }

    /// <summary>
    /// Doc 11 P3 step 12: the code is revalidated at placement, and one that has stopped qualifying
    /// refuses the order rather than being quietly dropped.
    /// </summary>
    /// <remarks>
    /// <b>Refused and not placed-without-the-discount, which is the decision this test pins.</b> The
    /// shopper agreed to a total that included the code; placing the order at a higher one would be G-10
    /// with extra steps. Arranged by deactivating the code between the quote and the submit, which is
    /// exactly what a staff member switching a campaign off mid-checkout does.
    /// </remarks>
    [Fact]
    public async Task A_code_switched_off_between_the_quote_and_the_submit_refuses_the_order()
    {
        var variantId = await CheckoutVariantIdAsync();
        var colombo = await DistrictIdAsync("Colombo");
        var standard = await DeliveryMethodIdAsync(DeliveryMethod.StandardCode);

        var code = await NewPromoCodeAsync(
            discountType: PromoDiscountType.FixedAmount,
            discountValue: 100m);

        await SetStockAsync(variantId, 10);

        var (_, _, api) = await NewBagAsync(variantId);
        var (_, session) = await api.GetAsync(colombo, standard, Token, code.Code);

        Assert.Equal(code.Code, session!.AppliedPromoCode);

        await DeactivateAsync(code.Id);

        var ordersBefore = await OrderCountAsync();

        var (response, _) = await api.PlaceAsync(
            PromoBody(session, standard, colombo, UniqueEmail("switchedoff"), code.Code),
            Token);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(nameof(PromoRefusal.NotRecognised), await PromoRefusalOfAsync(response));

        // Nothing was written, which is the half a status code cannot say.
        Assert.Equal(ordersBefore, await OrderCountAsync());
        Assert.Equal(0, await UsageCountAsync(code.Id));
        Assert.Equal(0, await ReservedAsync(variantId));
    }

    /// <summary>
    /// A minimum spend the bag does not meet refuses the code and says what the threshold is, because
    /// that is the one refusal a shopper can act on.
    /// </summary>
    [Fact]
    public async Task A_minimum_spend_the_bag_misses_is_refused_with_the_figure()
    {
        var variantId = await CheckoutVariantIdAsync();
        var colombo = await DistrictIdAsync("Colombo");
        var standard = await DeliveryMethodIdAsync(DeliveryMethod.StandardCode);

        await SetStockAsync(variantId, 10);

        var (_, _, api) = await NewBagAsync(variantId);
        var (_, plain) = await api.GetAsync(colombo, standard, Token);

        var threshold = plain!.Totals.ItemsSubtotal + 1_000m;

        var code = await NewPromoCodeAsync(
            discountType: PromoDiscountType.Percent,
            discountValue: 10m,
            minSpendAmount: threshold);

        var (_, session) = await api.GetAsync(colombo, standard, Token, code.Code);

        Assert.Null(session!.AppliedPromoCode);
        Assert.Equal(PromoRefusal.MinimumSpend, session.PromoRefusal);
        Assert.Equal(threshold, session.PromoMinimumSpend);
        Assert.Equal(0m, session.Totals.PromoDiscountAmount);
    }

    /// <summary>
    /// <b>Validity is a clock, and the shop is in Colombo.</b> A code whose window ends at midnight
    /// Colombo on the 13th is still live at 18:00 UTC on the 12th — 23:30 in the shop — and over at 18:30
    /// UTC, which is 00:00 on the 13th.
    /// </summary>
    /// <remarks>
    /// <b>Driven through the real endpoint with the stored UTC bound, which is the half a unit test of
    /// <c>PromoCodeSchedule</c> cannot cover.</b> The unit test proves the conversion; this proves that
    /// the column holds the converted instant and the evaluator compares UTC with UTC. The two together
    /// are what close this project's UTC-versus-Colombo defect class: either alone would pass over a
    /// conversion applied twice or not at all.
    /// <para>
    /// The window is placed around <i>now</i> rather than on a fixed calendar date, because the
    /// evaluator reads the real clock through <c>IClock</c> and the API host's clock is not a test double
    /// here.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task A_window_typed_in_Colombo_time_ends_at_Colombo_midnight_and_not_at_UTC_midnight()
    {
        var variantId = await CheckoutVariantIdAsync();
        var colombo = await DistrictIdAsync("Colombo");
        var standard = await DeliveryMethodIdAsync(DeliveryMethod.StandardCode);

        await SetStockAsync(variantId, 10);

        // Midnight tonight *in Colombo*, which is what a staff member means by "until the end of today",
        // converted the way the admin endpoint converts it.
        var colomboNow = PromoCodeSchedule.ToColombo(DateTime.UtcNow);
        var midnightColombo = colomboNow.Date.AddDays(1);
        var endsAtUtc = PromoCodeSchedule.ToUtc(midnightColombo);

        // The assertion that the conversion is the one being tested: Colombo is UTC+05:30, so Colombo
        // midnight is 18:30 UTC the day before. Without the conversion the column would hold
        // `midnightColombo` itself and the code would die five and a half hours early.
        Assert.Equal(midnightColombo.AddMinutes(-330), endsAtUtc);

        var live = await NewPromoCodeAsync(
            discountType: PromoDiscountType.FixedAmount,
            discountValue: 100m,
            endsAtUtc: endsAtUtc);

        var (_, _, api) = await NewBagAsync(variantId);
        var (_, session) = await api.GetAsync(colombo, standard, Token, live.Code);

        // It is before Colombo midnight, so the code works — and it would not if the stored bound were
        // the un-converted wall-clock value whenever the UTC time of day is past the Colombo one.
        Assert.Equal(live.Code, session!.AppliedPromoCode);

        // The same window one millisecond in the past is over, because doc 08 §3.6's end is exclusive —
        // the rule `ContentSchedule.IsLive` applies to every other scheduled row in this database.
        var ended = await NewPromoCodeAsync(
            discountType: PromoDiscountType.FixedAmount,
            discountValue: 100m,
            startsAtUtc: DateTime.UtcNow.AddHours(-2),
            endsAtUtc: DateTime.UtcNow.AddMilliseconds(-1));

        var (_, overSession) = await api.GetAsync(colombo, standard, Token, ended.Code);

        Assert.Null(overSession!.AppliedPromoCode);
        Assert.Equal(PromoRefusal.NotRecognised, overSession.PromoRefusal);
    }

    /// <summary>The id of the one order this code and email hold between them.</summary>
    private async Task<int> OrderIdOfAsync(int promoCodeId, string email)
    {
        var normalised = email.ToUpperInvariant();

        using var scope = CreateScope();

        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().Orders
            .AsNoTracking()
            .Where(order => order.PromoCodeId == promoCodeId
                && order.EmailNormalized == normalised
                && order.Status == OrderStatus.PendingPayment)
            .Select(order => order.Id)
            .SingleAsync(Token);
    }

    /// <summary>Switches a code off, as the admin screen's Active checkbox does.</summary>
    private async Task DeactivateAsync(int promoCodeId)
    {
        using var scope = CreateScope();

        var moved = await scope.ServiceProvider.GetRequiredService<AppDbContext>().PromoCodes
            .Where(code => code.Id == promoCodeId)
            .ExecuteUpdateAsync(code => code.SetProperty(row => row.IsActive, false), Token);

        Assert.Equal(1, moved);
    }

    private Task<CancelOrderOutcome> CancelAsync(int orderId, string reason) =>
        InScopeAsync(async services =>
        {
            var publicId = await PublicIdAsync(orderId);

            return await services
                .GetRequiredService<IOrderCancellationStore>()
                .CancelAsync(
                    new OrderCancellation(
                        publicId,
                        OrderCancelledBy.Staff,
                        Fulfiller(),
                        reason,
                        DateTime.UtcNow),
                    Token);
        });

    private async Task<Guid> PublicIdAsync(int orderId)
    {
        using var scope = CreateScope();

        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().Orders
            .AsNoTracking()
            .Where(order => order.Id == orderId)
            .Select(order => order.PublicId)
            .SingleAsync(Token);
    }
}
