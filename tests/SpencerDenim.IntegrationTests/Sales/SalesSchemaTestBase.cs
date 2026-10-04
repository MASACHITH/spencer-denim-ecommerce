using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SpencerDenim.Domain.Sales;
using SpencerDenim.Infrastructure.Persistence;
using SpencerDenim.IntegrationTests.Infrastructure;

namespace SpencerDenim.IntegrationTests.Sales;

/// <summary>
/// Shared setup for the W3-03 schema tests: a scope on the migrated database, a
/// <see cref="SalesRowWriter"/>, and the handful of reference rows an order cannot exist without.
/// <para>
/// <b>Every row it creates is uniquely named.</b> The suite shares one database and runs sequentially, and
/// doc 08 §1.6 forbids deleting orders, so these tests leave their rows behind on purpose rather than
/// cleaning up with deletes the application is not allowed to make. Unique codes and order numbers are what
/// keep one test's leftovers from failing the next one's unique-index assertion.
/// </para>
/// </summary>
public abstract class SalesSchemaTestBase(SqlServerDatabaseFixture sqlServer) : IDisposable
{
    private IServiceScope? _scope;
    private AppDbContext? _dbContext;
    private SalesRowWriter? _writer;
    private SalesReferenceData? _reference;

    protected static CancellationToken Token => TestContext.Current.CancellationToken;

    protected SqlServerDatabaseFixture SqlServer { get; } = sqlServer;

    protected AppDbContext DbContext
    {
        get
        {
            SqlServer.SkipIfUnavailable();
            _scope ??= SqlServer.CreateScope();
            return _dbContext ??= _scope.ServiceProvider.GetRequiredService<AppDbContext>();
        }
    }

    protected SalesRowWriter Writer => _writer ??= new SalesRowWriter(DbContext);

    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// A payment provider, a normal delivery method, a PICKUP method and a pickup location — created once
    /// per test, with codes nothing else uses. Doc 08's order table has real foreign keys to all four, so
    /// there is no way to test an order's own rules without them.
    /// </summary>
    protected async Task<SalesReferenceData> ReferenceAsync()
    {
        if (_reference is not null)
        {
            return _reference;
        }

        var providerCode = SalesRowWriter.Unique("Prov");
        await Writer.InsertWithoutIdentityAsync(
            "sales.PaymentProviders",
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["Code"] = providerCode,
                ["DisplayName"] = "Test provider",
                ["IsEnabled"] = true,
                ["DisplayOrder"] = (short)1,
                ["CreatedAtUtc"] = DateTime.UtcNow,
            },
            Token);

        var standardMethodId = await Writer.InsertAsync(
            "sales.DeliveryMethods",
            SalesRowWriter.DeliveryMethodRow(),
            Token);

        // A second method whose code really is PICKUP, because CK_Orders_ShipAddress and CK_Orders_Pickup
        // branch on the literal and a method merely *named* pickup would prove nothing. Get-or-create: the
        // code is unique, so the first test class to run creates it and the rest reuse it — which also means
        // UX_DeliveryMethods_Code is doing its job every time this runs.
        var pickupMethodId = await DbContext.DeliveryMethods
            .Where(method => method.Code == DeliveryMethod.PickupCode)
            .Select(method => method.Id)
            .FirstOrDefaultAsync(Token);

        if (pickupMethodId == 0)
        {
            pickupMethodId = await Writer.InsertAsync(
                "sales.DeliveryMethods",
                SalesRowWriter.With(
                    SalesRowWriter.With(SalesRowWriter.DeliveryMethodRow(), "Code", DeliveryMethod.PickupCode),
                    "RequiresAddress",
                    false),
                Token);
        }

        var pickupLocationId = await Writer.InsertAsync(
            "sales.PickupLocations",
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["Name"] = SalesRowWriter.Unique("Shop "),
                ["AddressLine"] = "100 Main Street",
                ["City"] = "Colombo",
                ["DistrictId"] = 1,
                ["IsActive"] = true,
                ["SortOrder"] = (short)1,
                ["CreatedAtUtc"] = DateTime.UtcNow,
            },
            Token);

        var variant = await DbContext.ProductVariants
            .Select(candidate => new { candidate.Id, candidate.ProductId })
            .FirstAsync(Token);

        var departmentId = await DbContext.Products
            .Where(product => product.Id == variant.ProductId)
            .Select(product => product.DepartmentId)
            .FirstAsync(Token);

        var categoryId = await DbContext.Categories
            .Where(category => category.DepartmentId == departmentId)
            .Select(category => category.Id)
            .FirstAsync(Token);

        return _reference = new SalesReferenceData(
            providerCode,
            standardMethodId,
            pickupMethodId,
            pickupLocationId,
            variant.ProductId,
            variant.Id,
            departmentId,
            categoryId);
    }

    /// <summary>A valid order, so a test can then break exactly one thing about a child row.</summary>
    protected async Task<int> InsertValidOrderAsync(SalesReferenceData reference)
    {
        ArgumentNullException.ThrowIfNull(reference);

        return await Writer.InsertAsync(
            "sales.Orders",
            SalesRowWriter.OrderRow(
                reference.PaymentProviderCode,
                reference.StandardDeliveryMethodId,
                DeliveryMethod.StandardCode),
            Token);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (disposing)
        {
            _scope?.Dispose();
            _scope = null;
            _dbContext = null;
            _writer = null;
        }
    }
}

/// <summary>The reference rows a <c>sales.Orders</c> row needs before it can exist.</summary>
/// <param name="PaymentProviderCode">A provider row created for this test only.</param>
/// <param name="StandardDeliveryMethodId">A method whose code is not <c>PICKUP</c>.</param>
/// <param name="PickupDeliveryMethodId">A method whose code is exactly <c>PICKUP</c>.</param>
/// <param name="PickupLocationId">A shop to collect from.</param>
/// <param name="ProductId">A seeded product, for order lines.</param>
/// <param name="VariantId">A variant of <paramref name="ProductId"/>.</param>
/// <param name="DepartmentId">That product's department.</param>
/// <param name="CategoryId">A category in that department.</param>
public sealed record SalesReferenceData(
    string PaymentProviderCode,
    int StandardDeliveryMethodId,
    int PickupDeliveryMethodId,
    int PickupLocationId,
    int ProductId,
    int VariantId,
    int DepartmentId,
    int CategoryId);
