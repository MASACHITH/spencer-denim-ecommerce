using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SpencerDenim.Domain.Auditing;
using SpencerDenim.Domain.Content;
using SpencerDenim.Domain.Identity;
using SpencerDenim.Infrastructure.Persistence;
using SpencerDenim.IntegrationTests.Infrastructure;

namespace SpencerDenim.IntegrationTests.Content;

/// <summary>
/// The contact enquiry against the real API and a real SQL Server (GLB-10, doc 11 P15 part B).
/// <para>
/// The properties that need a real database: the row lands as doc 08 §3.7 shapes it, the retention of
/// §8.4 is <i>possible</i> as two set-based statements, the hard delete really removes the row, and the
/// audit row for it carries no part of the message.
/// </para>
/// </summary>
public sealed class ContactEnquiryEndpointTests(SqlServerDatabaseFixture sqlServer)
{
    private const string SubmitUrl = "/api/content/contact";
    private const string InboxUrl = "/api/admin/contact-enquiries/";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    // ---- the public submission --------------------------------------------------------------------

    /// <summary>
    /// An enquiry from an anonymous visitor lands as <b>New</b> with the submitter's address, and the
    /// answer promises nothing that cannot happen.
    /// <para>
    /// <b>The confirmation is asserted not to mention email</b>, which is the specific defect checkout
    /// was marked Major for: doc 11 P15's notifications are "none in v1" and <c>ops.EmailOutbox</c> does
    /// not exist, so a sentence saying "we have emailed you a copy" would be a promise nothing could
    /// keep.
    /// </para>
    /// </summary>
    [Fact]
    public async Task An_enquiry_lands_as_new_and_the_answer_promises_no_email()
    {
        await using var host = await StartAsync();
        using var client = host.CreateAnonymousClient();

        var subject = "Sizing " + Guid.NewGuid().ToString("N")[..8];

        using var response = await client.PostAsJsonAsync(
            new Uri(SubmitUrl, UriKind.Relative),
            new
            {
                name = "Nimal Perera",
                email = "nimal@example.com",
                phone = "077 123 4567",
                subject,
                message = "Is the linen shirt true to size?",
                orderNumber = "cw-2026-0042",
            },
            JsonOptions,
            Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions, Token);
        var message = body.GetProperty("message").GetString() ?? "";

        Assert.False(string.IsNullOrWhiteSpace(message));
        foreach (var promise in new[] { "email", "e-mail", "inbox", "confirmation" })
        {
            Assert.DoesNotContain(promise, message, StringComparison.OrdinalIgnoreCase);
        }

        await using var scope = host.Factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var stored = await dbContext.ContactSubmissions
            .AsNoTracking()
            .SingleAsync(submission => submission.Subject == subject, Token);

        Assert.Equal(ContactSubmissionStatus.New, stored.Status);
        Assert.Equal("Nimal Perera", stored.Name);
        Assert.Equal("CW-2026-0042", stored.OrderNumber);
        Assert.Null(stored.HandledByUserId);

        // **Doc 11 P15 step 6's IP is not asserted to be present here, and that is a limit of the
        // harness rather than of the feature.** `WebApplicationFactory`'s in-memory `TestServer` sets no
        // `Connection.RemoteIpAddress` at all, so the column is legitimately NULL in this test; Kestrel
        // sets it in every real deployment. What the handler does with an address it *is* given is proved
        // in `ContactEnquiryTests.A_valid_enquiry_is_stored_as_New_with_the_submitters_address`, which
        // passes one in.
        //
        // What is asserted is the property that matters either way: a NULL address is stored as NULL
        // rather than as an empty string, so "not recorded" and "cleared after ninety days" are the same
        // value and the retention step in `The_retention_of_section_8_4_is_two_set_based_statements` has
        // somewhere to write.
        Assert.True(
            stored.IpAddress is null || stored.IpAddress.Trim().Length > 0,
            "An IP address that is present must not be an empty string.");
    }

    /// <summary>
    /// Doc 11 §7 G-06's honeypot, end to end: the status, the body and the absence of a row.
    /// <para>
    /// The answer has to be <b>indistinguishable</b> from a real submission's, or a script learns which
    /// field to leave alone and the technique is worth nothing.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_filled_honeypot_gets_the_same_answer_and_writes_nothing()
    {
        await using var host = await StartAsync();
        using var client = host.CreateAnonymousClient();

        var realSubject = "Real " + Guid.NewGuid().ToString("N")[..8];
        var botSubject = "Bot " + Guid.NewGuid().ToString("N")[..8];

        using var real = await client.PostAsJsonAsync(
            new Uri(SubmitUrl, UriKind.Relative),
            Enquiry(realSubject),
            JsonOptions,
            Token);

        using var bot = await client.PostAsJsonAsync(
            new Uri(SubmitUrl, UriKind.Relative),
            Enquiry(botSubject, website: "http://spam.example"),
            JsonOptions,
            Token);

        Assert.Equal(real.StatusCode, bot.StatusCode);
        Assert.Equal(
            await real.Content.ReadAsStringAsync(Token),
            await bot.Content.ReadAsStringAsync(Token));

        await using var scope = host.Factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        Assert.True(await dbContext.ContactSubmissions.AnyAsync(row => row.Subject == realSubject, Token));
        Assert.False(await dbContext.ContactSubmissions.AnyAsync(row => row.Subject == botSubject, Token));
    }

    [Fact]
    public async Task A_bad_enquiry_is_a_400_naming_each_field()
    {
        await using var host = await StartAsync();
        using var client = host.CreateAnonymousClient();

        using var response = await client.PostAsJsonAsync(
            new Uri(SubmitUrl, UriKind.Relative),
            new { name = "", email = "nope", message = "" },
            JsonOptions,
            Token);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions, Token);
        var errors = body.GetProperty("errors");

        Assert.True(errors.TryGetProperty("name", out _));
        Assert.True(errors.TryGetProperty("email", out _));
        Assert.True(errors.TryGetProperty("message", out _));
    }

    /// <summary>
    /// The form is anonymous, which is the whole point of it — a shopper with no account can ask a
    /// question. Asserted so that a future change requiring a session is a deliberate one.
    /// </summary>
    [Fact]
    public async Task The_form_needs_no_account()
    {
        await using var host = await StartAsync();
        using var client = host.CreateAnonymousClient();

        using var response = await client.PostAsJsonAsync(
            new Uri(SubmitUrl, UriKind.Relative),
            Enquiry("Anonymous " + Guid.NewGuid().ToString("N")[..8]),
            JsonOptions,
            Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ---- the inbox -------------------------------------------------------------------------------

    /// <summary>
    /// Doc 08 §3.7's G-06 resolution: the inbox is governed by the <b>Customers</b> module, not Content.
    /// A Content Editor can rewrite the Contact page and cannot read a single enquiry.
    /// </summary>
    [Fact]
    public async Task Reading_the_inbox_needs_Customers_View_and_not_Content_View()
    {
        await using var host = await StartAsync();

        using var contentEditor = await host.SignInAsync(
            Permissions.Content.View, Permissions.Content.Edit);
        using var refused = await contentEditor.GetAsync(new Uri(InboxUrl, UriKind.Relative), Token);
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);

        using var staff = await host.SignInAsync(Permissions.Customers.View);
        using var allowed = await staff.GetAsync(new Uri(InboxUrl, UriKind.Relative), Token);
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
    }

    /// <summary>
    /// Changing a status needs <c>Customers.Edit</c>, so a reader cannot move one.
    /// </summary>
    [Fact]
    public async Task Changing_a_status_needs_Customers_Edit()
    {
        await using var host = await StartAsync();
        var id = await SubmitAsync(host, "Perm " + Guid.NewGuid().ToString("N")[..8]);

        using var reader = await host.SignInAsync(Permissions.Customers.View);

        using var response = await PutAsync(reader, $"{InboxUrl}{id}/status", new { status = "Replied" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Deleting_needs_Customers_Delete()
    {
        await using var host = await StartAsync();
        var id = await SubmitAsync(host, "Perm " + Guid.NewGuid().ToString("N")[..8]);

        using var editor = await host.SignInAsync(Permissions.Customers.View, Permissions.Customers.Edit);

        using var response = await PostAsync(
            editor, $"{InboxUrl}{id}/delete", new { reason = "Contains card details" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <summary>
    /// The list payload carries a preview and <b>not</b> the message.
    /// <para>
    /// Doc 11 P8: a customer may paste bank account details into it, and a list is the payload most
    /// likely to be cached, screenshotted into a support ticket, or widened into an export later.
    /// </para>
    /// </summary>
    [Fact]
    public async Task The_list_carries_a_preview_and_not_the_message()
    {
        await using var host = await StartAsync();

        var subject = "Long " + Guid.NewGuid().ToString("N")[..8];
        var secret = "and my account number is 1234567890123456 at Sampath Bank";
        var message = new string('x', 300) + secret;

        await SubmitAsync(host, subject, message);

        using var staff = await host.SignInAsync(Permissions.Customers.View);
        var list = await GetJsonAsync(staff, InboxUrl);

        var raw = list.GetRawText();
        Assert.DoesNotContain("1234567890123456", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("Sampath", raw, StringComparison.Ordinal);

        var row = list.GetProperty("items").EnumerateArray()
            .Single(item => item.GetProperty("subject").GetString() == subject);

        Assert.True(row.GetProperty("preview").GetString()!.Length <= 120);
        Assert.False(row.TryGetProperty("message", out _));
    }

    /// <summary>
    /// The newest enquiry is first (doc 11 P15 step 7), and the unread count is the whole inbox's rather
    /// than the filter's — it is the badge's number, and a badge that changed with a filter is a badge
    /// nobody could trust.
    /// </summary>
    [Fact]
    public async Task The_inbox_is_newest_first_and_the_unread_count_ignores_the_filter()
    {
        await using var host = await StartAsync();

        var first = "First " + Guid.NewGuid().ToString("N")[..8];
        var second = "Second " + Guid.NewGuid().ToString("N")[..8];

        var firstId = await SubmitAsync(host, first);
        var secondId = await SubmitAsync(host, second);

        using var staff = await host.SignInAsync(Permissions.Customers.View);

        var all = await GetJsonAsync(staff, InboxUrl);
        var ids = all.GetProperty("items").EnumerateArray()
            .Select(item => item.GetProperty("id").GetInt32())
            .ToList();

        Assert.True(
            ids.IndexOf(secondId) < ids.IndexOf(firstId),
            "The inbox is not newest first.");

        var unread = all.GetProperty("newCount").GetInt32();

        var filtered = await GetJsonAsync(staff, $"{InboxUrl}?status=Spam");
        Assert.Equal(unread, filtered.GetProperty("newCount").GetInt32());
    }

    /// <summary>Doc 11 P15 step 7: reading the enquiry moves it to Read, and records who.</summary>
    [Fact]
    public async Task Marking_read_then_replied_records_who_and_when()
    {
        await using var host = await StartAsync();
        var id = await SubmitAsync(host, "Flow " + Guid.NewGuid().ToString("N")[..8]);

        using var staff = await host.SignInAsync(Permissions.Customers.View, Permissions.Customers.Edit);

        using var read = await PostAsync(staff, $"{InboxUrl}{id}/read", null);
        Assert.Equal(HttpStatusCode.NoContent, read.StatusCode);

        var afterRead = await GetJsonAsync(staff, $"{InboxUrl}{id}");
        Assert.Equal("Read", afterRead.GetProperty("status").GetString());
        Assert.False(string.IsNullOrWhiteSpace(afterRead.GetProperty("handledByUserEmail").GetString()));

        using var replied = await PutAsync(staff, $"{InboxUrl}{id}/status", new { status = "Replied" });
        Assert.Equal(HttpStatusCode.NoContent, replied.StatusCode);

        var afterReply = await GetJsonAsync(staff, $"{InboxUrl}{id}");
        Assert.Equal("Replied", afterReply.GetProperty("status").GetString());
        Assert.False(string.IsNullOrWhiteSpace(afterReply.GetProperty("handledAtUtc").GetString()));
    }

    /// <summary>
    /// "Unread it" is not one of doc 11 P15's transitions: the status would then say nobody had looked at
    /// an enquiry somebody had.
    /// </summary>
    [Fact]
    public async Task A_status_cannot_be_set_back_to_New()
    {
        await using var host = await StartAsync();
        var id = await SubmitAsync(host, "Unread " + Guid.NewGuid().ToString("N")[..8]);

        using var staff = await host.SignInAsync(Permissions.Customers.View, Permissions.Customers.Edit);

        using var response = await PutAsync(staff, $"{InboxUrl}{id}/status", new { status = "New" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>
    /// <b>The hard delete really removes the row, and the audit row holds no part of the message.</b>
    /// <para>
    /// Doc 08 §3.7: the case this exists for is a customer having pasted card or bank account details,
    /// which "must be removed rather than merely hidden" — so a soft-delete flag would defeat the whole
    /// point, and an audit row carrying a copy would defeat it twice, because <c>ops.AuditLogs</c> is
    /// kept three years (§8.4).
    /// </para>
    /// </summary>
    [Fact]
    public async Task Deleting_removes_the_row_and_audits_the_reason_without_the_message()
    {
        await using var host = await StartAsync();

        var subject = "Delete " + Guid.NewGuid().ToString("N")[..8];
        const string Secret = "my card is 4111111111111111 and my bank is Sampath";

        var id = await SubmitAsync(host, subject, Secret);

        using var staff = await host.SignInAsync(
            Permissions.Customers.View, Permissions.Customers.Edit, Permissions.Customers.Delete);

        using var response = await PostAsync(
            staff, $"{InboxUrl}{id}/delete", new { reason = "Contained card details" });

        var deleteBody = await response.Content.ReadAsStringAsync(Token);
        Assert.True(
            response.StatusCode == HttpStatusCode.NoContent,
            $"Expected 204, got {(int)response.StatusCode}: {deleteBody}");

        await using var scope = host.Factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // Gone, not hidden. There is no soft-delete flag on this table at all, so this is the whole row.
        Assert.False(await dbContext.ContactSubmissions.AnyAsync(row => row.Id == id, Token));
        Assert.False(
            await dbContext.ContactSubmissions.IgnoreQueryFilters().AnyAsync(row => row.Id == id, Token));

        var entityId = id.ToString(CultureInfo.InvariantCulture);
        var audit = await dbContext.AuditLogs
            .AsNoTracking()
            .Where(entry => entry.EntityType == "ContactSubmission" && entry.EntityId == entityId)
            .OrderByDescending(entry => entry.Id)
            .FirstAsync(Token);

        Assert.Equal(AuditActions.Delete, audit.Action);
        Assert.Contains("Contained card details", audit.AfterJson!, StringComparison.Ordinal);

        var trail = (audit.BeforeJson ?? "") + (audit.AfterJson ?? "");
        Assert.DoesNotContain("4111111111111111", trail, StringComparison.Ordinal);
        Assert.DoesNotContain("Sampath", trail, StringComparison.Ordinal);
        Assert.DoesNotContain("Nimal", trail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_deletion_with_no_reason_is_refused_and_the_row_stays()
    {
        await using var host = await StartAsync();
        var id = await SubmitAsync(host, "NoReason " + Guid.NewGuid().ToString("N")[..8]);

        using var staff = await host.SignInAsync(
            Permissions.Customers.View, Permissions.Customers.Delete);

        using var response = await PostAsync(staff, $"{InboxUrl}{id}/delete", new { reason = "" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        await using var scope = host.Factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.True(await dbContext.ContactSubmissions.AnyAsync(row => row.Id == id, Token));
    }

    // ---- the schema and the retention it has to allow --------------------------------------------

    /// <summary>
    /// Doc 08 §3.7's own index, and the one §8.4's retention needs.
    /// <para>
    /// <b>The retention job is not built</b> — there is no nightly job host for it yet — so what is
    /// asserted here is that it <i>can</i> be: the inbox's own index leads on <c>Status</c>, which a
    /// purge by date cannot seek on, so a second index leads on <c>CreatedAtUtc</c>. Adding an index to a
    /// large table later is the expensive half of that job.
    /// </para>
    /// </summary>
    [Fact]
    public async Task The_indexes_doc_08_names_and_the_one_the_purge_needs_both_exist()
    {
        await using var host = await StartAsync();

        var names = await IndexNamesAsync(host, "content", "ContactSubmissions");

        Assert.Contains("IX_ContactSubmissions_Status_Created", names);
        Assert.Contains("IX_ContactSubmissions_CreatedAtUtc", names);
    }

    /// <summary>
    /// Doc 08 §8.4: "ContactSubmissions 2 years; IPs 90 days". Both steps are set-based statements, and
    /// this proves each one runs against the real table — the IP step <b>without</b> touching the enquiry,
    /// which is why <c>IpAddress</c> is nullable rather than carrying a sentinel.
    /// </summary>
    [Fact]
    public async Task The_retention_of_section_8_4_is_two_set_based_statements()
    {
        await using var host = await StartAsync();

        var subject = "Retention " + Guid.NewGuid().ToString("N")[..8];
        var id = await SubmitAsync(host, subject);

        await using var scope = host.Factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // An address is written in first, because the in-memory TestServer gives the request no peer to
        // record (see the note in `An_enquiry_lands_as_new_and_the_answer_promises_no_email`). The step
        // being proved is the UPDATE, so the row has to have something for it to clear.
        await dbContext.ContactSubmissions
            .Where(row => row.Id == id)
            .ExecuteUpdateAsync(
                row => row.SetProperty(submission => submission.IpAddress, "203.0.113.5"),
                Token);

        // Step one, the ninety-day IP clear. One UPDATE, and the enquiry survives it intact.
        var cleared = await dbContext.ContactSubmissions
            .Where(row => row.Id == id && row.IpAddress != null)
            .ExecuteUpdateAsync(row => row.SetProperty(submission => submission.IpAddress, (string?)null), Token);

        Assert.Equal(1, cleared);

        var afterClear = await dbContext.ContactSubmissions.AsNoTracking()
            .SingleAsync(row => row.Id == id, Token);
        Assert.Null(afterClear.IpAddress);
        Assert.Equal(subject, afterClear.Subject);

        // Step two, the two-year delete. One DELETE, keyed on a column that leads an index.
        var removed = await dbContext.ContactSubmissions
            .Where(row => row.Id == id)
            .ExecuteDeleteAsync(Token);

        Assert.Equal(1, removed);
    }

    /// <summary>
    /// <c>CK_ContactSubmissions_Status</c>: the four statuses doc 08 §3.7 lists, and nothing else, even
    /// from a script that bypasses the API.
    /// </summary>
    [Fact]
    public async Task The_status_check_constraint_refuses_a_value_outside_the_four()
    {
        await using var host = await StartAsync();
        var id = await SubmitAsync(host, "CK " + Guid.NewGuid().ToString("N")[..8]);

        await using var scope = host.Factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var exception = await Assert.ThrowsAsync<SqlException>(async () =>
            await dbContext.Database.ExecuteSqlRawAsync(
                "UPDATE content.ContactSubmissions SET Status = 9 WHERE Id = {0}",
                [id],
                Token));

        Assert.Contains("CK_ContactSubmissions_Status", exception.Message, StringComparison.Ordinal);
    }

    // ---- helpers --------------------------------------------------------------------------------

    private static object Enquiry(string subject, string? website = null, string? message = null) => new
    {
        name = "Nimal Perera",
        email = "nimal@example.com",
        subject,
        message = message ?? "Is the linen shirt true to size?",
        website,
    };

    private async Task<MediaTestHost> StartAsync()
    {
        sqlServer.SkipIfUnavailable();
        await TestDatabaseMigrator.MigrateAsync(sqlServer.ConnectionString, Token);

        return MediaTestHost.Create(sqlServer.ConnectionString);
    }

    /// <summary>Submits one enquiry through the public form and answers with its row id.</summary>
    private static async Task<int> SubmitAsync(MediaTestHost host, string subject, string? message = null)
    {
        using var client = host.CreateAnonymousClient();

        using var response = await client.PostAsJsonAsync(
            new Uri(SubmitUrl, UriKind.Relative),
            Enquiry(subject, message: message),
            JsonOptions,
            Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await using var scope = host.Factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        return await dbContext.ContactSubmissions
            .AsNoTracking()
            .Where(row => row.Subject == subject)
            .Select(row => row.Id)
            .SingleAsync(Token);
    }

    private static async Task<List<string>> IndexNamesAsync(MediaTestHost host, string schema, string table)
    {
        await using var scope = host.Factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var connection = dbContext.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync(Token);
        }

        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT i.name FROM sys.indexes i JOIN sys.tables t ON t.object_id = i.object_id "
            + "JOIN sys.schemas s ON s.schema_id = t.schema_id "
            + "WHERE s.name = @schema AND t.name = @table AND i.name IS NOT NULL";

        var schemaParameter = command.CreateParameter();
        schemaParameter.ParameterName = "@schema";
        schemaParameter.Value = schema;
        command.Parameters.Add(schemaParameter);

        var tableParameter = command.CreateParameter();
        tableParameter.ParameterName = "@table";
        tableParameter.Value = table;
        command.Parameters.Add(tableParameter);

        var names = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(Token);
        while (await reader.ReadAsync(Token))
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }

    private static async Task<JsonElement> GetJsonAsync(HttpClient client, string path)
    {
        using var response = await client.GetAsync(new Uri(path, UriKind.Relative), Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions, Token);
    }

    private static async Task<HttpResponseMessage> PutAsync(HttpClient client, string path, object body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, new Uri(path, UriKind.Relative))
        {
            Content = JsonContent.Create(body, options: JsonOptions),
        };
        request.Headers.Add("X-XSRF-TOKEN", await AntiforgeryAsync(client));

        return await client.SendAsync(request, Token);
    }

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, string path, object? body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(path, UriKind.Relative));
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: JsonOptions);
        }

        request.Headers.Add("X-XSRF-TOKEN", await AntiforgeryAsync(client));

        return await client.SendAsync(request, Token);
    }

    private static async Task<string> AntiforgeryAsync(HttpClient client)
    {
        using var response = await client.GetAsync(
            new Uri("/api/admin/auth/antiforgery-token", UriKind.Relative), Token);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions, Token);

        return body.GetProperty("token").GetString()!;
    }
}
