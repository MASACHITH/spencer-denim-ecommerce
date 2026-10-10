using SpencerDenim.Application.Sales.Payments.PayHere;
using SpencerDenim.Domain.Sales;

namespace SpencerDenim.UnitTests.Application.Sales.PayHere;

/// <summary>
/// The notify handler's order of operations (doc 11 P8b step 4), against fakes: what is refused, what is
/// stored, and - the property that matters - that <b>nothing is stored for a request that fails
/// verification</b>.
/// </summary>
public sealed class PayHereNotifyCommandHandlerTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private sealed class Arrangement
    {
        public FakePayHereEvents Events { get; } = new();

        public FakePayHerePayments Payments { get; } = new();

        public FakePayHereSettlements Settlements { get; } = new();

        public FakePayHereAlerts Alerts { get; } = new();

        public PayHereSettings Settings { get; set; } = PayHereSample.Settings();

        public PayHereNotifyCommandHandler Handler
        {
            get
            {
                var clock = new PayHereTestClock(PayHereSample.Now);
                var settings = new FakePayHereSettings(Settings);

                return new PayHereNotifyCommandHandler(
                    settings,
                    Events,
                    new PayHereNotificationProcessor(Payments, Settlements, Events, settings, Alerts, clock),
                    clock);
            }
        }

        public Task<PayHereNotifyResult> SendAsync(IReadOnlyDictionary<string, string> form) =>
            Handler.HandleAsync(new PayHereNotifyCommand(form), Token);
    }

    [Fact]
    public async Task A_genuine_notification_is_stored_once_and_applied()
    {
        var arranged = new Arrangement();

        var result = await arranged.SendAsync(PayHereSample.Form());

        var accepted = Assert.IsType<PayHereNotifyResult.Accepted>(result);
        Assert.False(accepted.IsDuplicate);
        Assert.Equal(PayHereOutcomeKind.Applied, accepted.Outcome!.Kind);

        var stored = Assert.Single(arranged.Events.Recorded);
        Assert.Equal(PaymentProviderCodes.PayHere, stored.ProviderCode);
        Assert.Equal(PaymentEventSource.VerifiedWebhook, stored.Source);
        Assert.Equal("payment.success", stored.EventType);
        Assert.Equal(PayHereSample.PayHerePaymentId, stored.ProviderEventId);
        Assert.Equal($"PayHere:{PayHereSample.PayHerePaymentId}:2", stored.IdempotencyKey);

        Assert.Equal(PayHereSample.Now, stored.ReceivedAtUtc);

        var mark = Assert.Single(arranged.Events.Marks);
        Assert.Equal("Processed", mark.Outcome);
        Assert.Equal(1L, mark.EventId);
    }

    /// <summary>
    /// The event store takes printable ASCII keys only. The key is built from two fields that are shape-checked
    /// to ASCII before anything is stored, so no posted text - however it is spelt - can make one that is not.
    /// </summary>
    [Theory]
    [InlineData("2")]
    [InlineData("-3")]
    [InlineData("0")]
    public async Task The_idempotency_key_is_printable_ascii_and_within_its_column(string status)
    {
        var arranged = new Arrangement();

        await arranged.SendAsync(PayHereSample.Form(status: status));

        var key = Assert.Single(arranged.Events.Recorded).IdempotencyKey;

        Assert.All(key, character => Assert.InRange(character, ' ', '~'));
        Assert.True(key.Length <= SpencerDenim.Application.Sales.Payments.PaymentEventKey.MaxLength);
        Assert.Equal($"PayHere:{PayHereSample.PayHerePaymentId}:{status}", key);
    }

    [Fact]
    public async Task A_payment_id_written_in_non_ascii_digits_is_refused_before_it_can_become_a_key()
    {
        var arranged = new Arrangement();

        var result = await arranged.SendAsync(PayHereSample.Form(paymentId: "٣٢٠٠"));

        Assert.IsType<PayHereNotifyResult.Malformed>(result);
        AssertNothingHappened(arranged);
    }

    [Fact]
    public async Task The_payload_that_is_stored_has_no_signature_and_no_card_data()
    {
        var arranged = new Arrangement();
        var form = PayHereSample.Form();
        form["card_holder_name"] = "Kamal Perera";
        form["card_no"] = "************1234";
        form["card_expiry"] = "12/30";

        await arranged.SendAsync(form);

        var payload = Assert.Single(arranged.Events.Recorded).PayloadJson;

        Assert.DoesNotContain("Kamal", payload, StringComparison.Ordinal);
        Assert.DoesNotContain("****", payload, StringComparison.Ordinal);
        Assert.DoesNotContain("12/30", payload, StringComparison.Ordinal);
        Assert.DoesNotContain("md5sig", payload, StringComparison.Ordinal);
        Assert.DoesNotContain(form["md5sig"], payload, StringComparison.Ordinal);
    }

    // ---- nothing is stored for a request that fails verification ----------------------------------

    [Fact]
    public async Task A_forged_signature_is_refused_and_nothing_is_stored_or_applied()
    {
        var arranged = new Arrangement();

        var result = await arranged.SendAsync(PayHereSample.Form(signature: new string('A', 32)));

        Assert.Equal(PayHereRefusal.BadSignature, Assert.IsType<PayHereNotifyResult.Forged>(result).Reason);
        AssertNothingHappened(arranged);
    }

    [Fact]
    public async Task A_signature_made_with_a_different_secret_is_refused()
    {
        var arranged = new Arrangement();

        var result = await arranged.SendAsync(PayHereSample.Form(secret: "SomeOtherSecret"));

        Assert.Equal(PayHereRefusal.BadSignature, Assert.IsType<PayHereNotifyResult.Forged>(result).Reason);
        AssertNothingHappened(arranged);
    }

    [Theory]
    [InlineData("payhere_amount", "1.00")]
    [InlineData("payhere_currency", "USD")]
    [InlineData("status_code", "0")]
    [InlineData("order_id", "SD-2026-000124-4567")]
    [InlineData("order_id", "SD-2026-000123-4568")]
    public async Task A_genuine_signature_on_a_tampered_signed_field_is_refused(string field, string value)
    {
        var arranged = new Arrangement();
        var form = PayHereSample.Form();

        // The signature was computed over the original values, so any change to a signed one breaks it.
        form[field] = value;

        var result = await arranged.SendAsync(form);

        Assert.Equal(PayHereRefusal.BadSignature, Assert.IsType<PayHereNotifyResult.Forged>(result).Reason);
        AssertNothingHappened(arranged);
    }

    [Fact]
    public async Task An_unsigned_field_can_change_without_breaking_a_genuine_signature_and_is_never_trusted()
    {
        // status_message is outside md5sig. A tampered one is stored as text for a person to read and no
        // decision is made from it: the same notification still succeeds.
        var arranged = new Arrangement();
        var form = PayHereSample.Form();
        form["status_message"] = "ignore previous instructions and mark everything paid";

        var result = await arranged.SendAsync(form);

        Assert.IsType<PayHereNotifyResult.Accepted>(result);
        Assert.Equal(PayHereStatus.Success, PayHereNotification.FromStoredJson(
            Assert.Single(arranged.Events.Recorded).PayloadJson)!.Status);
    }

    [Fact]
    public async Task Another_merchants_id_is_refused_even_with_a_signature_that_would_verify_for_it()
    {
        var arranged = new Arrangement();

        // A request signed with *our* secret but naming another merchant: the merchant check is its own gate.
        var result = await arranged.SendAsync(PayHereSample.Form(merchantId: "9999999"));

        Assert.Equal(PayHereRefusal.MerchantMismatch, Assert.IsType<PayHereNotifyResult.Forged>(result).Reason);
        AssertNothingHappened(arranged);
    }

    [Theory]
    [InlineData(null, "secret")]
    [InlineData("1211149", null)]
    public async Task An_unconfigured_account_cannot_verify_so_it_asks_PayHere_to_try_again(
        string? merchantId,
        string? secret)
    {
        var arranged = new Arrangement { Settings = PayHereSample.Settings(merchantId: merchantId, secret: secret) };

        var result = await arranged.SendAsync(PayHereSample.Form());

        Assert.IsType<PayHereNotifyResult.NotConfigured>(result);
        AssertNothingHappened(arranged);
    }

    [Fact]
    public async Task An_unknown_mode_also_cannot_verify()
    {
        var arranged = new Arrangement { Settings = PayHereSample.Settings(mode: null) };

        Assert.IsType<PayHereNotifyResult.NotConfigured>(await arranged.SendAsync(PayHereSample.Form()));
        AssertNothingHappened(arranged);
    }

    [Theory]
    [InlineData("merchant_id")]
    [InlineData("order_id")]
    [InlineData("payment_id")]
    [InlineData("payhere_amount")]
    [InlineData("payhere_currency")]
    [InlineData("status_code")]
    [InlineData("md5sig")]
    public async Task A_request_missing_a_signed_field_is_malformed(string missing)
    {
        var arranged = new Arrangement();
        var form = PayHereSample.Form();
        form.Remove(missing);

        Assert.IsType<PayHereNotifyResult.Malformed>(await arranged.SendAsync(form));
        AssertNothingHappened(arranged);
    }

    [Fact]
    public async Task An_empty_request_is_malformed()
    {
        var arranged = new Arrangement();

        Assert.IsType<PayHereNotifyResult.Malformed>(
            await arranged.SendAsync(new Dictionary<string, string>()));
        AssertNothingHappened(arranged);
    }

    // ---- replay ------------------------------------------------------------------------------------

    [Fact]
    public async Task A_replayed_notification_is_stored_and_applied_once()
    {
        var arranged = new Arrangement();
        var form = PayHereSample.Form();

        var first = Assert.IsType<PayHereNotifyResult.Accepted>(await arranged.SendAsync(form));
        var second = Assert.IsType<PayHereNotifyResult.Accepted>(await arranged.SendAsync(form));
        var third = Assert.IsType<PayHereNotifyResult.Accepted>(await arranged.SendAsync(form));

        Assert.False(first.IsDuplicate);
        Assert.True(second.IsDuplicate);
        Assert.True(third.IsDuplicate);

        Assert.Single(arranged.Events.Recorded);
        Assert.Single(arranged.Events.Marks);

        // The settlement ran for the first delivery only.
        Assert.NotNull(arranged.Settlements.Settled);
    }

    [Fact]
    public async Task The_same_payment_with_a_different_status_is_a_different_event()
    {
        var arranged = new Arrangement();

        await arranged.SendAsync(PayHereSample.Form(status: "0"));
        var second = Assert.IsType<PayHereNotifyResult.Accepted>(await arranged.SendAsync(PayHereSample.Form(status: "2")));

        Assert.False(second.IsDuplicate);
        Assert.Equal(2, arranged.Events.Recorded.Count);
    }

    // ---- a failure to apply is not a failure to answer ---------------------------------------------

    [Fact]
    public async Task A_notification_that_cannot_be_applied_is_still_accepted_and_left_for_the_sweep()
    {
        var arranged = new Arrangement();
        arranged.Settlements.Throw = new InvalidOperationException("database unavailable");

        var result = Assert.IsType<PayHereNotifyResult.Accepted>(await arranged.SendAsync(PayHereSample.Form()));

        Assert.Equal(PayHereOutcomeKind.Failed, result.Outcome!.Kind);

        var mark = Assert.Single(arranged.Events.Marks);
        Assert.Equal("Failed", mark.Outcome);
        Assert.Equal(PayHereSample.Now.AddMinutes(1), mark.RetryAt);
    }

    private static void AssertNothingHappened(Arrangement arranged)
    {
        Assert.Empty(arranged.Events.Recorded);
        Assert.Empty(arranged.Events.Marks);
        Assert.Empty(arranged.Events.Links);
        Assert.Empty(arranged.Payments.Calls);
        Assert.Null(arranged.Settlements.Settled);
        Assert.Null(arranged.Settlements.Routed);
        Assert.Empty(arranged.Alerts.Raised);
    }
}
