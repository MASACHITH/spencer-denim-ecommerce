using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SpencerDenim.Application.Sales.Checkout.Contracts;
using SpencerDenim.Application.Settings;
using SpencerDenim.Domain.Auditing;
using SpencerDenim.Domain.Identity;
using SpencerDenim.Domain.Ops;
using SpencerDenim.Domain.Settings;
using SpencerDenim.Infrastructure.Persistence;
using SpencerDenim.IntegrationTests.Infrastructure;

namespace SpencerDenim.IntegrationTests.Sales;

/// <summary>
/// Settings → Bank details, end to end (ADM-22, PAY-01; owner instruction of 2026-10-21): what a Super Admin
/// types into <c>PUT /api/admin/settings/bank</c> is what the next shopper is told at checkout, on their order
/// page and in the bank-instructions email; a change reaches the next order; clearing it withholds the email
/// and invents nothing; and every change is audited and tells the Super Admins.
/// <para>
/// <b>Why this class exists beside <c>AdminBankTransferEndpointTests</c>.</b> That class proves the endpoint
/// stores what it is sent, and the checkout and email tests prove what is shown from rows a fixture wrote with
/// <c>SetBankDetailsAsync</c>. Nothing proved the two halves meet — that the screen's save and the shopper's read
/// agree about the same keys — which is the #126 shape (each half green, the join untested).
/// </para>
/// <para>
/// <b>Every test puts the rows back</b> (bank keys to null, which is the seeded state) and removes the alerts it
/// raised; the audit rows stay, because <c>ops.AuditLogs</c> has no delete path, and the tests only count rows
/// newer than their own baseline.
/// </para>
/// </summary>
public sealed class BankDetailsEndToEndTests(SqlServerDatabaseFixture sqlServer) : BankTransferTestBase(sqlServer)
{
    private const string SettingsUrl = "/api/admin/settings";
    private const string BankUrl = "/api/admin/settings/bank";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly DateTime _startedAt = DateTime.UtcNow.AddSeconds(-1);
    private long _auditBaseline;
    private int _alertBaseline;

    public override async ValueTask DisposeAsync()
    {
        await ClearBankDetailsAsync();

        if (SqlServer.IsAvailable)
        {
            using var scope = SqlServer.CreateScope();

            await scope.ServiceProvider.GetRequiredService<AppDbContext>().AdminAlerts
                .Where(alert => alert.AlertType == AdminAlertTypes.SensitiveSettingsChanged
                    && alert.Id > _alertBaseline
                    && alert.CreatedAtUtc >= _startedAt)
                .ExecuteDeleteAsync(CancellationToken.None);
        }

        await base.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    // ------------------------------------------------------------------------------ what the shopper sees

    [Fact]
    public async Task Bank_details_saved_in_admin_reach_checkout_the_order_page_and_the_instructions_email()
    {
        await BaselineAsync();
        using var admin = await SuperAdminAsync();

        await SaveAsync(admin, new()
        {
            ["bankName"] = "Verification Bank PLC",
            ["branch"] = "Verification Branch",
            ["accountName"] = "Verification Account (Pvt) Ltd",
            ["accountNumber"] = "1111 2222 3333",
            ["instructions"] = "Quote the order number as the reference.",
        });

        var (placed, _) = await PlaceOrderAsync();

        // Checkout: the placement answer carries no instructions itself — the storefront's checkout navigates
        // straight to the order page (checkout-page.ts, router.navigate(['/order', publicId])), so the order
        // page's payload, read anonymously by the order's own link, IS checkout's bank-transfer instructions.
        Assert.Null(placed.Payment);

        var page = await OrderPageAsync(placed.PublicId);
        AssertShows(page.Payment, "Verification Bank PLC", "Verification Branch", "Verification Account (Pvt) Ltd", "1111 2222 3333");
        Assert.Equal("Quote the order number as the reference.", page.Payment!.Notes);
        Assert.Equal(placed.OrderNumber, page.Payment.Reference);

        // The bank-instructions email, queued in the placement's transaction.
        var instructions = Assert.Single(
            await OutboxAsync(placed.PublicId),
            row => row.TemplateCode == EmailTemplateCodes.BankTransferInstructions);

        foreach (var expected in new[] { "Verification Bank PLC", "Verification Branch", "Verification Account (Pvt) Ltd", "1111 2222 3333" })
        {
            Assert.Contains(expected, instructions.BodyText!, StringComparison.Ordinal);
            Assert.Contains(expected, instructions.BodyHtml!, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task A_changed_account_number_reaches_the_next_order_and_the_open_order_page()
    {
        await BaselineAsync();
        using var admin = await SuperAdminAsync();

        await SaveAsync(admin, FullDetails("4444 5555 6666"));
        var (first, _) = await PlaceOrderAsync();

        await SaveAsync(admin, new() { ["accountNumber"] = "7777 8888 9999" });

        var (second, _) = await PlaceOrderAsync();
        var secondPage = await OrderPageAsync(second.PublicId);
        AssertShows(secondPage.Payment, "7777 8888 9999");
        Assert.DoesNotContain(secondPage.Payment!.Fields, field => field.Value == "4444 5555 6666");

        var secondEmail = Assert.Single(
            await OutboxAsync(second.PublicId),
            row => row.TemplateCode == EmailTemplateCodes.BankTransferInstructions);
        Assert.Contains("7777 8888 9999", secondEmail.BodyText!, StringComparison.Ordinal);

        // The order page reads the settings live (BankTransferSettingsReader, no cache), so a corrected number
        // reaches a customer who has not paid yet; their already-sent email keeps the number it was sent with.
        var firstPage = await OrderPageAsync(first.PublicId);
        AssertShows(firstPage.Payment, "7777 8888 9999");
    }

    [Fact]
    public async Task Cleared_bank_details_withhold_the_email_and_invent_no_account()
    {
        await BaselineAsync();
        using var admin = await SuperAdminAsync();

        await SaveAsync(admin, FullDetails("1212 3434 5656"));

        await SaveAsync(admin, new()
        {
            ["bankName"] = null,
            ["branch"] = null,
            ["accountName"] = null,
            ["accountNumber"] = null,
            ["instructions"] = null,
        });

        foreach (var key in BankKeys)
        {
            Assert.Null(await StoredAsync(key));
        }

        var (placed, _) = await PlaceOrderAsync();

        // No instructions block at all — not a half-block, not a placeholder number.
        var page = await OrderPageAsync(placed.PublicId);
        Assert.Null(page.Payment);

        var queued = await OutboxAsync(placed.PublicId);
        Assert.DoesNotContain(queued, row => row.TemplateCode == EmailTemplateCodes.BankTransferInstructions);

        var confirmation = Assert.Single(queued, row => row.TemplateCode == EmailTemplateCodes.OrderPlaced);
        Assert.DoesNotContain("1212 3434 5656", confirmation.BodyText!, StringComparison.Ordinal);
        Assert.DoesNotContain("Account number", confirmation.BodyText!, StringComparison.OrdinalIgnoreCase);
    }

    // ------------------------------------------------------------------------------ audit and alert

    [Fact]
    public async Task Every_bank_change_is_audited_per_key_and_raises_one_sensitive_alert_naming_keys_only()
    {
        await BaselineAsync();
        using var admin = await SuperAdminAsync();

        await SaveAsync(admin, new()
        {
            ["bankName"] = "Audit Bank PLC",
            ["accountNumber"] = "9090 8080 7070",
        });

        var audits = await AuditRowsAsync();
        Assert.Equal(2, audits.Count);
        Assert.Contains(audits, row => row.EntityId == SettingKeys.Bank.BankName);
        Assert.Contains(audits, row => row.EntityId == SettingKeys.Bank.AccountNumber);
        Assert.All(audits, row => Assert.Equal(AuditActions.SettingChange, row.Action));
        Assert.All(audits, row => Assert.NotNull(row.UserId));

        var alert = Assert.Single(await AlertsAsync());
        Assert.Equal(AdminAlertSeverity.Critical, alert.Severity);
        Assert.Null(alert.AcknowledgedAtUtc);

        using var details = JsonDocument.Parse(alert.DetailsJson!);
        var keys = details.RootElement.GetProperty("keys").EnumerateArray().Select(key => key.GetString()).ToList();

        Assert.Equal(2, keys.Count);
        Assert.Contains(SettingKeys.Bank.BankName, keys);
        Assert.Contains(SettingKeys.Bank.AccountNumber, keys);
        Assert.True(details.RootElement.GetProperty("changedByUserId").GetInt32() > 0);

        // Names, never values — the account number is the thing a hijacker would be changing.
        Assert.DoesNotContain("9090 8080 7070", alert.DetailsJson, StringComparison.Ordinal);
        Assert.DoesNotContain("9090 8080 7070", alert.Title, StringComparison.Ordinal);
        Assert.DoesNotContain("Audit Bank PLC", alert.DetailsJson, StringComparison.Ordinal);
        Assert.DoesNotContain("@", alert.DetailsJson, StringComparison.Ordinal);

        // Who sees the banner (GrantsAll only) is AdminAlertVisibility's rule, proved for this alert type by
        // PayPalSettingsEndpointTests.The_alert_is_visible_to_a_Super_Admin_and_not_to_order_staff.
    }

    [Fact]
    public async Task An_unchanged_or_refused_bank_save_raises_no_alert_and_writes_no_audit_row()
    {
        await BaselineAsync();
        using var admin = await SuperAdminAsync();

        await SaveAsync(admin, new() { ["bankName"] = "Steady Bank PLC" });

        var audits = (await AuditRowsAsync()).Count;
        var alerts = (await AlertsAsync()).Count;

        // The same value again is not a change.
        await SaveAsync(admin, new() { ["bankName"] = "Steady Bank PLC" });

        // And a Store Manager-shaped caller (Settings.Edit only) is refused before anything is written.
        using var editor = await MediaTestHost.SignInAsync(Factory, Permissions.Settings.Edit);
        using (var refused = await editor.PutAsJsonAsync(
            BankUrl,
            new Dictionary<string, object?> { ["bankName"] = "Not theirs", ["rowVersion"] = await VersionAsync(admin) },
            Json,
            Token))
        {
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        }

        Assert.Equal(audits, (await AuditRowsAsync()).Count);
        Assert.Equal(alerts, (await AlertsAsync()).Count);
        Assert.Equal("Steady Bank PLC", await StoredAsync(SettingKeys.Bank.BankName));
    }

    // ------------------------------------------------------------------------------ helpers

    private static Dictionary<string, object?> FullDetails(string accountNumber) => new()
    {
        ["bankName"] = "Verification Bank PLC",
        ["branch"] = "Verification Branch",
        ["accountName"] = "Verification Account (Pvt) Ltd",
        ["accountNumber"] = accountNumber,
        ["instructions"] = null,
    };

    private static void AssertShows(OrderPaymentInstructionsDto? payment, params string[] values)
    {
        Assert.NotNull(payment);

        foreach (var value in values)
        {
            Assert.Contains(payment.Fields, field => field.Value == value);
        }
    }

    /// <summary>A Super Admin's two settings permissions (the bank keys need <c>Settings.Payments</c>).</summary>
    private Task<HttpClient> SuperAdminAsync() =>
        MediaTestHost.SignInAsync(Factory, Permissions.Settings.Payments, Permissions.Settings.Edit);

    private async Task BaselineAsync()
    {
        using var scope = CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        _auditBaseline = await db.AuditLogs.AsNoTracking().MaxAsync(log => (long?)log.Id, Token) ?? 0;
        _alertBaseline = await db.AdminAlerts.AsNoTracking().MaxAsync(alert => (int?)alert.Id, Token) ?? 0;
    }

    private static async Task<string> VersionAsync(HttpClient client)
    {
        var body = await client.GetFromJsonAsync<JsonElement>(SettingsUrl, Token);

        return body.GetProperty("groups").EnumerateArray()
            .Single(group => group.GetProperty("groupKey").GetString() == SettingKeys.Bank.Group)
            .GetProperty("rowVersion").GetString()!;
    }

    private static async Task SaveAsync(HttpClient client, Dictionary<string, object?> fields)
    {
        fields["rowVersion"] = await VersionAsync(client);

        using var response = await client.PutAsJsonAsync(BankUrl, fields, Json, Token);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    private async Task<OrderConfirmationDto> OrderPageAsync(Guid publicId)
    {
        var visitor = NewCheckoutVisitor();

        using var response = await visitor.Http.GetAsync(
            new Uri($"{CheckoutApiClient.OrderPath}/{publicId:D}", UriKind.Relative),
            Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return (await response.Content.ReadFromJsonAsync<OrderConfirmationDto>(CheckoutApiClient.JsonOptions, Token))!;
    }

    private async Task<string?> StoredAsync(string key)
    {
        using var scope = CreateScope();

        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().Settings
            .AsNoTracking()
            .Where(setting => setting.Key == key)
            .Select(setting => setting.Value)
            .SingleAsync(Token);
    }

    private async Task<List<EmailOutboxMessage>> OutboxAsync(Guid publicId)
    {
        using var scope = CreateScope();
        var id = publicId.ToString("D");

        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().EmailOutbox
            .AsNoTracking()
            .Where(message => message.RelatedEntityId == id)
            .OrderBy(message => message.Id)
            .ToListAsync(Token);
    }

    private async Task<List<AuditLog>> AuditRowsAsync()
    {
        using var scope = CreateScope();

        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().AuditLogs
            .AsNoTracking()
            .Where(log => log.EntityType == SettingsAuditTrail.EntityType
                && log.Id > _auditBaseline
                && log.EntityId!.StartsWith("Bank."))
            .OrderBy(log => log.Id)
            .ToListAsync(Token);
    }

    private async Task<List<AdminAlert>> AlertsAsync()
    {
        using var scope = CreateScope();

        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().AdminAlerts
            .AsNoTracking()
            .Where(alert => alert.AlertType == AdminAlertTypes.SensitiveSettingsChanged && alert.Id > _alertBaseline)
            .OrderBy(alert => alert.Id)
            .ToListAsync(Token);
    }
}
