using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SpencerDenim.Domain.Identity;
using SpencerDenim.Domain.Ops;
using SpencerDenim.Infrastructure.Persistence;
using SpencerDenim.IntegrationTests.Infrastructure;

namespace SpencerDenim.IntegrationTests.Api.Accounts;

/// <summary>
/// Shared setup for W3-01's endpoint tests (ACC-01, ACC-02): a host with a storefront origin configured,
/// a client that keeps cookies, and the one thing these tests could not be written without — a way to
/// read the <b>real one-time link out of the real queued email</b>.
/// </summary>
/// <remarks>
/// <b>Why the token is read out of <c>ops.EmailOutbox</c> and not out of a test hook.</b> The confirmation
/// and reset flows are only correct if the token the producer puts in the message is the token the
/// redeemer accepts, and the only way to assert that is to take the one from the message. A test that
/// minted its own token, or read one through an internal helper, would pass with a producer that composed
/// a broken link — which is precisely the class of defect <c>EmailTemplateCodes</c>' hand-off note was
/// written about.
/// <para>
/// <b>It works because nothing in a test ever sends.</b> <c>ApiFactory</c>'s <c>NoMailTransport</c> is
/// never configured and throws if called, so doc 08 §3.8's G-03 body destruction — which happens
/// <i>on send</i> — has not run and the body is still on the row. That is the real behaviour of an
/// unconfigured shop, not a test affordance.
/// </para>
/// <para>
/// <b><c>Email:StorefrontBaseUrl</c> is set here deliberately</b>, because the <i>absence</i> of it is a
/// separate behaviour with its own test (<c>CustomerAccountEmailTests</c>): without an origin there is no
/// link, so no confirmation or reset message is queued at all.
/// </para>
/// </remarks>
/// <remarks>
/// <b>It derives from <c>SalesSchemaTestBase</c> rather than repeating it.</b> ACC-02's order history is
/// about <c>sales.Orders</c>, and doc 08 §3.5 gives that table real foreign keys to a payment provider, a
/// delivery method, a pickup location, a product and a variant — so there is no way to test an order
/// without the reference rows that base already knows how to create. Its <c>DbContext</c> and
/// <c>Writer</c> read the same database the host under test writes to; they are a second connection to one
/// database, not a second database.
/// </remarks>
public abstract class CustomerAccountTestBase(SqlServerDatabaseFixture sqlServer)
    : SpencerDenim.IntegrationTests.Sales.SalesSchemaTestBase(sqlServer)
{
    /// <summary>The origin every link in these tests is built from.</summary>
    public const string StorefrontOrigin = "https://shop.test";

    /// <summary>A password that satisfies the length rule and all four of Identity's character classes.</summary>
    public const string GoodPassword = "Correct-Horse-Battery-1!";

    /// <summary>Skips when there is no SQL Server, then makes sure the schema is there.</summary>
    protected async Task ReadyAsync()
    {
        SqlServer.SkipIfUnavailable();
        await TestDatabaseMigrator.MigrateAsync(SqlServer.ConnectionString, Token);
    }

    /// <summary>
    /// A host with the storefront origin configured, so confirmation and reset links can be built.
    /// </summary>
    /// <param name="extra">
    /// Further configuration, applied after the origin — how a lockout test shrinks the threshold
    /// rather than sending ten requests to discover the default.
    /// </param>
    /// <param name="configureServices">
    /// Service overrides, applied after the application's own registrations — how the sign-in race test
    /// replaces the password hasher with one that locks the account mid-verification, which is the only
    /// deterministic way to reach the post-verification lockout guard.
    /// </param>
    /// <param name="commandInterceptor">
    /// An EF command interceptor — how the address-cap race widens the window between the count and the
    /// insert so that eight concurrent tabs really do overlap. #146's own tests do the same thing
    /// ("with delayed token writes to force the worst case"); without it the window is one round trip
    /// and the race is not reliably reproducible at all.
    /// </param>
    protected ApiFactory CreateHost(
        IReadOnlyDictionary<string, string?>? extra = null,
        Action<IServiceCollection>? configureServices = null,
        DbCommandInterceptor? commandInterceptor = null)
    {
        var configuration = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["Email:StorefrontBaseUrl"] = StorefrontOrigin,
        };

        if (extra is not null)
        {
            foreach (var (key, value) in extra)
            {
                configuration[key] = value;
            }
        }

        return new ApiFactory(
            SqlServer.ConnectionString,
            additionalConfiguration: configuration,
            commandInterceptor: commandInterceptor,
            configureServices: configureServices);
    }

    /// <summary>
    /// A client that keeps cookies, because every signed-in route is identified by one.
    /// </summary>
    /// <remarks>
    /// <c>HandleCookies</c> is the default, and it is named here anyway: a test that lost the cookie
    /// would see 401 from every account route and read as an authorization defect rather than as a client
    /// setting.
    /// </remarks>
    protected static HttpClient CreateClient(ApiFactory factory) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            HandleCookies = true,
        });

    /// <summary>An address nothing else in the suite will use.</summary>
    protected static string UniqueEmail(string label) => $"{label}-{Guid.NewGuid():N}@example.com";

    /// <summary>
    /// Registers a customer through the real endpoint and returns the address.
    /// </summary>
    /// <remarks>
    /// Through the endpoint rather than through <c>UserManager</c>, so the two messages doc 11 P11 step
    /// A.3 queues are actually queued and the confirmation link exists to be read.
    /// </remarks>
    protected static async Task<string> RegisterAsync(
        HttpClient client,
        string email,
        string password = GoodPassword,
        string firstName = "Nimal",
        string? lastName = "Perera",
        string? phone = null)
    {
        using var response = await client.PostAsJsonAsync(
            "/api/account/register",
            new { firstName, lastName, email, password, phone },
            Token);

        Assert.Equal(System.Net.HttpStatusCode.Accepted, response.StatusCode);

        return email;
    }

    /// <summary>Signs in through the real endpoint and asserts it worked.</summary>
    protected static async Task<JsonElement> SignInAsync(
        HttpClient client,
        string email,
        string password = GoodPassword)
    {
        using var response = await client.PostAsJsonAsync(
            "/api/account/login",
            new { email, password },
            Token);

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);

        return await response.Content.ReadFromJsonAsync<JsonElement>(Token);
    }

    /// <summary>
    /// The <c>token</c> query parameter of the newest queued message with this template code, or null
    /// when no such message was queued.
    /// </summary>
    /// <remarks>
    /// <b>Parsed out of the plain-text body, which is the part a text-only client reads.</b> Both parts
    /// are stored (<c>EmailOutboxMessage.BodyText</c>), and asserting against the text part means a test
    /// cannot pass on an HTML-only body — which would reach a shopper as a blank message.
    /// <para>
    /// Null rather than an exception for "no message", because two tests are <i>about</i> nothing being
    /// queued and need to say so without catching.
    /// </para>
    /// </remarks>
    protected static async Task<string?> ReadLinkTokenAsync(ApiFactory factory, string templateCode, string toEmail)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var body = await dbContext.EmailOutbox
            .AsNoTracking()
            .Where(message => message.TemplateCode == templateCode && message.ToEmail == toEmail)
            .OrderByDescending(message => message.Id)
            .Select(message => message.BodyText)
            .FirstOrDefaultAsync(Token);

        if (body is null)
        {
            return null;
        }

        const string marker = "?token=";
        var start = body.IndexOf(marker, StringComparison.Ordinal);

        if (start < 0)
        {
            return null;
        }

        start += marker.Length;
        var end = start;

        while (end < body.Length && !char.IsWhiteSpace(body[end]))
        {
            end++;
        }

        return Uri.UnescapeDataString(body[start..end]);
    }

    /// <summary>How many messages with this template code are queued to this address.</summary>
    protected static async Task<int> CountQueuedAsync(ApiFactory factory, string templateCode, string toEmail)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        return await dbContext.EmailOutbox
            .AsNoTracking()
            .CountAsync(
                message => message.TemplateCode == templateCode && message.ToEmail == toEmail,
                Token);
    }

    /// <summary>Every template code queued to this address, newest last.</summary>
    protected static async Task<List<string>> QueuedTemplatesAsync(ApiFactory factory, string toEmail)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        return await dbContext.EmailOutbox
            .AsNoTracking()
            .Where(message => message.ToEmail == toEmail)
            .OrderBy(message => message.Id)
            .Select(message => message.TemplateCode)
            .ToListAsync(Token);
    }

    /// <summary>
    /// A staff account with this address, created directly through <c>UserManager</c>.
    /// </summary>
    /// <remarks>
    /// Doc 08 D7 makes an address unique across the whole store, so this is how the "a staff member tries
    /// to register as a customer" and "a staff address asks for a customer password reset" cases are set
    /// up — both of which doc 08 §3.1 DB10 says must answer exactly like an unknown address and send
    /// nothing.
    /// </remarks>
    protected static async Task CreateStaffUserAsync(ApiFactory factory, string email, string password)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var userManager = scope.ServiceProvider
            .GetRequiredService<Microsoft.AspNetCore.Identity.UserManager<SpencerDenim.Infrastructure.Identity.AppUser>>();

        var user = new SpencerDenim.Infrastructure.Identity.AppUser
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

        var result = await userManager.CreateAsync(user, password);
        Assert.True(result.Succeeded, string.Join("; ", result.Errors.Select(error => error.Description)));
    }

    /// <summary>The customer row with this address, read directly, or null.</summary>
    protected static async Task<CustomerRow?> ReadCustomerAsync(ApiFactory factory, string email)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var normalised = email.ToUpperInvariant();

        return await dbContext.Users
            .AsNoTracking()
            .Where(user => user.NormalizedEmail == normalised)
            .Select(user => new CustomerRow(
                user.Id,
                user.UserType,
                user.EmailConfirmed,
                user.IsActive,
                user.AccessFailedCount,
                user.LockoutEnd))
            .FirstOrDefaultAsync(Token);
    }

    /// <summary>How many one-time tokens of this kind the account is holding.</summary>
    protected static async Task<int> CountTokensAsync(ApiFactory factory, int userId, string name)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        return await dbContext.UserTokens
            .AsNoTracking()
            .CountAsync(row => row.UserId == userId && row.Name == name, Token);
    }

    /// <summary>What doc 08 §3.1's user row says, for the columns these tests are about.</summary>
    protected sealed record CustomerRow(
        int Id,
        UserType UserType,
        bool EmailConfirmed,
        bool IsActive,
        int AccessFailedCount,
        DateTimeOffset? LockoutEnd);

    /// <summary>The template codes these tests name, from the one place that declares them.</summary>
    protected static class Templates
    {
        public const string Confirmation = EmailTemplateCodes.EmailConfirmation;
        public const string Welcome = EmailTemplateCodes.Welcome;
        public const string AccountExists = EmailTemplateCodes.AccountExists;
        public const string PasswordReset = EmailTemplateCodes.PasswordReset;
    }
}
