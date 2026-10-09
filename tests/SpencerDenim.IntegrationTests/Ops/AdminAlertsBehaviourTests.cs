using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SpencerDenim.Application.Abstractions.Auditing;
using SpencerDenim.Application.Ops.Alerts.Abstractions;
using SpencerDenim.Application.Ops.Email.Abstractions;
using SpencerDenim.Domain.Auditing;
using SpencerDenim.Domain.Ops;
using SpencerDenim.Infrastructure.Persistence;
using SpencerDenim.IntegrationTests.Infrastructure;
using SpencerDenim.IntegrationTests.Sales;

namespace SpencerDenim.IntegrationTests.Ops;

/// <summary>
/// The three things <c>ops.AdminAlerts</c> does, against a real SQL Server: raise an alert for a dead
/// email (doc 11 §4, §7 G-37), read the banner (doc 08 §3.8 R10), and acknowledge one with its audit row.
/// <para>
/// <b>The rows here are inserted by hand or raised by the real raiser, never mocked.</b> What is under
/// test is the statements: the conditional acknowledge, the idempotence of the raiser, and the one thing
/// no unit test can see — that the alert and its <c>AlertAcknowledged</c> row commit together or not at
/// all.
/// </para>
/// </summary>
public sealed class AdminAlertsBehaviourTests(SqlServerDatabaseFixture sqlServer)
    : SalesSchemaTestBase(sqlServer)
{
    /// <summary>
    /// The telephone number doc 08 §3.1 step 8 searches every table for. A constant because two places
    /// need the same value: the order it is written onto, and the search that must be able to find it.
    /// </summary>
    private const string CustomerPhone = "0771234567";

    private static readonly DateTime Now = new(2026, 10, 9, 12, 0, 0, DateTimeKind.Utc);

    // ------------------------------------------------------- the dead-email alert

    /// <summary>
    /// <b>A dead order email raises a banner that names G-37's three facts, and carries none of the
    /// customer's.</b>
    /// </summary>
    /// <remarks>
    /// The order is a real <c>sales.Orders</c> row, so the order number in the alert is one the raiser
    /// resolved from the public id on the outbox row — the one step a unit test cannot exercise, because
    /// the outbox stores a <c>PublicId</c> and doc 11 §4 requires the alert to name a number.
    /// </remarks>
    [Fact]
    public async Task A_dead_order_email_raises_an_alert_naming_the_template_the_order_and_the_row()
    {
        var reference = await ReferenceAsync();
        var orderId = await InsertValidOrderAsync(reference);

        var order = await DbContext.Orders.AsNoTracking().SingleAsync(row => row.Id == orderId, Token);

        var messageId = await InsertDeadMessageAsync(
            EmailTemplateCodes.BankTransferInstructions,
            order.PublicId);

        Assert.True(await RaiseAsync(messageId));

        var alert = await AlertForAsync(messageId);

        Assert.Equal(AdminAlertTypes.EmailDeliveryFailed, alert.AlertType);
        Assert.Equal(AdminAlertSeverity.Warning, alert.Severity);
        Assert.Contains(EmailTemplateCodes.BankTransferInstructions, alert.Title, StringComparison.Ordinal);
        Assert.Contains(order.OrderNumber, alert.Title, StringComparison.Ordinal);
        Assert.Contains(messageId.ToString(System.Globalization.CultureInfo.InvariantCulture), alert.Title, StringComparison.Ordinal);
        Assert.Contains(order.OrderNumber, alert.DetailsJson!, StringComparison.Ordinal);
        Assert.Contains(order.PublicId.ToString("D"), alert.DetailsJson!, StringComparison.Ordinal);

        // Doc 08 §3.8's "source change (no FK)" column: ops.EmailOutbox is never audited, so there is no
        // audit row to point at.
        Assert.Null(alert.AuditLogId);
        Assert.Null(alert.AcknowledgedAtUtc);
    }

    /// <summary>
    /// <b>The PDPA acceptance test, applied to this one table.</b>
    /// </summary>
    /// <remarks>
    /// Doc 08 §3.1 step 8 searches <i>every</i> table — <c>ops.AdminAlerts</c> named in it — for a
    /// customer's original email, telephone number and name, and doc 11 §4 records that nothing
    /// anonymises these rows. So this is the whole of the reason G-37 restricts the payload to three
    /// facts, and it is asserted the way §3.1 step 8 asserts it: by searching the stored columns for the
    /// values the outbox row beside them really holds.
    /// </remarks>
    [Fact]
    public async Task No_column_of_the_alert_holds_the_customers_email_name_or_telephone_number()
    {
        var reference = await ReferenceAsync();
        var orderId = await InsertValidOrderAsync(reference);

        // **The order needs a contact telephone number, and the shared writer does not give it one.**
        // `SalesRowWriter` sets `Ship_Phone` and leaves `Orders.Phone` null, so the value this test used
        // to interpolate into its search was null — which is half of why it never checked a telephone
        // number (see PersonalDataHitsAsync for the other half).
        await DbContext.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE sales.Orders SET Phone = {CustomerPhone} WHERE Id = {orderId};",
            Token);

        var order = await DbContext.Orders.AsNoTracking().SingleAsync(row => row.Id == orderId, Token);
        Assert.Equal(CustomerPhone, order.Phone);

        var messageId = await InsertDeadMessageAsync(
            EmailTemplateCodes.OrderPlaced,
            order.PublicId,
            toEmail: "nimali.perera@example.com",
            toName: "Nimali Perera");

        Assert.True(await RaiseAsync(messageId));

        // The values really are on the outbox row, so the search below is not vacuous.
        var message = await DbContext.EmailOutbox.AsNoTracking()
            .SingleAsync(row => row.Id == messageId, Token);
        Assert.Equal("nimali.perera@example.com", message.ToEmail);
        Assert.Equal("Nimali Perera", message.ToName);

        Assert.Equal(0, await PersonalDataHitsAsync(order.Phone));

        // **A positive control, because the search above is a negative and a negative can rot.** The
        // telephone clause used to be `LIKE '%{order.Phone}%'`, and `SqlQuery` substitutes its parameter
        // *inside* the string literal: the SQL that reached the server was `LIKE '%@p0%'`, a search for
        // the literal text "@p0", so this test asserted nothing at all about the one value doc 08 §3.1
        // step 8 names that no other clause covers — the email is caught by '%@%' and the name by
        // '%nimali%', and the number by nothing. Planting it in a title proves the search can see it.
        var planted = await InsertAlertAsync(
            AdminAlertTypes.EmailDeliveryFailed,
            Now.AddMinutes(-1),
            title: $"A probe that should never ship: rang {order.Phone}");

        Assert.Equal(1, await PersonalDataHitsAsync(order.Phone));

        await DbContext.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM ops.AdminAlerts WHERE Id = {planted};",
            Token);
    }

    /// <summary>
    /// Doc 08 §3.1 step 8's search, over the columns <c>ops.AdminAlerts</c> has: how many rows hold the
    /// customer's email address, name or telephone number.
    /// </summary>
    /// <remarks>
    /// <b>The pattern is one parameter, not a parameter inside a literal.</b> <c>SqlQuery</c> renders
    /// <c>LIKE '%{value}%'</c> as <c>LIKE '%@p0%'</c> — the quotes swallow the hole and the predicate
    /// becomes a search for the text "@p0". The wildcards therefore go into the value.
    /// </remarks>
    private async Task<int> PersonalDataHitsAsync(string? phone)
    {
        // **A blank number would make the pattern '%%', which matches every row.** That is the shape that
        // would turn this search from "nothing leaked" into "everything leaked" or, with the clause
        // written the old way, into nothing at all — so the value is asserted rather than trusted.
        Assert.False(string.IsNullOrWhiteSpace(phone), "the search needs a telephone number to look for.");

        var phonePattern = $"%{phone}%";

        return await DbContext.Database
            .SqlQuery<int>($"""
                SELECT COUNT(*) AS [Value]
                  FROM ops.AdminAlerts
                 WHERE Title LIKE '%nimali%'
                    OR Title LIKE '%Perera%'
                    OR Title LIKE '%@%'
                    OR Title LIKE {phonePattern}
                    OR DetailsJson LIKE '%nimali%'
                    OR DetailsJson LIKE '%Perera%'
                    OR DetailsJson LIKE '%@%'
                    OR DetailsJson LIKE {phonePattern}
                    OR RelatedEntityId LIKE '%nimali%'
                    OR RelatedEntityId LIKE {phonePattern}
                    OR ISNULL(AcknowledgementNote, '') LIKE '%nimali%'
                    OR ISNULL(AcknowledgementNote, '') LIKE {phonePattern}
                """)
            .SingleAsync(Token);
    }

    /// <summary>
    /// A dead account email — no order — still raises an alert, and says there is nothing to open.
    /// </summary>
    [Fact]
    public async Task A_dead_message_with_no_order_still_raises_an_alert()
    {
        var messageId = await InsertDeadMessageAsync("PasswordReset", orderPublicId: null);

        Assert.True(await RaiseAsync(messageId));

        var alert = await AlertForAsync(messageId);

        Assert.Contains("PasswordReset", alert.Title, StringComparison.Ordinal);
        Assert.Contains("not about an order", alert.Title, StringComparison.Ordinal);
        Assert.DoesNotContain("orderNumber", alert.DetailsJson!, StringComparison.Ordinal);
    }

    /// <summary>
    /// An outbox row naming an order that no longer exists still raises an alert.
    /// </summary>
    /// <remarks>
    /// Reachable because doc 08 §3.8 gives the outbox no foreign key to <c>sales.Orders</c> on purpose —
    /// "nothing in the database stops a row naming an order that does not exist". Withholding the alert
    /// would restore the silence G-37 exists to end, so the title says which fact is missing instead.
    /// </remarks>
    [Fact]
    public async Task A_dead_message_naming_an_order_that_is_gone_still_raises_an_alert()
    {
        var messageId = await InsertDeadMessageAsync(EmailTemplateCodes.OrderPlaced, Guid.NewGuid());

        Assert.True(await RaiseAsync(messageId));

        var alert = await AlertForAsync(messageId);

        Assert.Contains("can no longer be found", alert.Title, StringComparison.Ordinal);
        Assert.DoesNotContain("orderNumber", alert.DetailsJson!, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>A row that is not <c>Dead</c> raises nothing</b>, whatever the caller asks for.
    /// </summary>
    /// <remarks>
    /// A banner telling a Super Admin that a message failed while the ladder is still retrying it is
    /// worse than no banner: they would telephone a customer who is about to get the email anyway. The
    /// raiser reads the status back rather than trusting its caller, which is what makes the port
    /// impossible to misuse.
    /// </remarks>
    [Theory]
    [InlineData(EmailOutboxStatus.Pending)]
    [InlineData(EmailOutboxStatus.Sending)]
    [InlineData(EmailOutboxStatus.Sent)]
    [InlineData(EmailOutboxStatus.Failed)]
    [InlineData(EmailOutboxStatus.Cancelled)]
    public async Task A_message_that_is_not_dead_raises_nothing(EmailOutboxStatus status)
    {
        var messageId = await InsertDeadMessageAsync(
            EmailTemplateCodes.OrderPlaced,
            orderPublicId: null,
            status: status);

        Assert.False(await RaiseAsync(messageId));

        Assert.Equal(0, await CountAlertsForAsync(messageId));
    }

    [Fact]
    public async Task A_message_that_does_not_exist_raises_nothing()
    {
        SqlServer.SkipIfUnavailable();

        Assert.False(await RaiseAsync(long.MaxValue));
    }

    /// <summary>
    /// Raising twice for the same message writes one alert, acknowledged or not.
    /// </summary>
    /// <remarks>
    /// <b>Not load-bearing today, and that is said plainly:</b> both transitions to <c>Dead</c> are
    /// conditional <c>UPDATE</c>s with exactly one winner, so nothing calls this twice. It is what makes
    /// the port safe to call again — the difference between a sweep that can be retried and one that
    /// cannot — and the acknowledged case matters more than the open one: a Super Admin who has already
    /// dealt with a message must not get the same banner back.
    /// </remarks>
    [Fact]
    public async Task Raising_the_same_message_twice_writes_one_alert()
    {
        var messageId = await InsertDeadMessageAsync(EmailTemplateCodes.OrderPlaced, orderPublicId: null);

        Assert.True(await RaiseAsync(messageId));
        Assert.False(await RaiseAsync(messageId));

        Assert.Equal(1, await CountAlertsForAsync(messageId));

        var alert = await AlertForAsync(messageId);
        await AcknowledgeAsync(alert.Id);

        Assert.False(await RaiseAsync(messageId));
        Assert.Equal(1, await CountAlertsForAsync(messageId));
    }

    /// <summary>
    /// Two messages for the same order get an alert each.
    /// </summary>
    /// <remarks>
    /// <b>Why the alert is keyed on the outbox row and not on the order.</b> An order queues two messages
    /// at placement — "we have your order" and "how to pay" — and an SMTP outage kills both; a Super
    /// Admin has to know that the bank details did not arrive <i>as well as</i> the confirmation, because
    /// those are two different things to tell the customer.
    /// </remarks>
    [Fact]
    public async Task Two_dead_messages_for_one_order_raise_two_alerts()
    {
        var reference = await ReferenceAsync();
        var orderId = await InsertValidOrderAsync(reference);
        var order = await DbContext.Orders.AsNoTracking().SingleAsync(row => row.Id == orderId, Token);

        var placed = await InsertDeadMessageAsync(EmailTemplateCodes.OrderPlaced, order.PublicId);
        var instructions = await InsertDeadMessageAsync(
            EmailTemplateCodes.BankTransferInstructions,
            order.PublicId);

        Assert.True(await RaiseAsync(placed));
        Assert.True(await RaiseAsync(instructions));

        Assert.Equal(1, await CountAlertsForAsync(placed));
        Assert.Equal(1, await CountAlertsForAsync(instructions));
    }

    // ------------------------------------------------------------- the read path

    /// <summary>
    /// The banner reads only open alerts, newest first, and counts all of them.
    /// </summary>
    [Fact]
    public async Task The_banner_reads_open_alerts_newest_first()
    {
        await ResetAlertsAsync();

        var older = await InsertAlertAsync(AdminAlertTypes.EmailDeliveryFailed, Now.AddHours(-2));
        var newer = await InsertAlertAsync(AdminAlertTypes.EmailDeliveryFailed, Now.AddHours(-1));
        var acknowledged = await InsertAlertAsync(
            AdminAlertTypes.EmailDeliveryFailed,
            Now,
            acknowledged: true);

        using var scope = SqlServer.CreateScope();
        var reader = scope.ServiceProvider.GetRequiredService<IAdminAlertReader>();

        var (moreThanTake, rows) = await reader.GetOpenAsync(AdminAlertTypes.All, 10, Token);

        Assert.False(moreThanTake);
        Assert.Equal([newer, older], rows.Select(row => row.Id));
        Assert.DoesNotContain(acknowledged, rows.Select(row => row.Id));
    }

    /// <summary>
    /// A reader told two types returns only those two, and counts only those two.
    /// </summary>
    /// <remarks>
    /// This is doc 08 §3.8 R10 reaching the database. The count is asserted as well as the list, because
    /// a count that ignored the type filter would tell an order staff member how many security alerts the
    /// shop has had.
    /// </remarks>
    [Fact]
    public async Task A_reader_told_two_types_sees_only_those_two()
    {
        await ResetAlertsAsync();

        await InsertAlertAsync(AdminAlertTypes.EmailDeliveryFailed, Now);
        await InsertAlertAsync(AdminAlertTypes.BreakGlassUsed, Now);
        var payment = await InsertAlertAsync(AdminAlertTypes.PaymentException, Now);

        using var scope = SqlServer.CreateScope();
        var reader = scope.ServiceProvider.GetRequiredService<IAdminAlertReader>();

        var (moreThanTake, rows) = await reader.GetOpenAsync(
            AdminAlertVisibility.OrderStaffTypes,
            10,
            Token);

        Assert.False(moreThanTake);
        Assert.Equal([payment], rows.Select(row => row.Id));
    }

    /// <summary>
    /// An empty visible list means nothing, never no filter.
    /// </summary>
    /// <remarks>
    /// The one shape of this query that must not exist. A reader handed an empty list by accident is a
    /// reader one wrong <c>if</c> away from returning every alert in the shop to a staff member R10 gives
    /// no path to.
    /// </remarks>
    [Fact]
    public async Task An_empty_visible_list_returns_nothing()
    {
        await ResetAlertsAsync();
        await InsertAlertAsync(AdminAlertTypes.EmailDeliveryFailed, Now);

        // **Counted, not just asserted empty.** EF renders an empty `EF.Constant` collection as a
        // predicate that matches nothing, so dropping the guard leaves the *answer* right and the
        // statement run anyway — a mutation proved it. The guard is what makes "no audience" an answer
        // the database is never asked for, which is the property R10 needs: a reader that runs a
        // statement for a caller with no audience is a reader one wrong predicate away from returning
        // every alert in the shop.
        var counter = new CommandCountingInterceptor();

        var services = new ServiceCollection();
        services.AddDbContext<AppDbContext>(builder => builder
            .UseSqlServer(SqlServer.ConnectionString)
            .AddInterceptors(counter));

        await using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var reader = new SpencerDenim.Infrastructure.Ops.Alerts.AdminAlertReader(
            scope.ServiceProvider.GetRequiredService<AppDbContext>());

        counter.Reset();

        var (moreThanTake, rows) = await reader.GetOpenAsync([], 10, Token);

        Assert.False(moreThanTake);
        Assert.Empty(rows);
        Assert.Equal(0, counter.Count);
    }

    /// <summary>
    /// The cap bounds the list, and the extra row the reader fetches is not returned with it.
    /// </summary>
    /// <remarks>
    /// <b>The boundary is the whole point of the one-extra-row trick.</b> Exactly as many open alerts as
    /// the cap must report "no more" — otherwise a banner showing all ten would tell a Super Admin to go
    /// looking for an eleventh that does not exist — and the cap must never leak the probe row onto the
    /// screen.
    /// </remarks>
    [Theory]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(3, true)]
    [InlineData(5, true)]
    public async Task The_cap_bounds_the_list_and_the_extra_row_only_answers_whether_there_are_more(
        int openAlerts,
        bool expectedMore)
    {
        await ResetAlertsAsync();

        for (var index = 0; index < openAlerts; index++)
        {
            await InsertAlertAsync(AdminAlertTypes.EmailDeliveryFailed, Now.AddMinutes(-index));
        }

        using var scope = SqlServer.CreateScope();
        var reader = scope.ServiceProvider.GetRequiredService<IAdminAlertReader>();

        var (moreThanTake, rows) = await reader.GetOpenAsync(AdminAlertTypes.All, 2, Token);

        Assert.Equal(expectedMore, moreThanTake);
        Assert.Equal(Math.Min(openAlerts, 2), rows.Count);
    }

    /// <summary>
    /// <b>The banner's read is one statement.</b>
    /// </summary>
    /// <remarks>
    /// Doc 08 §1.6 DB28's lesson, as a count of commands rather than as a plan: a <c>COUNT(*)</c> beside
    /// the list could not use <c>IX_AdminAlerts_Open</c> and cost 5,020 logical reads against the list's
    /// 38 on 20,000 alerts (<c>AdminAlertsPlanTests</c> holds that measurement). Adding one back is the
    /// obvious readable change — the payload would carry an exact total instead of a flag — so the
    /// absence of a second statement is asserted rather than left to a remark.
    /// </remarks>
    [Fact]
    public async Task The_banners_read_runs_one_statement()
    {
        await ResetAlertsAsync();
        await InsertAlertAsync(AdminAlertTypes.EmailDeliveryFailed, Now);

        var counter = new CommandCountingInterceptor();

        var services = new ServiceCollection();
        services.AddDbContext<AppDbContext>(builder => builder
            .UseSqlServer(SqlServer.ConnectionString)
            .AddInterceptors(counter));

        await using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var reader = new SpencerDenim.Infrastructure.Ops.Alerts.AdminAlertReader(
            scope.ServiceProvider.GetRequiredService<AppDbContext>());

        counter.Reset();

        var (_, rows) = await reader.GetOpenAsync(AdminAlertTypes.All, 10, Token);

        Assert.Single(rows);
        Assert.Equal(1, counter.Count);
    }

    /// <summary>
    /// Alerts raised in the same millisecond come back in a stable order.
    /// </summary>
    /// <remarks>
    /// <c>CreatedAtUtc</c> is <c>datetime2(3)</c> and one sweep can kill twenty messages inside one
    /// millisecond, so a sort on the timestamp alone returns them in whatever order the engine finds them
    /// — which makes "newest first" something no test could assert. The id is the tie-break;
    /// <c>OrderExpiryStore.FindDueOrdersAsync</c> and <c>EmailOutboxStore.ClaimAsync</c> learned the same
    /// thing about the same column type.
    /// <para>
    /// <b>It ascends inside a descending sort</b>, which is <c>IX_AdminAlerts_Open</c>'s own physical
    /// order — see <c>AdminAlertReader</c>, where asking for the other direction cost the index.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Alerts_raised_in_the_same_millisecond_are_ordered_by_id()
    {
        await ResetAlertsAsync();

        var first = await InsertAlertAsync(AdminAlertTypes.EmailDeliveryFailed, Now);
        var second = await InsertAlertAsync(AdminAlertTypes.EmailDeliveryFailed, Now);
        var third = await InsertAlertAsync(AdminAlertTypes.EmailDeliveryFailed, Now);

        using var scope = SqlServer.CreateScope();
        var reader = scope.ServiceProvider.GetRequiredService<IAdminAlertReader>();

        var (_, rows) = await reader.GetOpenAsync(AdminAlertTypes.All, 10, Token);

        Assert.Equal([first, second, third], rows.Select(row => row.Id));
    }

    // ----------------------------------------------------------- the acknowledge

    /// <summary>
    /// <b>Acknowledging stamps the row and writes its audit trail in the same transaction.</b>
    /// </summary>
    /// <remarks>
    /// Doc 08 §3.8: "Acknowledging is audited and doesn't resolve the underlying issue." A banner that
    /// disappeared with no <c>AlertAcknowledged</c> row behind it would be a silencing with nobody's name
    /// on it — which doc 11 §6.1's escalation rules name as the thing that must not happen.
    /// </remarks>
    [Fact]
    public async Task Acknowledging_stamps_the_row_and_writes_the_audit_trail()
    {
        await ResetAlertsAsync();

        var alertId = await InsertAlertAsync(AdminAlertTypes.EmailDeliveryFailed, Now);
        var userId = await StaffUserIdAsync(DbContext, Token);

        // **The note arrives here already trimmed.** Trimming, and refusing one longer than the
        // column, is `AcknowledgeAdminAlertCommandHandler`'s — asserted in `AdminAlertHandlerTests` —
        // and this suite drives the port under it, so a note with spaces round-tripped here would be
        // testing the wrong layer's rule.
        var (outcome, acknowledged) = await AcknowledgeAsync(
            alertId,
            note: "Rang the customer on 0771234567",
            userId: userId);

        Assert.Equal(AcknowledgeAlertOutcome.Acknowledged, outcome);
        Assert.Equal(alertId, acknowledged!.Id);

        var row = await DbContext.AdminAlerts.AsNoTracking().SingleAsync(alert => alert.Id == alertId, Token);
        Assert.NotNull(row.AcknowledgedAtUtc);
        Assert.Equal(userId, row.AcknowledgedByUserId);
        Assert.Equal("Rang the customer on 0771234567", row.AcknowledgementNote);

        var trail = await DbContext.AuditLogs.AsNoTracking()
            .Where(entry => entry.Action == AuditActions.AlertAcknowledged
                && entry.EntityId == alertId.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .ToListAsync(Token);

        var entry = Assert.Single(trail);
        Assert.Equal("AdminAlert", entry.EntityType);
        Assert.Equal(userId, entry.UserId);
        Assert.Equal("127.0.0.1", entry.IpAddress);
        Assert.Contains(AdminAlertTypes.EmailDeliveryFailed, entry.AfterJson!, StringComparison.Ordinal);
        Assert.Contains("\"HasNote\":true", entry.AfterJson!, StringComparison.Ordinal);

        // **The note is not copied into the audit row.** It is free text a staff member typed, the audit
        // log is not scrubbed by the PDPA routine either (doc 08 §3.1 step 8), and the note is already on
        // the row this entry points at — so a second copy adds a place for a pasted telephone number to
        // survive, for no reader's benefit.
        Assert.DoesNotContain("0771234567", entry.AfterJson!, StringComparison.Ordinal);
        Assert.Null(entry.BeforeJson);
    }

    /// <summary>
    /// Acknowledging twice records one acknowledgement and <b>one</b> audit row, and the second caller is
    /// told when the first one cleared it.
    /// </summary>
    /// <remarks>
    /// Doc 08 §3.8 shows the banner "until <i>one of them</i> acknowledges it", so the second Super Admin
    /// has the outcome they wanted — a refusal would be a screen telling somebody off for agreeing. What
    /// must not happen is two audit rows, because the trail is read to find out who saw a problem.
    /// </remarks>
    [Fact]
    public async Task A_second_acknowledgement_changes_nothing_and_writes_no_second_audit_row()
    {
        await ResetAlertsAsync();

        var alertId = await InsertAlertAsync(AdminAlertTypes.EmailDeliveryFailed, Now);

        var (first, firstAlert) = await AcknowledgeAsync(alertId, note: "I saw it");
        Assert.Equal(AcknowledgeAlertOutcome.Acknowledged, first);

        var (second, secondAlert) = await AcknowledgeAsync(alertId, note: "I saw it too");

        Assert.Equal(AcknowledgeAlertOutcome.AlreadyAcknowledged, second);

        var row = await DbContext.AdminAlerts.AsNoTracking().SingleAsync(alert => alert.Id == alertId, Token);
        Assert.Equal("I saw it", row.AcknowledgementNote);

        // **The second caller is told the time the row really holds**, which is what the screen shows.
        // Compared against the stored value rather than against the winner's own answer: the column is
        // `datetime2(3)` (doc 08 §3.8) and `IClock` gives a hundred-nanosecond tick, so the winner's
        // in-memory instant has four digits the database never kept. The fact that matters is that the
        // loser is told when it was cleared, not that two C# values are bit-identical.
        Assert.Equal(row.AcknowledgedAtUtc, secondAlert!.AcknowledgedAtUtc);
        Assert.True(firstAlert!.AcknowledgedAtUtc - secondAlert.AcknowledgedAtUtc < TimeSpan.FromSeconds(1));

        var trail = await DbContext.AuditLogs.AsNoTracking()
            .CountAsync(
                entry => entry.Action == AuditActions.AlertAcknowledged
                    && entry.EntityId == alertId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Token);

        Assert.Equal(1, trail);
    }

    /// <summary>
    /// An alert of a type the caller may not see cannot be acknowledged, and leaves no audit row.
    /// </summary>
    /// <remarks>
    /// R10 goes into the statement rather than being checked before it, and this is the assertion that
    /// matters: an order staff member cannot clear the banner that tells a Super Admin a bank setting was
    /// changed. It answers the same way as a wrong id, so the route cannot be used to count them either.
    /// </remarks>
    [Fact]
    public async Task An_alert_of_a_type_the_caller_may_not_see_cannot_be_acknowledged()
    {
        await ResetAlertsAsync();

        var alertId = await InsertAlertAsync(AdminAlertTypes.SensitiveSettingsChanged, Now);

        var (outcome, _) = await AcknowledgeAsync(
            alertId,
            visibleTypes: AdminAlertVisibility.OrderStaffTypes);

        Assert.Equal(AcknowledgeAlertOutcome.NotFound, outcome);

        var row = await DbContext.AdminAlerts.AsNoTracking().SingleAsync(alert => alert.Id == alertId, Token);
        Assert.Null(row.AcknowledgedAtUtc);

        Assert.Equal(
            0,
            await DbContext.AuditLogs.AsNoTracking().CountAsync(
                entry => entry.Action == AuditActions.AlertAcknowledged
                    && entry.EntityId == alertId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Token));
    }

    [Fact]
    public async Task An_alert_that_does_not_exist_cannot_be_acknowledged()
    {
        SqlServer.SkipIfUnavailable();

        var (outcome, alert) = await AcknowledgeAsync(int.MaxValue);

        Assert.Equal(AcknowledgeAlertOutcome.NotFound, outcome);
        Assert.Null(alert);
    }

    /// <summary>
    /// An acknowledgement with no identified user writes nothing at all.
    /// </summary>
    [Fact]
    public async Task An_acknowledgement_with_no_actor_writes_nothing()
    {
        await ResetAlertsAsync();

        var alertId = await InsertAlertAsync(AdminAlertTypes.EmailDeliveryFailed, Now);

        using var scope = SqlServer.CreateScope();
        var acknowledger = scope.ServiceProvider.GetRequiredService<IAdminAlertAcknowledger>();

        var (outcome, _) = await acknowledger.AcknowledgeAsync(
            alertId,
            AdminAlertTypes.All,
            "nobody",
            AdminActor.Unknown,
            Token);

        Assert.Equal(AcknowledgeAlertOutcome.NotFound, outcome);

        var row = await DbContext.AdminAlerts.AsNoTracking().SingleAsync(alert => alert.Id == alertId, Token);
        Assert.Null(row.AcknowledgedAtUtc);
    }

    /// <summary>
    /// <b>Two Super Admins pressing the button at the same moment record one acknowledgement and one
    /// audit row.</b>
    /// </summary>
    /// <remarks>
    /// The acknowledging <c>UPDATE</c> is conditional on the row still being open (doc 08 §5.2), so the
    /// loser's statement matches nothing and its transaction rolls back — audit row and all. Run with
    /// several real contexts against the real engine rather than reasoned about, because "last write
    /// wins" and "one winner" look identical from the C#.
    /// </remarks>
    [Fact]
    public async Task Racing_acknowledgements_produce_one_winner_and_one_audit_row()
    {
        await ResetAlertsAsync();

        var alertId = await InsertAlertAsync(AdminAlertTypes.EmailDeliveryFailed, Now);
        var userId = await StaffUserIdAsync(DbContext, Token);

        var outcomes = await Task.WhenAll(Enumerable.Range(0, 4).Select(async index =>
        {
            using var scope = SqlServer.CreateScope();
            var acknowledger = scope.ServiceProvider.GetRequiredService<IAdminAlertAcknowledger>();

            var (outcome, _) = await acknowledger.AcknowledgeAsync(
                alertId,
                AdminAlertTypes.All,
                $"racer {index}",
                new AdminActor(userId, "racer@example.com", "127.0.0.1", "agent", $"corr-{index}"),
                Token);

            return outcome;
        }));

        Assert.Equal(1, outcomes.Count(outcome => outcome == AcknowledgeAlertOutcome.Acknowledged));
        Assert.Equal(
            3,
            outcomes.Count(outcome => outcome == AcknowledgeAlertOutcome.AlreadyAcknowledged));

        Assert.Equal(
            1,
            await DbContext.AuditLogs.AsNoTracking().CountAsync(
                entry => entry.Action == AuditActions.AlertAcknowledged
                    && entry.EntityId == alertId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Token));
    }

    /// <summary>
    /// <b>Acknowledging an alert changes nothing about the problem.</b>
    /// </summary>
    /// <remarks>
    /// Doc 08 §3.8 and doc 11 §6.1 both say so, and this asserts it where it could be got wrong: the dead
    /// outbox row is untouched — not requeued, not reset, not deleted — so the message is still dead and
    /// the customer still has to be telephoned.
    /// </remarks>
    [Fact]
    public async Task Acknowledging_leaves_the_dead_message_exactly_as_it_was()
    {
        var messageId = await InsertDeadMessageAsync(EmailTemplateCodes.OrderPlaced, orderPublicId: null);
        Assert.True(await RaiseAsync(messageId));

        var before = await DbContext.EmailOutbox.AsNoTracking()
            .SingleAsync(row => row.Id == messageId, Token);

        var alert = await AlertForAsync(messageId);
        var (outcome, _) = await AcknowledgeAsync(alert.Id);
        Assert.Equal(AcknowledgeAlertOutcome.Acknowledged, outcome);

        using var scope = SqlServer.CreateScope();
        var after = await scope.ServiceProvider.GetRequiredService<AppDbContext>().EmailOutbox
            .AsNoTracking()
            .SingleAsync(row => row.Id == messageId, Token);

        Assert.Equal(EmailOutboxStatus.Dead, after.Status);
        Assert.Equal(before.AttemptCount, after.AttemptCount);
        Assert.Equal(before.NextAttemptAtUtc, after.NextAttemptAtUtc);
        Assert.Null(after.SentAtUtc);
    }

    // ---------------------------------------------------------- the enlisting writer

    /// <summary>
    /// <c>IAdminAlertWriter.Enlist</c> commits with the caller's own transaction, and rolls back with it.
    /// </summary>
    /// <remarks>
    /// <b>No producer in this build uses it</b> — the settings task's <c>SensitiveSettingsChanged</c> is
    /// its first caller (doc 08 §3.8 DB2: the alert row goes in with the setting change) — so it is
    /// exercised here rather than left as an untested promise, which is how a port that "is ready" turns
    /// out not to be.
    /// </remarks>
    [Fact]
    public async Task An_enlisted_alert_commits_with_its_callers_transaction_and_rolls_back_with_it()
    {
        await ResetAlertsAsync();

        await InTransactionAsync("rolled back", commit: false);

        Assert.Equal(0, await DbContext.AdminAlerts.AsNoTracking().CountAsync(Token));

        await InTransactionAsync("committed", commit: true);

        var alert = Assert.Single(await DbContext.AdminAlerts.AsNoTracking().ToListAsync(Token));
        Assert.Equal("committed", alert.Title);
    }

    /// <summary>
    /// Enlists one alert inside a transaction and then commits or rolls back.
    /// </summary>
    /// <remarks>
    /// <b>Wrapped in the execution strategy, which is not optional.</b> Doc 08 §10.3 puts
    /// <c>EnableRetryOnFailure</c> on the context, and EF then refuses a user-initiated transaction
    /// outright — "the configured execution strategy does not support user-initiated transactions" — so
    /// this is the shape every caller of <c>Enlist</c> will have to use, and the test has to use it too
    /// or it would be proving something about a configuration production does not have.
    /// </remarks>
    private async Task InTransactionAsync(string title, bool commit)
    {
        using var scope = SqlServer.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var writer = scope.ServiceProvider.GetRequiredService<IAdminAlertWriter>();

        var strategy = dbContext.Database.CreateExecutionStrategy();

        await strategy.ExecuteAsync(async () =>
        {
            dbContext.ChangeTracker.Clear();

            await using var transaction = await dbContext.Database.BeginTransactionAsync(Token);

            writer.Enlist(NewAlert(AdminAlertTypes.SensitiveSettingsChanged, Now, title));
            await dbContext.SaveChangesAsync(Token);

            if (commit)
            {
                await transaction.CommitAsync(Token);
            }
            else
            {
                await transaction.RollbackAsync(Token);
            }
        });
    }

    /// <summary>
    /// A staff user to acknowledge with, created on first use.
    /// </summary>
    /// <remarks>
    /// <b>The SQL fixture seeds the catalogue and not identity</b> — <c>AdminSeedHostedService</c> runs in
    /// the API host, which this suite does not start — so <c>identity.Users</c> is empty here and a test
    /// that read "the first user" got "sequence contains no elements". Inserted with SQL rather than
    /// through <c>UserManager</c>, which this fixture does not register: only the non-nullable columns are
    /// set and the rest take their defaults. <c>internal static</c> so
    /// <see cref="AdminAlertsSchemaTests"/> uses the same one row rather than a second copy of this
    /// reasoning.
    /// </remarks>
    internal static async Task<int> StaffUserIdAsync(AppDbContext dbContext, CancellationToken token)
    {
        const string email = "alerts-probe@example.invalid";

        var existing = await dbContext.Users
            .AsNoTracking()
            .Where(user => user.Email == email)
            .Select(user => user.Id)
            .FirstOrDefaultAsync(token);

        if (existing != 0)
        {
            return existing;
        }

        var ids = await dbContext.Database
            .SqlQuery<int>($"""
                INSERT INTO [identity].[Users]
                    (UserType, FirstName, LastName, UserName, NormalizedUserName, Email, NormalizedEmail,
                     EmailConfirmed, PhoneNumberConfirmed, TwoFactorEnabled, LockoutEnabled,
                     AccessFailedCount, SecurityStamp, ConcurrencyStamp)
                OUTPUT inserted.Id AS [Value]
                VALUES
                    (1, 'Alerts', 'Probe', {email}, {email.ToUpperInvariant()}, {email},
                     {email.ToUpperInvariant()}, 1, 0, 0, 1, 0, {Guid.NewGuid().ToString("N")},
                     {Guid.NewGuid().ToString("N")});
                """)
            .ToListAsync(token);

        return ids.Single();
    }

    // ------------------------------------------------------------------- helpers

    /// <summary>
    /// Empties <c>ops.AdminAlerts</c> so a count or an ordering assertion is exact.
    /// </summary>
    /// <remarks>
    /// <b>Deleting from this table is allowed, unlike the tables around it.</b> Doc 08 §1.6's
    /// never-deleted list is Users, Orders, OrderLines, Payments, Refunds, PaymentEvents,
    /// InventoryMovements and AuditLogs; <c>ops.AdminAlerts</c> is not on it, and §8.4 gives it a
    /// one-year purge, so a set-based delete is a thing the system itself will do. The suite shares one
    /// throwaway database the fixture created and runs sequentially.
    /// </remarks>
    private async Task ResetAlertsAsync()
    {
        SqlServer.SkipIfUnavailable();

        await DbContext.Database.ExecuteSqlRawAsync("DELETE FROM ops.AdminAlerts;", Token);
    }

    private static AdminAlert NewAlert(string alertType, DateTime createdAtUtc, string title) => new()
    {
        AlertType = alertType,
        Severity = AdminAlertSeverity.Warning,
        Title = title,
        DetailsJson = "{\"probe\":true}",
        CreatedAtUtc = createdAtUtc,
    };

    private async Task<int> InsertAlertAsync(
        string alertType,
        DateTime createdAtUtc,
        bool acknowledged = false,
        string? title = null)
    {
        SqlServer.SkipIfUnavailable();

        using var scope = SqlServer.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var alert = NewAlert(alertType, createdAtUtc, title ?? $"A probe raised at {createdAtUtc:O}");

        if (acknowledged)
        {
            alert.AcknowledgedAtUtc = createdAtUtc;
            alert.AcknowledgedByUserId = await StaffUserIdAsync(dbContext, Token);
        }

        dbContext.AdminAlerts.Add(alert);
        await dbContext.SaveChangesAsync(Token);

        return alert.Id;
    }

    /// <summary>
    /// One outbox row in whatever state a test needs, inserted with SQL.
    /// </summary>
    /// <remarks>
    /// Raw SQL rather than EF for <c>EmailOutboxSendingTests</c>' reason: a row already at <c>Dead</c> on
    /// its sixth attempt is a state no application path can write in one step, and going through the
    /// entity would mean this fixture could only produce states the code under test already produces.
    /// </remarks>
    private async Task<long> InsertDeadMessageAsync(
        string templateCode,
        Guid? orderPublicId,
        EmailOutboxStatus status = EmailOutboxStatus.Dead,
        string toEmail = "queue-probe@example.invalid",
        string toName = "Queue Probe")
    {
        SqlServer.SkipIfUnavailable();

        var relatedType = orderPublicId is null ? null : EmailOutboxMessage.OrderEntityType;
        var relatedId = orderPublicId?.ToString("D");

        var ids = await DbContext.Database
            .SqlQuery<long>($"""
                INSERT INTO ops.EmailOutbox
                    (TemplateCode, ToEmail, ToName, Subject, BodyHtml, BodyText, RelatedEntityType,
                     RelatedEntityId, Status, AttemptCount, MaxAttempts, NextAttemptAtUtc, LastError,
                     CreatedAtUtc)
                OUTPUT inserted.Id AS [Value]
                VALUES
                    ({templateCode}, {toEmail}, {toName}, 'A queue probe', '<p>probe</p>', 'probe',
                     {relatedType}, {relatedId}, {(byte)status}, {EmailBackoff.MaxAttempts},
                     {EmailBackoff.MaxAttempts}, {Now}, '550 refused', {Now});
                """)
            .ToListAsync(Token);

        return ids.Single();
    }

    private async Task<bool> RaiseAsync(long outboxMessageId)
    {
        SqlServer.SkipIfUnavailable();

        using var scope = SqlServer.CreateScope();

        return await scope.ServiceProvider.GetRequiredService<IDeadEmailAlerts>()
            .RaiseAsync(outboxMessageId, Token);
    }

    private Task<int> CountAlertsForAsync(long outboxMessageId) =>
        DbContext.AdminAlerts
            .AsNoTracking()
            .CountAsync(
                alert => alert.AlertType == AdminAlertTypes.EmailDeliveryFailed
                    && alert.RelatedEntityType == AdminAlert.EmailOutboxEntityType
                    && alert.RelatedEntityId == outboxMessageId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Token);

    private async Task<AdminAlert> AlertForAsync(long outboxMessageId)
    {
        using var scope = SqlServer.CreateScope();

        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().AdminAlerts
            .AsNoTracking()
            .SingleAsync(
                alert => alert.AlertType == AdminAlertTypes.EmailDeliveryFailed
                    && alert.RelatedEntityType == AdminAlert.EmailOutboxEntityType
                    && alert.RelatedEntityId == outboxMessageId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Token);
    }

    private async Task<(AcknowledgeAlertOutcome Outcome, AcknowledgedAlert? Alert)> AcknowledgeAsync(
        int alertId,
        string? note = null,
        int? userId = null,
        IReadOnlyList<string>? visibleTypes = null)
    {
        SqlServer.SkipIfUnavailable();

        var actorId = userId ?? await StaffUserIdAsync(DbContext, Token);

        using var scope = SqlServer.CreateScope();
        var acknowledger = scope.ServiceProvider.GetRequiredService<IAdminAlertAcknowledger>();

        return await acknowledger.AcknowledgeAsync(
            alertId,
            visibleTypes ?? AdminAlertTypes.All,
            note,
            new AdminActor(actorId, "super@example.com", "127.0.0.1", "agent", "corr"),
            Token);
    }
}
