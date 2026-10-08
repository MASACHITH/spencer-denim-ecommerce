using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SpencerDenim.Application.Abstractions.Auditing;
using SpencerDenim.Application.Sales.Orders.Abstractions;
using SpencerDenim.Domain.Sales;
using SpencerDenim.Domain.Settings;
using SpencerDenim.Infrastructure.Persistence;
using SpencerDenim.IntegrationTests.Infrastructure;

namespace SpencerDenim.IntegrationTests.Sales;

/// <summary>
/// Places real, paid orders and then exercises doc 11 P9's fulfilment on them (ADM-09).
/// <para>
/// <b>Orders are placed over HTTP and paid through the real approval transaction, never inserted by
/// hand.</b> <c>SalesRowWriter</c> exists for testing a constraint; every invariant this suite is about
/// depends on the order, its reservation and its invoice number having been written by the real paths. An
/// order whose <c>Status</c> was set by a fixture would let a transition "move" an order that had never
/// been paid, and the test would pass while the thing it is about did not happen.
/// </para>
/// </summary>
public abstract class AdminOrderTestBase(SqlServerDatabaseFixture sqlServer)
    : BankTransferTestBase(sqlServer)
{
    /// <summary>Every courier id this test created, so <see cref="DisposeAsync"/> can remove them.</summary>
    private readonly List<int> _couriers = [];

    /// <summary>The pickup location this test created, if any.</summary>
    private int? _pickupLocationId;

    /// <summary>
    /// An actor for a fulfilment call, with an id so the history and audit rows have one.
    /// </summary>
    /// <remarks>
    /// A <b>different</b> user id from <c>BankTransferTestBase.Staff</c>'s, on purpose: a test that pays an
    /// order and then ships it has two actors in its history, and asserting "the shipper is 7001 and the
    /// approver is 4242" is how a transition writing the wrong one would be caught. Named differently so
    /// nothing hides the inherited member.
    /// </remarks>
    protected static AdminActor Fulfiller(int userId = 7001) =>
        new(userId, $"fulfilment-{userId}@example.com", "203.0.113.9", "xunit", null);

    /// <summary>
    /// Places an order and pays it through the real bank-transfer approval, so it starts at <c>Paid</c>
    /// with its stock committed and an invoice number issued.
    /// </summary>
    protected async Task<(Guid PublicId, string OrderNumber, int OrderId, int VariantId)> PaidOrderAsync(
        int quantity = 1,
        int onHand = 10)
    {
        var (order, variantId) = await PlaceOrderAsync(quantity, onHand);
        var orderId = await OrderIdAsync(order.PublicId);

        var approved = await ApproveAsync(orderId, "Paid, for the fulfilment fixture.");
        Assert.IsType<SpencerDenim.Application.Sales.Payments.Abstractions.BankTransferDecisionResult.Applied>(
            approved);

        return (order.PublicId, order.OrderNumber, orderId, variantId);
    }

    /// <summary>
    /// Adds a courier of this test's own and returns its id.
    /// </summary>
    /// <remarks>
    /// <b>Named with the test's own marker, because <c>sales.Couriers</c> is seeded empty on purpose</b>
    /// (which carriers the client uses is their information) and the assembly shares one database. A
    /// courier left behind would be a neighbour's fixture — the shape that made four
    /// <c>CartStaleItemTests</c> red in a full run and green alone — and, worse, would make
    /// <c>AdminOrderActions.CouriersConfigured</c> true for a test that is about it being false.
    /// </remarks>
    protected async Task<int> NewCourierAsync(
        string? name = null,
        string? trackingUrlTemplate = "https://track.example.test/{trackingNumber}",
        bool isActive = true)
    {
        using var scope = CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var courier = new Courier
        {
            Name = name ?? $"xunit-courier-{Guid.NewGuid():N}"[..40],
            TrackingUrlTemplate = trackingUrlTemplate,
            IsActive = isActive,
            SortOrder = 0,
            CreatedAtUtc = DateTime.UtcNow,
        };

        db.Couriers.Add(courier);
        await db.SaveChangesAsync(Token);

        _couriers.Add(courier.Id);

        return courier.Id;
    }

    /// <summary>
    /// Turns one order into a store-pickup order, by rewriting <b>its own</b> snapshot columns.
    /// </summary>
    /// <remarks>
    /// <b>Why not place a pickup order through checkout.</b> Doc 08 §9 seeds <c>PICKUP</c> <i>disabled</i>
    /// and seeds <b>zero</b> pickup locations, deliberately — the client's shop address is their
    /// information and inventing one would put a street a shopper could drive to on a live checkout page.
    /// Enabling the method would therefore change shared reference data that a neighbouring test
    /// (<c>CheckoutRequestValidationTests.A_delivery_method_that_is_not_enabled_is_refused</c>) asserts the
    /// current state of.
    /// <para>
    /// So this rewrites the <i>order's</i> two snapshot columns instead, which is a row no other test
    /// reads. It is a fixture and not a path the application has: nothing in the product writes these
    /// columns after placement (doc 11 P9 makes the delivery method immutable), and the point of the test
    /// is what the <i>transitions</i> do when they find <c>DeliveryMethodCode = 'PICKUP'</c> — which is a
    /// real state the moment the client's address is in the database.
    /// </para>
    /// <para>
    /// A pickup location row is created because <c>CK_Orders_Pickup</c> requires one, and it is removed in
    /// <see cref="DisposeAsync"/>.
    /// </para>
    /// </remarks>
    protected async Task MakePickupAsync(int orderId)
    {
        using var scope = CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        if (_pickupLocationId is null)
        {
            var location = new PickupLocation
            {
                Name = $"xunit-pickup-{Guid.NewGuid():N}"[..40],
                AddressLine = "A test address",
                City = "A test city",
                DistrictId = await DistrictIdAsync("Colombo"),
                IsActive = true,
                SortOrder = 0,
                CreatedAtUtc = DateTime.UtcNow,
            };

            db.PickupLocations.Add(location);
            await db.SaveChangesAsync(Token);

            _pickupLocationId = location.Id;
        }

        var updated = await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            UPDATE sales.Orders
               SET DeliveryMethodCode = {DeliveryMethod.PickupCode},
                   PickupLocationId = {_pickupLocationId},
                   PickupLocationName = {"A test pickup point"}
             WHERE Id = {orderId};
            """,
            Token);

        Assert.Equal(1, updated);
    }

    /// <summary>Sets <c>Orders.AutoDeliverAfterDays</c>, for the job's own tests.</summary>
    /// <remarks>
    /// Restored by <see cref="DisposeAsync"/> to doc 08 §3.8's seeded 7, not to whatever it was: the
    /// seeder writes 7 and nothing else sets it, so 7 is the correct resting value and reading the old one
    /// first would only let a failure in one test leak into the next.
    /// </remarks>
    protected async Task SetAutoDeliverDaysAsync(string? value)
    {
        using var scope = CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        await db.Settings
            .Where(setting => setting.Key == SettingKeys.Orders.AutoDeliverAfterDays)
            .ExecuteUpdateAsync(setting => setting.SetProperty(row => row.Value, value), Token);
    }

    /// <summary>Sets the four <c>Store.*</c> legal keys, for the invoice's own tests.</summary>
    protected async Task SetStoreLegalDetailsAsync(
        string? legalName,
        string? address,
        string? brNumber = null,
        string? vatNumber = null)
    {
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            [SettingKeys.Store.LegalName] = legalName,
            [SettingKeys.Store.Address] = address,
            [SettingKeys.Store.BRNumber] = brNumber,
            [SettingKeys.Store.VatNumber] = vatNumber,
        };

        using var scope = CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        foreach (var (key, value) in values)
        {
            await db.Settings
                .Where(setting => setting.Key == key)
                .ExecuteUpdateAsync(setting => setting.SetProperty(row => row.Value, value), Token);
        }
    }

    protected Task<OrderTransitionResult> StartProcessingAsync(int orderId, string? note = null) =>
        InScopeAsync(services => services
            .GetRequiredService<IOrderFulfilmentStore>()
            .StartProcessingAsync(Transition(orderId, note), Token));

    protected Task<OrderTransitionResult> MarkShippedAsync(
        int orderId,
        int? courierId,
        string? trackingNumber,
        string? note = null) =>
        InScopeAsync(services => services
            .GetRequiredService<IOrderFulfilmentStore>()
            .MarkShippedAsync(
                Transition(orderId, note),
                courierId is null || trackingNumber is null
                    ? null
                    : new OrderDispatchDetails(courierId.Value, trackingNumber),
                Token));

    protected Task<OrderTransitionResult> MarkDeliveredAsync(int orderId, string? note = null) =>
        InScopeAsync(services => services
            .GetRequiredService<IOrderFulfilmentStore>()
            .MarkDeliveredAsync(Transition(orderId, note), Token));

    protected Task<OrderTransitionResult> UpdateDispatchAsync(
        int orderId,
        int courierId,
        string trackingNumber,
        string? note = null) =>
        InScopeAsync(services => services
            .GetRequiredService<IOrderFulfilmentStore>()
            .UpdateDispatchAsync(
                Transition(orderId, note),
                new OrderDispatchDetails(courierId, trackingNumber),
                Token));

    protected Task<OrderTransitionResult> SetInternalNoteAsync(int orderId, string? note) =>
        InScopeAsync(services => services
            .GetRequiredService<IOrderFulfilmentStore>()
            .SetInternalNoteAsync(Transition(orderId, null), note, Token));

    /// <summary>Marks an order <c>Shipped</c> through the real transitions, so it can be delivered.</summary>
    protected async Task<int> ShipAsync(int orderId, int? courierId = null, bool pickup = false)
    {
        Assert.IsType<OrderTransitionResult.Applied>(await StartProcessingAsync(orderId));

        if (pickup)
        {
            Assert.IsType<OrderTransitionResult.Applied>(
                await MarkShippedAsync(orderId, null, null));
            return 0;
        }

        var courier = courierId ?? await NewCourierAsync();

        Assert.IsType<OrderTransitionResult.Applied>(
            await MarkShippedAsync(orderId, courier, "TRACK-0001"));

        return courier;
    }

    /// <summary>Moves an order's <c>ShippedAtUtc</c> back, so the auto-deliver job finds it due.</summary>
    /// <remarks>
    /// <b>The dispatch time is moved rather than the cut-off, which is the opposite of
    /// <c>ExpireAsync</c>'s choice and right for the same reason.</b> The job's <i>statement</i> takes the
    /// cut-off as a parameter, so a test that moved the cut-off would be checking arithmetic it had
    /// supplied; moving the stored time means the guard is evaluated against a real column.
    /// </remarks>
    protected async Task BackdateShippedAsync(int orderId, TimeSpan by)
    {
        using var scope = CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // Raw SQL with DATEADD rather than ExecuteUpdate: EF cannot translate subtracting a TimeSpan
        // from a datetime2 column (it throws "Object must implement IConvertible"), and computing the
        // target in C# would mean this fixture wrote an absolute time rather than moving the stored one.
        var updated = await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            UPDATE sales.Orders
               SET ShippedAtUtc = DATEADD(second, {-(int)by.TotalSeconds}, ShippedAtUtc)
             WHERE Id = {orderId} AND ShippedAtUtc IS NOT NULL;
            """,
            Token);

        Assert.Equal(1, updated);
    }

    protected async Task<AdminOrderTarget?> TargetAsync(Guid publicId) =>
        await InScopeAsync(services => services
            .GetRequiredService<IAdminOrderReader>()
            .FindTargetAsync(publicId, Token));

    /// <summary>
    /// Puts back everything this test created: its couriers, its pickup location, the auto-deliver window
    /// and the four <c>Store.*</c> legal keys.
    /// </summary>
    /// <remarks>
    /// <b>Only rows this test created are removed.</b> An order is never deleted (doc 08 §1.6), so the
    /// orders stay; the couriers and the pickup location are this test's own and would otherwise be a
    /// neighbour's fixture. The settings go back to the seeder's values — null for the legal keys, 7 for
    /// the window — rather than to whatever was read first, so a failure in one test cannot leak into the
    /// next.
    /// </remarks>
    public override async ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);

        if (SqlServer.IsAvailable)
        {
            using var scope = SqlServer.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            // The orders that point at them go first, or the FK refuses the delete. Nulling CourierId on
            // this test's own orders is safe: they are terminal, nothing reads them again, and the
            // CourierName snapshot — which is what a dispatch note actually shows — stays.
            if (_couriers.Count > 0)
            {
                await db.Orders
                    .Where(order => order.CourierId != null && _couriers.Contains(order.CourierId.Value))
                    .ExecuteUpdateAsync(
                        order => order.SetProperty(row => row.CourierId, (int?)null),
                        CancellationToken.None);

                await db.Couriers
                    .Where(courier => _couriers.Contains(courier.Id))
                    .ExecuteDeleteAsync(CancellationToken.None);
            }

            if (_pickupLocationId is { } locationId)
            {
                // **The method code and the location have to move in the same statement**, because
                // `CK_Orders_Pickup` is `DeliveryMethodCode <> 'PICKUP' OR PickupLocationId IS NOT
                // NULL`: nulling the location on an order still marked PICKUP violates it, which is
                // exactly what the first version of this teardown did. Putting the code back to
                // STANDARD undoes `MakePickupAsync` rather than half-undoing it.
                await db.Database.ExecuteSqlInterpolatedAsync(
                    $"""
                    UPDATE sales.Orders
                       SET DeliveryMethodCode = {DeliveryMethod.StandardCode},
                           PickupLocationId = NULL,
                           PickupLocationName = NULL
                     WHERE PickupLocationId = {locationId};
                    """,
                    CancellationToken.None);

                await db.PickupLocations
                    .Where(location => location.Id == locationId)
                    .ExecuteDeleteAsync(CancellationToken.None);
            }

            await db.Settings
                .Where(setting => setting.Key == SettingKeys.Orders.AutoDeliverAfterDays)
                .ExecuteUpdateAsync(
                    setting => setting.SetProperty(
                        row => row.Value,
                        SettingKeys.Orders.AutoDeliverAfterDaysDefault.ToString(
                            System.Globalization.CultureInfo.InvariantCulture)),
                    CancellationToken.None);

            await db.Settings
                .Where(setting =>
                    setting.Key == SettingKeys.Store.LegalName
                    || setting.Key == SettingKeys.Store.Address
                    || setting.Key == SettingKeys.Store.BRNumber
                    || setting.Key == SettingKeys.Store.VatNumber)
                .ExecuteUpdateAsync(
                    setting => setting.SetProperty(row => row.Value, (string?)null),
                    CancellationToken.None);
        }

        await base.DisposeAsync();
    }

    private static OrderTransition Transition(int orderId, string? note) =>
        new(orderId, Fulfiller(), note, DateTime.UtcNow);
}
