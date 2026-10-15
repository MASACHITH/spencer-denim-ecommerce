using SpencerDenim.Api.Http;

namespace SpencerDenim.UnitTests.Api;

/// <summary>
/// The webhook half of PayPal's endpoint throttle (#166).
/// <para>
/// The webhook's endpoint-wide bucket is also what PayPal's <i>real</i> deliveries draw on, and a pending
/// capture settles only by webhook, so the property worth pinning is who can spend it. These tests read the
/// numbers off the behaviour rather than restating the throttle's constants: what matters is that one
/// address's excess is refused <b>before</b> it reaches the shared bucket, so it cannot drain what other
/// addresses are drawing on.
/// </para>
/// </summary>
public sealed class PayPalEndpointThrottleTests
{
    /// <summary>More attempts than any bucket here holds, so "keep hammering" really does outlast it.</summary>
    private const int Hammering = 5_000;

    [Fact]
    public void A_notification_with_budget_left_gets_a_permit()
    {
        using var throttle = new PayPalEndpointThrottle();

        Assert.True(throttle.TryAcquireWebhook("203.0.113.7"));
    }

    [Fact]
    public void One_address_spends_only_its_own_budget_and_is_then_refused()
    {
        using var throttle = new PayPalEndpointThrottle();

        var spent = Spend(throttle, "198.51.100.1");

        Assert.InRange(spent, 1, Hammering - 1);
        Assert.False(throttle.TryAcquireWebhook("198.51.100.1"));
    }

    /// <summary>
    /// The point of #166. The endpoint-wide capacity is measured on a throttle of its own, as the number of
    /// permits a crowd of different addresses can take between them; then one address hammers a second
    /// throttle for far longer than it has budget, and a crowd of others takes what is left. If the refused
    /// attempts had been taking a shared permit each (the address checked <i>after</i> the shared bucket, or
    /// not at all) the crowd would find it empty.
    /// </summary>
    [Fact]
    public void An_address_that_is_refused_has_not_taken_anything_from_the_shared_bucket()
    {
        int endpointCapacity;

        using (var reference = new PayPalEndpointThrottle())
        {
            endpointCapacity = SpendAcrossManyAddresses(reference, firstOctet: 10);
        }

        using var throttle = new PayPalEndpointThrottle();

        var attackerSpent = Spend(throttle, "198.51.100.1");
        var crowdSpent = SpendAcrossManyAddresses(throttle, firstOctet: 172);

        Assert.True(attackerSpent < endpointCapacity, "the attacker must not be able to take the whole bucket by itself");
        Assert.Equal(endpointCapacity - attackerSpent, crowdSpent);
    }

    [Fact]
    public void One_address_running_out_does_not_refuse_another()
    {
        using var throttle = new PayPalEndpointThrottle();

        Spend(throttle, "198.51.100.2");

        Assert.False(throttle.TryAcquireWebhook("198.51.100.2"));
        Assert.True(throttle.TryAcquireWebhook("203.0.113.9"));
    }

    /// <summary>
    /// A request with no usable address is one address, not a free pass: behind a gateway whose forwarded
    /// headers are not trusted every visitor looks like this (W0-15), so it must be limited like any other.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_request_with_no_address_is_limited_like_any_other(string? address)
    {
        using var throttle = new PayPalEndpointThrottle();

        var spent = Spend(throttle, address);

        Assert.InRange(spent, 1, Hammering - 1);
        Assert.False(throttle.TryAcquireWebhook(address));
    }

    /// <summary>The customer routes keep their own buckets: a spent webhook budget refuses nobody's order page.</summary>
    [Fact]
    public void A_spent_webhook_budget_does_not_touch_the_customer_limits()
    {
        using var throttle = new PayPalEndpointThrottle();

        Spend(throttle, "198.51.100.3");

        Assert.True(throttle.TryAcquireCustomer(Guid.NewGuid(), "198.51.100.3"));
    }

    /// <summary>Permits one address can take before it is refused, capped so a broken limiter fails instead of hanging.</summary>
    private static int Spend(PayPalEndpointThrottle throttle, string? address)
    {
        var spent = 0;

        while (spent < Hammering && throttle.TryAcquireWebhook(address))
        {
            spent++;
        }

        // Keep hammering after the refusal: the refused attempts are what must be free.
        for (var i = 0; i < Hammering; i++)
        {
            Assert.False(throttle.TryAcquireWebhook(address));
        }

        return spent;
    }

    /// <summary>
    /// Permits a crowd of different addresses can take between them: each address takes a few, until a fresh
    /// one is refused on its very first attempt, which can only be the shared bucket.
    /// </summary>
    private static int SpendAcrossManyAddresses(PayPalEndpointThrottle throttle, int firstOctet)
    {
        var total = 0;

        for (var host = 1; host < 254 * 4; host++)
        {
            var address = $"{firstOctet}.0.{host / 254}.{host % 254 + 1}";

            if (!throttle.TryAcquireWebhook(address))
            {
                return total;
            }

            total++;

            // A second permit from the same address, so the crowd is not limited by how many addresses it has.
            if (throttle.TryAcquireWebhook(address))
            {
                total++;
            }
        }

        throw new InvalidOperationException("The shared bucket never ran out within a thousand addresses.");
    }
}
