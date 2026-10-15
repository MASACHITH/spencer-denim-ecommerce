using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SpencerDenim.Domain.Identity;
using SpencerDenim.Domain.Sales;
using SpencerDenim.Infrastructure.Identity;
using SpencerDenim.Infrastructure.Persistence;
using SpencerDenim.IntegrationTests.Infrastructure;

namespace SpencerDenim.IntegrationTests.Api.Admin;

/// <summary>
/// What the customer and newsletter admin tests (ADM-15, ADM-21) need: customer accounts, their
/// addresses, real orders placed through checkout, and a staff caller on the <i>same</i> host.
/// <para>
/// <b>Orders are placed over HTTP through the shopper's real checkout, never inserted.</b> Linking one to
/// an account is a single <c>UPDATE … SET CustomerUserId</c> — the exact statement doc 08 §3.1 D5 runs
/// after a customer confirms their address — because what is under test is which orders the admin screen
/// <i>reads</i> as the customer's, not how they became so.
/// </para>
/// <para>
/// <b>Every customer this base creates is named with a per-test marker</b>, because the assembly shares
/// one database: another suite's customers are on the same list, so every assertion about rows searches by
/// a marker that only this test wrote.
/// </para>
/// </summary>
public abstract class AdminCustomerTestBase(SqlServerDatabaseFixture sqlServer)
{
    protected static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// How far down the descending variant list to take stock from. Distinct from the order suite's 24 and
    /// the bank-transfer suite's 12, so the three cannot drain one variant between them.
    /// </summary>
    private const int VariantBandOffset = 36;

    protected SqlServerDatabaseFixture SqlServer { get; } = sqlServer;

    protected static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>A marker unique to this test, for names and addresses.</summary>
    protected string Marker { get; } = "w307" + Guid.NewGuid().ToString("N")[..12];

    protected async Task<MediaTestHost> StartAsync()
    {
        SqlServer.SkipIfUnavailable();
        await TestDatabaseMigrator.MigrateAsync(SqlServer.ConnectionString, Token);

        return MediaTestHost.Create(SqlServer.ConnectionString);
    }

    /// <summary>Creates a customer account directly, as registration would leave it, and returns its id.</summary>
    protected async Task<int> CreateCustomerAsync(
        MediaTestHost host,
        string email,
        string firstName,
        string? lastName = null,
        string? phone = null,
        string? districtName = null,
        bool emailConfirmed = true,
        DateTime? createdAtUtc = null)
    {
        await using var scope = host.Factory.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var user = new AppUser
        {
            UserName = email,
            Email = email,
            EmailConfirmed = emailConfirmed,
            UserType = UserType.Customer,
            FirstName = firstName,
            LastName = lastName,
            PhoneNumber = phone,
            DefaultDistrictId = districtName is null ? null : await DistrictIdAsync(districtName),
            IsActive = true,
            CreatedAtUtc = DateTime.UtcNow,
        };

        var created = await users.CreateAsync(user);
        Assert.True(created.Succeeded, string.Join("; ", created.Errors.Select(error => error.Description)));

        if (createdAtUtc is { } when)
        {
            // Set after the insert, because the audit interceptor stamps CreatedAtUtc on the way in; the
            // paging tests need registration times they chose.
            await db.Users
                .Where(row => row.Id == user.Id)
                .ExecuteUpdateAsync(row => row.SetProperty(column => column.CreatedAtUtc, when), Token);
        }

        return user.Id;
    }

    /// <summary>Creates a staff account and returns its id.</summary>
    protected static async Task<int> CreateStaffAsync(MediaTestHost host, string email)
    {
        await using var scope = host.Factory.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();

        var user = new AppUser
        {
            UserName = email,
            Email = email,
            EmailConfirmed = true,
            UserType = UserType.Staff,
            FirstName = "Office",
            LastName = "Staff",
            IsActive = true,
            CreatedAtUtc = DateTime.UtcNow,
        };

        var created = await users.CreateAsync(user);
        Assert.True(created.Succeeded, string.Join("; ", created.Errors.Select(error => error.Description)));

        return user.Id;
    }

    protected async Task<int> AddAddressAsync(
        int userId,
        string recipientName,
        string districtName,
        bool isDefault,
        string? label = null)
    {
        using var scope = SqlServer.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var address = new CustomerAddress
        {
            UserId = userId,
            Label = label,
            RecipientName = recipientName,
            Phone = "+94771234567",
            Line1 = "14 Galle Road",
            Line2 = null,
            City = "Kollupitiya",
            DistrictId = await DistrictIdAsync(districtName),
            PostalCode = "00300",
            Landmark = null,
            IsDefault = isDefault,
            CreatedAtUtc = DateTime.UtcNow,
        };

        db.CustomerAddresses.Add(address);
        await db.SaveChangesAsync(Token);

        return address.Id;
    }

    /// <summary>Places one real order through the public checkout, as a guest, with this email.</summary>
    protected async Task<PlacedOrder> PlaceOrderAsync(MediaTestHost host, string email, string firstName = "Nimali")
    {
        using var shopper = host.CreateAnonymousClient();

        var variantId = await InStockVariantIdAsync();

        using var added = await shopper.PostAsJsonAsync(
            new Uri("/api/cart/items", UriKind.Relative),
            new { variantId, quantity = 1 },
            JsonOptions,
            Token);

        Assert.Equal(HttpStatusCode.OK, added.StatusCode);

        var districtId = await DistrictIdAsync("Colombo");
        var deliveryMethodId = await StandardDeliveryIdAsync();

        using var quote = await shopper.GetAsync(
            new Uri($"/api/checkout?districtId={districtId}&deliveryMethodId={deliveryMethodId}", UriKind.Relative),
            Token);

        Assert.Equal(HttpStatusCode.OK, quote.StatusCode);
        var session = await quote.Content.ReadFromJsonAsync<JsonElement>(JsonOptions, Token);

        using var placed = await shopper.PostAsJsonAsync(
            new Uri("/api/checkout/orders", UriKind.Relative),
            new
            {
                checkoutKey = session.GetProperty("checkoutKey").GetGuid(),
                firstName,
                lastName = "Perera",
                email,
                phone = "0771234567",
                deliveryMethodId,
                districtId,
                paymentMethodCode = PaymentProviderCodes.BankTransfer,
                expectedGrandTotal = session.GetProperty("totals").GetProperty("grandTotal").GetDecimal(),
                address = new
                {
                    recipientName = $"{firstName} Perera",
                    phone = "0771234567",
                    line1 = "14 Galle Road",
                    city = "Kollupitiya",
                },
            },
            JsonOptions,
            Token);

        Assert.Equal(HttpStatusCode.Created, placed.StatusCode);

        var body = await placed.Content.ReadFromJsonAsync<JsonElement>(JsonOptions, Token);

        return new PlacedOrder(
            body.GetProperty("publicId").GetGuid(),
            body.GetProperty("orderNumber").GetString()!,
            body.GetProperty("grandTotal").GetDecimal());
    }

    /// <summary>
    /// Puts one order on an account with doc 08 §3.1 D5's own statement — restricted to this one order.
    /// </summary>
    protected async Task LinkAsync(Guid orderPublicId, int customerId)
    {
        using var scope = SqlServer.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var updated = await db.Orders
            .Where(order => order.PublicId == orderPublicId && order.CustomerUserId == null)
            .ExecuteUpdateAsync(order => order.SetProperty(row => row.CustomerUserId, (int?)customerId), Token);

        Assert.Equal(1, updated);
    }

    /// <summary>Approves an order's bank transfer through the real admin route, so it is paid.</summary>
    protected static async Task ApproveAsync(MediaTestHost host, Guid orderPublicId)
    {
        using var approver = await host.SignInAsync(Permissions.Orders.Approve);

        using var response = await approver.PostAsJsonAsync(
            new Uri($"/api/admin/orders/bank-transfers/{orderPublicId:D}/approve", UriKind.Relative),
            new { note = "Paid, for the customer-screen fixture." },
            JsonOptions,
            Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    protected static async Task<JsonElement> GetJsonAsync(HttpClient client, string url)
    {
        using var response = await client.GetAsync(new Uri(url, UriKind.Relative), Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions, Token);
    }

    protected async Task<int> DistrictIdAsync(string name)
    {
        using var scope = SqlServer.CreateScope();

        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().Districts
            .AsNoTracking()
            .Where(district => district.Name == name)
            .Select(district => district.Id)
            .SingleAsync(Token);
    }

    private async Task<int> StandardDeliveryIdAsync()
    {
        using var scope = SqlServer.CreateScope();

        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().DeliveryMethods
            .AsNoTracking()
            .Where(method => method.Code == DeliveryMethod.StandardCode)
            .Select(method => method.Id)
            .SingleAsync(Token);
    }

    private async Task<int> InStockVariantIdAsync()
    {
        using var scope = SqlServer.CreateScope();

        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().ProductVariants
            .AsNoTracking()
            .Where(variant => variant.IsActive && variant.Product!.IsPublished)
            .Where(variant =>
                variant.Product!.Department!.IsActive && variant.Product!.PrimaryCategory!.IsActive)
            .Where(variant => variant.StockLevel != null && variant.StockLevel.Available > 2)
            .OrderByDescending(variant => variant.Id)
            .Skip(VariantBandOffset)
            .Select(variant => variant.Id)
            .FirstAsync(Token);
    }

    protected sealed record PlacedOrder(Guid PublicId, string OrderNumber, decimal GrandTotal);
}
