using Microsoft.Extensions.DependencyInjection;
using SpencerDenim.Application.Sales.Orders.Abstractions;
using SpencerDenim.Domain.Sales;
using SpencerDenim.Domain.Settings;
using SpencerDenim.IntegrationTests.Infrastructure;

namespace SpencerDenim.IntegrationTests.Sales;

/// <summary>
/// <c>AutoDeliverShipped</c> (doc 11 P9 step 8, doc 08 §4.1's System path), against a real SQL Server.
/// <para>
/// <b>The test this file exists for is the pickup exclusion</b> (doc 08 §4.1 G-18): for a store pickup,
/// <c>Shipped</c> means <i>ready for collection</i>, so an order nobody has collected must never be
/// recorded as delivered — which would also hand out a "Verified buyer" badge for goods still on the
/// shelf. It is asserted against both statements, because the candidate query is read outside the
/// transaction and a guard that lived only there would be one a concurrent change could walk past.
/// </para>
/// </summary>
public sealed class AutoDeliverShippedTests(SqlServerDatabaseFixture sqlServer)
    : AdminOrderTestBase(sqlServer)
{
    [Fact]
    public async Task An_order_shipped_longer_ago_than_the_window_is_delivered_by_the_system()
    {
        var order = await PaidOrderAsync();
        await ShipAsync(order.OrderId);
        await BackdateShippedAsync(order.OrderId, TimeSpan.FromDays(8));

        var now = DateTime.UtcNow;
        var cutOff = now.AddDays(-SettingKeys.Orders.AutoDeliverAfterDaysDefault);

        Assert.Contains(order.OrderId, await FindDueAsync(cutOff));
        Assert.True(await AutoDeliverAsync(order.OrderId, cutOff, now));

        var row = await ReadOrderAsync(order.PublicId);
        Assert.Equal(OrderStatus.Delivered, row.Status);
        Assert.NotNull(row.DeliveredAtUtc);

        // Doc 08 §4: "System" carries no staff permission, so the history row has a source and no user.
        // That nullability is exactly what this row exists to use.
        var history = row.StatusHistory.OrderBy(entry => entry.Id).Last();
        Assert.Equal(OrderStatus.Shipped, history.FromStatus);
        Assert.Equal(OrderStatus.Delivered, history.ToStatus);
        Assert.Equal(OrderStatusChangeSource.SystemJob, history.Source);
        Assert.Null(history.ChangedByUserId);
        Assert.Contains("automatically", history.Note!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task An_order_shipped_inside_the_window_is_left_alone()
    {
        var order = await PaidOrderAsync();
        await ShipAsync(order.OrderId);
        await BackdateShippedAsync(order.OrderId, TimeSpan.FromDays(3));

        var now = DateTime.UtcNow;
        var cutOff = now.AddDays(-SettingKeys.Orders.AutoDeliverAfterDaysDefault);

        Assert.DoesNotContain(order.OrderId, await FindDueAsync(cutOff));

        // And the claim refuses it even if something hands it the id anyway, which is the point of the age
        // condition living in the statement as well as in the finder.
        Assert.False(await AutoDeliverAsync(order.OrderId, cutOff, now));

        var row = await ReadOrderAsync(order.PublicId);
        Assert.Equal(OrderStatus.Shipped, row.Status);
        Assert.Null(row.DeliveredAtUtc);
    }

    /// <summary>
    /// <b>Doc 08 §4.1 G-18, the test that matters: a store pickup order is never auto-delivered.</b>
    /// </summary>
    /// <remarks>
    /// <b>Both statements are checked, and that is deliberate rather than belt and braces.</b> The
    /// candidate list is read outside the transaction, so a guard that only existed in the finder would be
    /// one a concurrent change could walk past; and a guard that only existed in the claim would still let
    /// the job log "9 candidates" every night about orders it can never move. A mutation that removes
    /// either one turns this test red.
    /// </remarks>
    [Fact]
    public async Task A_store_pickup_order_is_never_auto_delivered()
    {
        var order = await PaidOrderAsync();
        await MakePickupAsync(order.OrderId);
        await ShipAsync(order.OrderId, pickup: true);
        await BackdateShippedAsync(order.OrderId, TimeSpan.FromDays(90));

        var now = DateTime.UtcNow;
        var cutOff = now.AddDays(-SettingKeys.Orders.AutoDeliverAfterDaysDefault);

        Assert.DoesNotContain(order.OrderId, await FindDueAsync(cutOff));
        Assert.False(await AutoDeliverAsync(order.OrderId, cutOff, now));

        var row = await ReadOrderAsync(order.PublicId);

        // Still waiting to be collected, ninety days on. Doc 11 P9: staff chase it from the daily
        // checklist, and if it is never collected they agree a refund — the system does not pretend it
        // arrived.
        Assert.Equal(OrderStatus.Shipped, row.Status);
        Assert.Null(row.DeliveredAtUtc);
        Assert.DoesNotContain(row.StatusHistory, entry => entry.ToStatus == OrderStatus.Delivered);
    }

    /// <summary>
    /// A staff member marking the same order delivered a second before the job does is an ordinary lost
    /// race, not a double write.
    /// </summary>
    [Fact]
    public async Task A_staff_transition_and_the_job_racing_produce_one_delivery()
    {
        var order = await PaidOrderAsync();
        await ShipAsync(order.OrderId);
        await BackdateShippedAsync(order.OrderId, TimeSpan.FromDays(8));

        var now = DateTime.UtcNow;
        var cutOff = now.AddDays(-SettingKeys.Orders.AutoDeliverAfterDaysDefault);

        var staff = MarkDeliveredAsync(order.OrderId);
        var job = AutoDeliverAsync(order.OrderId, cutOff, now);

        await Task.WhenAll(staff, job);

        // Exactly one of them happened. Which one is not asserted, because either outcome is correct —
        // the point is that the conditional UPDATE makes it one.
        var appliedByStaff = (await staff) is OrderTransitionResult.Applied;
        var appliedByJob = await job;
        Assert.True(appliedByStaff ^ appliedByJob);

        var row = await ReadOrderAsync(order.PublicId);
        Assert.Equal(OrderStatus.Delivered, row.Status);
        Assert.Single(row.StatusHistory, entry => entry.ToStatus == OrderStatus.Delivered);
    }

    /// <summary>
    /// An order that is not <c>Shipped</c> is not a candidate, whatever its dispatch time says.
    /// </summary>
    [Fact]
    public async Task Only_a_shipped_order_is_a_candidate()
    {
        var order = await PaidOrderAsync();
        await ShipAsync(order.OrderId);
        await BackdateShippedAsync(order.OrderId, TimeSpan.FromDays(8));
        Assert.IsType<OrderTransitionResult.Applied>(await MarkDeliveredAsync(order.OrderId));

        var cutOff = DateTime.UtcNow.AddDays(-SettingKeys.Orders.AutoDeliverAfterDaysDefault);

        Assert.DoesNotContain(order.OrderId, await FindDueAsync(cutOff));
        Assert.False(await AutoDeliverAsync(order.OrderId, cutOff, DateTime.UtcNow));
    }

    /// <summary>
    /// The window comes from <c>ops.Settings</c> rather than from a constant in the job.
    /// </summary>
    /// <remarks>
    /// <b>Two written values, and no assertion about the resting one.</b> An earlier version of this test
    /// opened by asserting that an unwritten read answers doc 08 §3.8's seven — an assertion about a row
    /// nothing in the test had written, in a table several other classes in this assembly deliberately
    /// mutate (7 files pair <c>SettingKeys.</c> with <c>ExecuteUpdateAsync</c>, two of them shared bases),
    /// and one of them was nulling <i>every</i> catalogued key in its <c>finally</c> — including this one,
    /// whose seeded value is not null. Classes here do <b>not</b> interleave (measured; see
    /// <c>AssemblyInfo.cs</c>, where a W3-04 claim to the contrary is corrected): the restore left a NULL
    /// that persisted, so this line failed in whichever runs were ordered after that class. Either way it
    /// was an assertion about state the test did not own, which is the same defect the review found in
    /// <c>SettingsSchemaAndSeedTests</c>, in a second place.
    /// <para>
    /// The seeded seven keeps two race-free homes and does not need a third here: the unit test
    /// <c>AdminOrderWorkflowRulesTests</c> pins the catalogue's <c>SeedValue</c>, and
    /// <c>SettingsSchemaAndSeedTests.The_seeder_writes_a_missing_keys_catalogued_seed_value</c> pins what
    /// the seeder does with it inside a rolled-back transaction.
    /// </para>
    /// <para>
    /// Two different values rather than one, because a reader that returned a hard-coded number would pass
    /// a single-value test whenever that number was the one written.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task The_window_is_read_from_the_settings_row()
    {
        await SetAutoDeliverDaysAsync("14");
        Assert.Equal(14, await ReadWindowAsync());

        await SetAutoDeliverDaysAsync("3");
        Assert.Equal(3, await ReadWindowAsync());
    }

    /// <summary>
    /// <b>An unusable setting answers null, so the job skips the pass rather than assuming a number.</b>
    /// </summary>
    /// <remarks>
    /// The key is seeded with 7, so reaching this state means the row was deleted or holds something that
    /// is not a number — and a job that silently fell back to a hard-coded 7 would be moving money's worth
    /// of orders to Delivered on a figure no screen could show. Zero is refused for its own reason: "mark
    /// it delivered the moment it ships" is exactly the false "Verified buyer" badge G-18 is about.
    /// </remarks>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("seven")]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("91")]
    [InlineData("7.5")]
    public async Task An_unusable_window_answers_null(string? value)
    {
        await SetAutoDeliverDaysAsync(value);

        Assert.Null(await ReadWindowAsync());
    }

    [Theory]
    [InlineData("1")]
    [InlineData("90")]
    public async Task The_bounds_themselves_are_accepted(string value)
    {
        await SetAutoDeliverDaysAsync(value);

        Assert.Equal(int.Parse(value, System.Globalization.CultureInfo.InvariantCulture), await ReadWindowAsync());
    }

    private Task<IReadOnlyList<int>> FindDueAsync(DateTime shippedBeforeUtc) =>
        InScopeAsync(services => services
            .GetRequiredService<IAutoDeliverStore>()
            .FindDueOrdersAsync(50, shippedBeforeUtc, Token));

    private Task<bool> AutoDeliverAsync(int orderId, DateTime shippedBeforeUtc, DateTime nowUtc) =>
        InScopeAsync(services => services
            .GetRequiredService<IAutoDeliverStore>()
            .AutoDeliverAsync(orderId, shippedBeforeUtc, nowUtc, Token));

    private Task<int?> ReadWindowAsync() =>
        InScopeAsync(services => services
            .GetRequiredService<IOrderSettingsReader>()
            .ReadAutoDeliverAfterDaysAsync(Token));
}
