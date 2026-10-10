using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SpencerDenim.Application.Abstractions.Auditing;
using SpencerDenim.Application.Sales.Orders.Abstractions;
using SpencerDenim.Application.Sales.Payments.PayHere;
using SpencerDenim.Domain.Identity;
using SpencerDenim.Domain.Sales;
using SpencerDenim.Infrastructure.Persistence;
using SpencerDenim.IntegrationTests.Infrastructure;

namespace SpencerDenim.IntegrationTests.Sales;

/// <summary>
/// What the rest of the shop does with a PayHere order (doc 11 P8b steps 6 and 8; P10): who may cancel it
/// while PayHere holds a payment, what staff can see of it to make a manual refund, and what a PayHere payment
/// that could not settle looks like to the code that will tell the shopper about it.
/// </summary>
public sealed class PayHereOrderHandlingTests(SqlServerDatabaseFixture sqlServer) : PayHereTestBase(sqlServer)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    // ---- cancel while PayHere holds a payment (P8 step 9's rule, applied to PayHere) ----------------

    /// <summary>
    /// A Pending PayHere payment blocks cancel exactly as a Pending PayPal one does: the customer and an
    /// <c>Orders.Edit</c> holder are both refused, with the neutral code, and nothing changes.
    /// </summary>
    [Fact]
    public async Task A_pending_PayHere_payment_blocks_cancel_for_the_customer_and_for_staff()
    {
        await ConfigurePayHereAsync();
        var placed = await PlacePayHereOrderAsync(quantity: 2, onHand: 6);

        using (var pending = await PostNotifyAsync(NotifyForm(placed.OrderReference, placed.GrandTotal, "0")))
        {
            Assert.Equal(HttpStatusCode.OK, pending.StatusCode);
        }

        Assert.Equal(PaymentStatus.Pending, Assert.Single(await PaymentsAsync(placed.PublicId)).Status);

        var stockBefore = await StockAsync(placed.VariantId);

        // The store, for the code and the words.
        foreach (var caller in new[] { OrderCancelledBy.Customer, OrderCancelledBy.Staff })
        {
            var refused = Assert.IsType<CancelOrderOutcome.Refused>(await CancelAsync(placed.PublicId, caller));

            Assert.Equal(CancelOrderRefusalCodes.GatewayPaymentPending, refused.Code);
            Assert.Equal("gateway_payment_pending", refused.Code);
            Assert.Equal(OrderStatus.PendingPayment, refused.Status);
            Assert.Contains("online payment", refused.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("being confirmed", refused.Message, StringComparison.OrdinalIgnoreCase);

            // The gateway is not named: a customer reads this, and so will a third gateway.
            Assert.DoesNotContain("PayHere", refused.Message, StringComparison.OrdinalIgnoreCase);
        }

        // The customer's route: a bare 404, as for every refusal there, and the page says it cannot be cancelled.
        using (var viaRoute = await NewCheckoutVisitorWithoutCookieJar().CancelOrderAsync(placed.PublicId.ToString("D"), Token))
        {
            Assert.Equal(HttpStatusCode.NotFound, viaRoute.StatusCode);
        }

        Assert.False((await OrderJsonAsync(placed.PublicId)).GetProperty("canCancel").GetBoolean());

        // Staff's route: 409 with the code, and the screen's flag agrees.
        using var editor = await MediaTestHost.SignInAsync(Factory, Permissions.Orders.Edit, Permissions.Orders.View);

        var detail = await AdminDetailAsync(editor, placed.PublicId);
        Assert.False(detail.GetProperty("actions").GetProperty("canCancel").GetBoolean());

        using var cancel = await editor.PostAsJsonAsync(
            new Uri($"/api/admin/orders/{placed.PublicId:D}/cancel", UriKind.Relative),
            new { cancelReason = "The customer telephoned to cancel." },
            JsonOptions,
            Token);

        Assert.Equal(HttpStatusCode.Conflict, cancel.StatusCode);

        var problem = await cancel.Content.ReadFromJsonAsync<JsonElement>(JsonOptions, Token);
        Assert.Equal("gateway_payment_pending", problem.GetProperty("code").GetString());

        // Nothing moved.
        var order = await ReadOrderAsync(placed.PublicId);

        Assert.Equal(OrderStatus.PendingPayment, order.Status);
        Assert.Equal(PaymentStatus.Pending, Assert.Single(order.Payments).Status);
        Assert.Equal(stockBefore, await StockAsync(placed.VariantId));
    }

    [Fact]
    public async Task A_PayHere_payment_that_is_only_initiated_can_be_cancelled_by_the_customer()
    {
        await ConfigurePayHereAsync();
        var placed = await PlacePayHereOrderAsync();

        Assert.True((await OrderJsonAsync(placed.PublicId)).GetProperty("canCancel").GetBoolean());

        var applied = await CancelAsync(placed.PublicId, OrderCancelledBy.Customer);

        Assert.IsType<CancelOrderOutcome.Applied>(applied);

        var order = await ReadOrderAsync(placed.PublicId);

        Assert.Equal(OrderStatus.Cancelled, order.Status);
        Assert.Equal(PaymentStatus.Cancelled, Assert.Single(order.Payments).Status);
    }

    /// <summary>
    /// Once PayHere has failed the pending payment the block lifts: the failed attempt is closed, a fresh one is
    /// open, and a fresh Initiated attempt is cancellable.
    /// </summary>
    [Fact]
    public async Task The_block_lifts_when_PayHere_fails_the_pending_payment()
    {
        await ConfigurePayHereAsync();
        var placed = await PlacePayHereOrderAsync();
        var payHereId = NewPayHerePaymentId();

        using (var pending = await PostNotifyAsync(
            NotifyForm(placed.OrderReference, placed.GrandTotal, "0", payHerePaymentId: payHereId)))
        {
            Assert.Equal(HttpStatusCode.OK, pending.StatusCode);
        }

        Assert.IsType<CancelOrderOutcome.Refused>(await CancelAsync(placed.PublicId, OrderCancelledBy.Customer));

        using (var failed = await PostNotifyAsync(
            NotifyForm(placed.OrderReference, placed.GrandTotal, "-2", payHerePaymentId: payHereId)))
        {
            Assert.Equal(HttpStatusCode.OK, failed.StatusCode);
        }

        Assert.True((await OrderJsonAsync(placed.PublicId)).GetProperty("canCancel").GetBoolean());
        Assert.IsType<CancelOrderOutcome.Applied>(await CancelAsync(placed.PublicId, OrderCancelledBy.Customer));
    }

    /// <summary>
    /// PayPal's own code is unchanged, so nothing that matches on it moves: the constant still has its value
    /// and the neutral one is distinct.
    /// </summary>
    [Fact]
    public void The_PayPal_refusal_code_keeps_its_value_and_the_neutral_one_is_distinct()
    {
        Assert.Equal("paypal_pending", CancelOrderRefusalCodes.PayPalPending);
        Assert.NotEqual(CancelOrderRefusalCodes.PayPalPending, CancelOrderRefusalCodes.GatewayPaymentPending);
    }

    // ---- what staff see, to refund by hand (P8b step 8) -------------------------------------------

    [Fact]
    public async Task The_admin_order_detail_shows_the_ids_staff_search_the_PayHere_portal_for()
    {
        await ConfigurePayHereAsync();
        var placed = await PlacePayHereOrderAsync();
        var payHereId = NewPayHerePaymentId();

        using (var response = await PostNotifyAsync(
            NotifyForm(placed.OrderReference, placed.GrandTotal, payHerePaymentId: payHereId)))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        using var viewer = await MediaTestHost.SignInAsync(Factory, Permissions.Orders.View);

        var payment = Assert.Single((await AdminDetailAsync(viewer, placed.PublicId)).GetProperty("payments").EnumerateArray());

        Assert.Equal("PayHere", payment.GetProperty("providerCode").GetString());
        Assert.Equal("Succeeded", payment.GetProperty("status").GetString());

        // PayHere's payment_id twice - the reference and the money - and the order_id the form carried.
        Assert.Equal(payHereId, payment.GetProperty("providerReference").GetString());
        Assert.Equal(payHereId, payment.GetProperty("providerTransactionId").GetString());
        Assert.Equal(placed.OrderReference, payment.GetProperty("providerOrderId").GetString());
        Assert.Equal(placed.OrderReference, $"{placed.OrderNumber}-{placed.PaymentId}");
    }

    [Fact]
    public async Task Every_attempt_shows_its_own_provider_order_id_and_a_failed_one_its_payment_id()
    {
        await ConfigurePayHereAsync();
        var placed = await PlacePayHereOrderAsync();
        var failedId = NewPayHerePaymentId();

        using (var response = await PostNotifyAsync(
            NotifyForm(placed.OrderReference, placed.GrandTotal, "-2", payHerePaymentId: failedId)))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        using var viewer = await MediaTestHost.SignInAsync(Factory, Permissions.Orders.View);

        var payments = (await AdminDetailAsync(viewer, placed.PublicId)).GetProperty("payments").EnumerateArray().ToList();

        // Newest first: the replacement attempt, then the one that failed.
        Assert.Equal(2, payments.Count);

        var replacement = payments[0];
        var failed = payments[1];
        var replacementId = (await PaymentsAsync(placed.PublicId)).Max(candidate => candidate.Id);

        Assert.Equal("Initiated", replacement.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, replacement.GetProperty("providerReference").ValueKind);
        Assert.Equal(JsonValueKind.Null, replacement.GetProperty("providerTransactionId").ValueKind);
        Assert.Equal($"{placed.OrderNumber}-{replacementId}", replacement.GetProperty("providerOrderId").GetString());

        Assert.Equal("Failed", failed.GetProperty("status").GetString());
        Assert.Equal(failedId, failed.GetProperty("providerReference").GetString());
        Assert.Equal(placed.OrderReference, failed.GetProperty("providerOrderId").GetString());
    }

    [Fact]
    public async Task A_bank_transfer_payment_shows_no_provider_ids()
    {
        var (order, _) = await PlaceOrderAsync();

        using var viewer = await MediaTestHost.SignInAsync(Factory, Permissions.Orders.View);

        var payment = Assert.Single((await AdminDetailAsync(viewer, order.PublicId)).GetProperty("payments").EnumerateArray());

        Assert.Equal("BankTransfer", payment.GetProperty("providerCode").GetString());
        Assert.Equal(JsonValueKind.Null, payment.GetProperty("providerReference").ValueKind);
        Assert.Equal(JsonValueKind.Null, payment.GetProperty("providerTransactionId").ValueKind);
        Assert.Equal(JsonValueKind.Null, payment.GetProperty("providerOrderId").ValueKind);
    }

    // ---- what a PayHere payment that could not settle looks like (the shopper notice reads this) ----

    /// <summary>
    /// The outcomes of the notify path that leave money on record without paying the order all end the same
    /// way: the payment is <c>CapturedRequiresRefund</c>, it is not the settling payment, it carries PayHere's
    /// id, and staff see it. A notice for the shopper that keys on that status needs nothing PayHere-specific.
    /// </summary>
    [Fact]
    public async Task Every_notify_outcome_that_leaves_money_unapplied_is_a_status_7_payment_staff_can_see()
    {
        await ConfigurePayHereAsync();

        var expired = await PlacePayHereOrderAsync();
        Assert.True(await ExpireAsync(expired.OrderId, DateTime.UtcNow.AddHours(3)));

        var cancelled = await PlacePayHereOrderAsync();
        Assert.IsType<CancelOrderOutcome.Applied>(await CancelAsync(cancelled.PublicId, OrderCancelledBy.Customer));

        var wrongAmount = await PlacePayHereOrderAsync();
        var wrongCurrency = await PlacePayHereOrderAsync();

        var cases = new (PayHereOrder Order, decimal Amount, string Currency, string Why)[]
        {
            (expired, expired.GrandTotal, "LKR", "after the order expired"),
            (cancelled, cancelled.GrandTotal, "LKR", "after the customer cancelled"),
            (wrongAmount, 1m, "LKR", "for the wrong amount"),
            (wrongCurrency, wrongCurrency.GrandTotal, "USD", "in the wrong currency"),
        };

        try
        {
            using var viewer = await MediaTestHost.SignInAsync(Factory, Permissions.Orders.View);

            foreach (var (order, amount, currency, why) in cases)
            {
                var payHereId = NewPayHerePaymentId();

                using var response = await PostNotifyAsync(
                    NotifyForm(order.OrderReference, amount, currency: currency, payHerePaymentId: payHereId));
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);

                var payment = Assert.Single(await PaymentsAsync(order.PublicId));

                Assert.True(payment.Status == PaymentStatus.CapturedRequiresRefund, $"Money {why}: {payment.Status}.");
                Assert.False(payment.IsOrderSettlement, why);
                Assert.Equal(payHereId, payment.ProviderTransactionId);
                Assert.Equal(PaymentProviderCodes.PayHere, payment.ProviderCode);

                // And staff can find it in the PayHere portal from the order screen.
                var shown = Assert.Single((await AdminDetailAsync(viewer, order.PublicId)).GetProperty("payments").EnumerateArray());

                Assert.Equal("CapturedRequiresRefund", shown.GetProperty("status").GetString());
                Assert.Equal(payHereId, shown.GetProperty("providerTransactionId").GetString());
                Assert.Equal(order.OrderReference, shown.GetProperty("providerOrderId").GetString());
            }
        }
        finally
        {
            foreach (var (order, _, _, _) in cases)
            {
                await DeleteAlertsAsync(order.PublicId);
            }
        }
    }

    // ---- a switched-off provider and a payment already in flight ---------------------------------

    /// <summary>
    /// Switching PayHere off hides it from new orders; it does not strand a payment already in flight. The
    /// notification is verified with the saved merchant ID and secret and does not read the provider row.
    /// </summary>
    [Fact]
    public async Task A_notification_for_a_payment_already_in_flight_is_still_applied_after_PayHere_is_switched_off()
    {
        await ConfigurePayHereAsync();
        var placed = await PlacePayHereOrderAsync();

        // The administrator turns PayHere off while the customer is at PayHere's page.
        await ConfigurePayHereAsync(enabled: false);

        Assert.DoesNotContain(PaymentProviderCodes.PayHere, await OfferedMethodCodesAsync());

        using var response = await PostNotifyAsync(NotifyForm(placed.OrderReference, placed.GrandTotal));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(OrderStatus.Paid, (await ReadOrderAsync(placed.PublicId)).Status);
    }

    // ---- helpers -----------------------------------------------------------------------------------

    private Task<CancelOrderOutcome> CancelAsync(Guid orderPublicId, OrderCancelledBy by) =>
        InScopeAsync(services => services
            .GetRequiredService<IOrderCancellationStore>()
            .CancelAsync(
                new OrderCancellation(
                    orderPublicId,
                    by,
                    by == OrderCancelledBy.Staff ? Staff() : null,
                    by == OrderCancelledBy.Staff ? "Asked." : null,
                    DateTime.UtcNow),
                Token));

    private static async Task<JsonElement> AdminDetailAsync(HttpClient client, Guid orderPublicId)
    {
        using var response = await client.GetAsync(
            new Uri($"/api/admin/orders/{orderPublicId:D}", UriKind.Relative),
            Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions, Token);
    }
}
