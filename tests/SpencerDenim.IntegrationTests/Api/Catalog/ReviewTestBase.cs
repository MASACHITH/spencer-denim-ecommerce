using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SpencerDenim.Domain.Catalog;
using SpencerDenim.Domain.Sales;
using SpencerDenim.Domain.Settings;
using SpencerDenim.Infrastructure.Catalog;
using SpencerDenim.Infrastructure.Persistence;
using SpencerDenim.IntegrationTests.Api.Accounts;
using SpencerDenim.IntegrationTests.Infrastructure;
using SpencerDenim.IntegrationTests.Sales;

namespace SpencerDenim.IntegrationTests.Api.Catalog;

/// <summary>
/// What the review suites (PDP-08, ADM-16; #189) share: real customers registered and signed in through the real
/// endpoints, real orders linked (or deliberately not linked) to them, staff with exact permissions, and products
/// whose rating this class owns for the length of a test.
/// <para>
/// <b>Every route is called over HTTP against the real database</b> (#126: a screen whose only tests ran against a
/// Node stub was dead for a day and a half).
/// </para>
/// <para>
/// <b>A test owns the products it touches and puts them back</b> (AssemblyInfo's rule): the demo catalogue seeds a
/// rating on many products, and the recompute replaces it with the real figure, so each product's
/// <c>RatingAverage</c>/<c>RatingCount</c> is recorded before the first review and restored afterwards, its reviews
/// deleted, and <c>Reviews.RequireApproval</c> put back to its catalogued <c>true</c>.
/// </para>
/// </summary>
public abstract class ReviewTestBase(SqlServerDatabaseFixture sqlServer) : CustomerAccountTestBase(sqlServer), IAsyncDisposable
{
    protected static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly Dictionary<int, (decimal? Average, int Count)> _originalRatings = [];
    private readonly List<IDisposable> _disposables = [];
    private bool _approvalChanged;

    /// <summary>A visible product with a variant, the n-th from the end of the catalogue.</summary>
    protected async Task<TestProduct> ProductAsync(int index = 0)
    {
        using var scope = SqlServer.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var product = await db.Products
            .AsNoTracking()
            .Where(CatalogVisibility.VisibleProduct)
            .Where(candidate => db.ProductVariants.Any(variant => variant.ProductId == candidate.Id))
            .OrderByDescending(candidate => candidate.Id)
            .Skip(index)
            .Select(candidate => new
            {
                candidate.Id,
                candidate.Slug,
                candidate.DepartmentId,
                candidate.PrimaryCategoryId,
                VariantId = db.ProductVariants.Where(variant => variant.ProductId == candidate.Id).Min(variant => variant.Id),
                candidate.Stats!.RatingAverage,
                candidate.Stats!.RatingCount,
            })
            .FirstAsync(Token);

        if (!_originalRatings.ContainsKey(product.Id))
        {
            _originalRatings[product.Id] = (product.RatingAverage, product.RatingCount);

            // A previous run that died mid-test leaves its reviews behind; start clean.
            await db.ProductReviews.Where(review => review.ProductId == product.Id).ExecuteDeleteAsync(Token);
        }

        return new TestProduct(product.Id, product.Slug, product.VariantId, product.DepartmentId, product.PrimaryCategoryId);
    }

    /// <summary>A registered, signed-in customer with their own cookie-keeping client.</summary>
    protected async Task<TestCustomer> CustomerAsync(ApiFactory factory, string label = "reviewer", string firstName = "Nimal", string? lastName = "Perera")
    {
        var client = CreateClient(factory);
        _disposables.Add(client);

        var email = UniqueEmail(label);
        await RegisterAsync(client, email, firstName: firstName, lastName: lastName);
        await SignInAsync(client, email);

        var row = await ReadCustomerAsync(factory, email)
            ?? throw new InvalidOperationException("The customer just registered cannot be read.");

        return new TestCustomer(row.Id, email, client);
    }

    /// <summary>A staff client holding exactly these permissions, antiforgery header set.</summary>
    protected async Task<HttpClient> StaffAsync(ApiFactory factory, params string[] permissions)
    {
        var client = await MediaTestHost.SignInAsync(factory, permissions);
        _disposables.Add(client);
        return client;
    }

    /// <summary>
    /// An order containing the product, in the given status, linked to <paramref name="customerUserId"/> (or a guest
    /// order when null) with <paramref name="email"/> on it.
    /// </summary>
    protected async Task<int> OrderAsync(TestProduct product, int? customerUserId, string email, OrderStatus status)
    {
        var reference = await ReferenceAsync();

        var row = SalesRowWriter.OrderRow(
            reference.PaymentProviderCode,
            reference.StandardDeliveryMethodId,
            DeliveryMethod.StandardCode);

        row["CustomerUserId"] = customerUserId;
        row["Email"] = email;
        row["EmailNormalized"] = email.ToUpperInvariant();
        row["Status"] = (byte)status;
        row["StockState"] = (byte)(status == OrderStatus.PendingPayment ? OrderStockState.Reserved : OrderStockState.Committed);

        if (status is OrderStatus.Paid or OrderStatus.Processing or OrderStatus.Shipped or OrderStatus.Delivered)
        {
            row["PaidAtUtc"] = DateTime.UtcNow.AddDays(-10);
        }

        if (status is OrderStatus.Delivered)
        {
            row["DeliveredAtUtc"] = DateTime.UtcNow.AddDays(-1);
        }

        var orderId = await Writer.InsertAsync("sales.Orders", row, Token);

        await Writer.InsertAsync(
            "sales.OrderLines",
            SalesRowWriter.OrderLineRow(orderId, product.Id, product.VariantId, product.DepartmentId, product.CategoryId),
            Token);

        return orderId;
    }

    /// <summary>Sets <c>Reviews.RequireApproval</c> directly; the class restores it to the catalogued <c>true</c>.</summary>
    protected async Task SetRequireApprovalAsync(bool value)
    {
        _approvalChanged = true;

        using var scope = SqlServer.CreateScope();
        await scope.ServiceProvider.GetRequiredService<AppDbContext>().Settings
            .Where(setting => setting.Key == SettingKeys.Reviews.RequireApproval)
            .ExecuteUpdateAsync(setting => setting.SetProperty(row => row.Value, value ? "true" : "false"), Token);
    }

    protected static string Url(TestProduct product) => $"/api/account/reviews/{product.Id}";

    protected static Task<(HttpStatusCode Status, JsonElement Body)> CreateReviewAsync(HttpClient client, TestProduct product, object body) =>
        SendAsync(client, HttpMethod.Post, Url(product), body);

    protected static Task<(HttpStatusCode Status, JsonElement Body)> EditReviewAsync(HttpClient client, TestProduct product, object body) =>
        SendAsync(client, HttpMethod.Put, Url(product), body);

    protected static async Task<(HttpStatusCode Status, JsonElement Body)> SendAsync(HttpClient client, HttpMethod method, string url, object? body)
    {
        using var request = new HttpRequestMessage(method, new Uri(url, UriKind.Relative));

        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        using var response = await client.SendAsync(request, Token);
        var text = await response.Content.ReadAsStringAsync(Token);

        return (response.StatusCode, text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone());
    }

    protected static async Task<(HttpStatusCode Status, JsonElement Body)> GetJsonAsync(HttpClient client, string url)
    {
        using var response = await client.GetAsync(new Uri(url, UriKind.Relative), Token);
        var text = await response.Content.ReadAsStringAsync(Token);

        return (response.StatusCode, text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone());
    }

    /// <summary>A review that must be created (201), returning its id.</summary>
    protected static async Task<int> MustCreateAsync(HttpClient client, TestProduct product, int rating, string? title = null, string? body = null)
    {
        var (status, response) = await CreateReviewAsync(client, product, new { rating, title, body });
        Assert.True(status == HttpStatusCode.Created, $"The review answered {(int)status}: {response}");
        return response.GetProperty("review").GetProperty("id").GetInt32();
    }

    /// <summary>The review row as stored.</summary>
    protected async Task<ProductReview?> ReviewRowAsync(int reviewId)
    {
        using var scope = SqlServer.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().ProductReviews
            .AsNoTracking()
            .FirstOrDefaultAsync(review => review.Id == reviewId, Token);
    }

    /// <summary>The product's stored rating, as cards and the listing read it.</summary>
    protected async Task<(decimal? Average, int Count)> StatsAsync(TestProduct product)
    {
        using var scope = SqlServer.CreateScope();
        var stats = await scope.ServiceProvider.GetRequiredService<AppDbContext>().ProductStats
            .AsNoTracking()
            .Where(row => row.ProductId == product.Id)
            .Select(row => new { row.RatingAverage, row.RatingCount })
            .SingleAsync(Token);

        return (stats.RatingAverage, stats.RatingCount);
    }

    /// <summary>Sets a review's status directly (arranging a state, not testing moderation).</summary>
    protected async Task ForceStatusAsync(int reviewId, ReviewStatus status)
    {
        using var scope = SqlServer.CreateScope();
        await scope.ServiceProvider.GetRequiredService<AppDbContext>().ProductReviews
            .Where(review => review.Id == reviewId)
            .ExecuteUpdateAsync(review => review.SetProperty(row => row.Status, status), Token);
    }

    /// <summary>The review's current rowversion, base64, as the admin list gives it.</summary>
    protected async Task<string> RowVersionAsync(int reviewId)
    {
        var row = await ReviewRowAsync(reviewId) ?? throw new InvalidOperationException($"No review {reviewId}.");
        return Convert.ToBase64String(row.RowVersion!);
    }

    protected async Task<long> AuditMarkAsync()
    {
        using var scope = SqlServer.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().AuditLogs
            .AsNoTracking()
            .MaxAsync(row => (long?)row.Id, Token) ?? 0;
    }

    protected async Task<List<SpencerDenim.Domain.Auditing.AuditLog>> AuditAfterAsync(long mark, string entityType)
    {
        using var scope = SqlServer.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().AuditLogs
            .AsNoTracking()
            .Where(row => row.Id > mark && row.EntityType == entityType)
            .OrderBy(row => row.Id)
            .ToListAsync(Token);
    }

    /// <summary>
    /// The account host with registration and sign-in budgets wide enough for a test that needs a crowd of
    /// customers from one address. Those throttles have their own suites; they are not what these tests are about.
    /// </summary>
    protected ApiFactory CreateHost() => CreateHost(new Dictionary<string, string?>(StringComparer.Ordinal)
    {
        ["CustomerSecurity:RegistrationsPerWindow"] = "1000",
        ["CustomerSecurity:SignInAttemptsPerWindow"] = "1000",
    });

    /// <summary>
    /// Takes the product's stats row with <c>UPDLOCK</c> on a separate connection and holds it until disposed, so
    /// requests sent meanwhile are all in flight at once. <see cref="StatsRowHold.WaitForBlockedAsync"/> waits until
    /// that many requests are actually waiting on a lock in this database.
    /// </summary>
    protected async Task<StatsRowHold> HoldStatsRowAsync(TestProduct product)
    {
        var connection = new Microsoft.Data.SqlClient.SqlConnection(SqlServer.ConnectionString);
        await connection.OpenAsync(Token);
        var transaction = (Microsoft.Data.SqlClient.SqlTransaction)await connection.BeginTransactionAsync(Token);

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT COUNT(*) FROM catalog.ProductStats WITH (UPDLOCK, ROWLOCK) WHERE ProductId = @id;";
            command.Parameters.AddWithValue("@id", product.Id);
            await command.ExecuteScalarAsync(Token);
        }

        return new StatsRowHold(connection, transaction, SqlServer.ConnectionString);
    }

    public virtual async ValueTask DisposeAsync()
    {
        foreach (var disposable in _disposables)
        {
            disposable.Dispose();
        }

        if (SqlServer.IsAvailable)
        {
            using var scope = SqlServer.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            foreach (var (productId, (average, count)) in _originalRatings)
            {
                await db.ProductReviews.Where(review => review.ProductId == productId).ExecuteDeleteAsync(CancellationToken.None);
                await db.ProductStats
                    .Where(stats => stats.ProductId == productId)
                    .ExecuteUpdateAsync(
                        stats => stats.SetProperty(row => row.RatingAverage, average).SetProperty(row => row.RatingCount, count),
                        CancellationToken.None);
            }

            if (_approvalChanged)
            {
                await db.Settings
                    .Where(setting => setting.Key == SettingKeys.Reviews.RequireApproval)
                    .ExecuteUpdateAsync(setting => setting.SetProperty(row => row.Value, "true"), CancellationToken.None);
            }
        }

        Dispose();
        GC.SuppressFinalize(this);
    }
}

/// <summary>A product a review test owns.</summary>
public sealed record TestProduct(int Id, string Slug, int VariantId, int DepartmentId, int CategoryId);

/// <summary>A signed-in customer and their client.</summary>
public sealed record TestCustomer(int Id, string Email, HttpClient Client);

/// <summary>A held stats-row lock; disposing commits and lets the waiting requests go.</summary>
public sealed class StatsRowHold(
    Microsoft.Data.SqlClient.SqlConnection connection,
    Microsoft.Data.SqlClient.SqlTransaction transaction,
    string connectionString) : IAsyncDisposable
{
    /// <summary>Waits (up to 20 s) until at least <paramref name="expected"/> requests in this database are blocked.</summary>
    public async Task WaitForBlockedAsync(int expected)
    {
        await using var probe = new Microsoft.Data.SqlClient.SqlConnection(connectionString);
        await probe.OpenAsync(TestContext.Current.CancellationToken);

        var deadline = DateTime.UtcNow.AddSeconds(20);

        while (DateTime.UtcNow < deadline)
        {
            await using var command = probe.CreateCommand();
            command.CommandText =
                "SELECT COUNT(*) FROM sys.dm_exec_requests WHERE blocking_session_id <> 0 AND database_id = DB_ID();";
            var blocked = (int)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;

            if (blocked >= expected)
            {
                return;
            }

            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        Assert.Fail($"Fewer than {expected} requests ever waited on the held stats row; the race was not staged.");
    }

    public async ValueTask DisposeAsync()
    {
        await transaction.CommitAsync();
        await transaction.DisposeAsync();
        await connection.DisposeAsync();
    }
}
