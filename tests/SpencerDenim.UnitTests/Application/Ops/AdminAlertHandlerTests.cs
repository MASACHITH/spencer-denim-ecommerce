using SpencerDenim.Application.Abstractions.Auditing;
using SpencerDenim.Application.Admin.CurrentUser;
using SpencerDenim.Application.Ops.Alerts;
using SpencerDenim.Application.Ops.Alerts.Abstractions;
using SpencerDenim.Domain.Identity;
using SpencerDenim.Domain.Ops;

namespace SpencerDenim.UnitTests.Application.Ops;

/// <summary>
/// The banner's read and its acknowledge, against fake ports (doc 08 §3.8 N2, R10).
/// <para>
/// <b>What these two handlers own is R10 and nothing else</b>, so that is what is asserted: the reader is
/// told which alert types to return, the acknowledger is told which types may be acknowledged, and a
/// staff member with no alert audience never reaches either.
/// </para>
/// </summary>
public sealed class AdminAlertHandlerTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static readonly DateTime Raised = new(2026, 10, 9, 3, 11, 0, DateTimeKind.Utc);

    private static readonly AdminActor Actor = new(42, "super@example.com", "127.0.0.1", "agent", "corr");

    private const string OrderPublicId = "44444444-4444-4444-8444-444444444444";

    private static AdminAlertRow Row(string? detailsJson, int id = 7) => new(
        id,
        AdminAlertTypes.EmailDeliveryFailed,
        (byte)AdminAlertSeverity.Warning,
        "Email not delivered: OrderPlaced for order SD-2026-000123 (outbox row 4821)",
        Raised,
        AdminAlert.EmailOutboxEntityType,
        "4821",
        detailsJson);

    // ---------------------------------------------------------------- the read

    [Fact]
    public async Task A_super_admin_is_read_every_alert_type()
    {
        var reader = new FakeReader([Row(null)]);
        var handler = new GetOpenAdminAlertsQueryHandler(Permissions(grantsAll: true), reader);

        var banner = await handler.HandleAsync(new GetOpenAdminAlertsQuery(42), Token);

        Assert.Equal(AdminAlertTypes.All, reader.AskedFor);
        Assert.Equal(GetOpenAdminAlertsQuery.MaxAlerts, reader.AskedTake);
        Assert.Single(banner.Alerts);
    }

    [Fact]
    public async Task Order_staff_are_read_only_the_two_payment_types()
    {
        var reader = new FakeReader([]);
        var handler = new GetOpenAdminAlertsQueryHandler(
            Permissions(grantsAll: false, SpencerDenim.Domain.Identity.Permissions.Orders.Approve),
            reader);

        await handler.HandleAsync(new GetOpenAdminAlertsQuery(42), Token);

        Assert.Equal(
            [AdminAlertTypes.PaymentException, AdminAlertTypes.CaptureRecoveryFailed],
            reader.AskedFor);
    }

    /// <summary>
    /// A staff member R10 gives no path to is answered without the database being asked at all.
    /// </summary>
    /// <remarks>
    /// <b>Not a performance point.</b> There is no set of rows that could be the right answer for them, so
    /// the one shape of this call that must not exist is "ask the reader for nothing and hope it filters"
    /// — a reader handed an empty list by accident is a reader one wrong `if` away from returning
    /// everything.
    /// </remarks>
    [Fact]
    public async Task A_staff_member_with_no_alert_audience_is_answered_without_a_read()
    {
        var reader = new FakeReader([Row(null)]);
        var handler = new GetOpenAdminAlertsQueryHandler(
            Permissions(grantsAll: false, SpencerDenim.Domain.Identity.Permissions.Orders.View),
            reader);

        var banner = await handler.HandleAsync(new GetOpenAdminAlertsQuery(42), Token);

        Assert.False(banner.MoreThanShown);
        Assert.Empty(banner.Alerts);
        Assert.Equal(0, reader.Calls);
    }

    [Fact]
    public async Task The_order_link_and_the_template_code_are_lifted_out_of_the_details()
    {
        var reader = new FakeReader(
            [Row($"{{\"templateCode\":\"OrderPlaced\",\"outboxMessageId\":4821,\"orderPublicId\":\"{OrderPublicId}\",\"orderNumber\":\"SD-2026-000123\"}}")]);

        var handler = new GetOpenAdminAlertsQueryHandler(Permissions(grantsAll: true), reader);

        var banner = await handler.HandleAsync(new GetOpenAdminAlertsQuery(42), Token);

        var alert = Assert.Single(banner.Alerts);
        Assert.Equal(OrderPublicId, alert.OrderPublicId);
        Assert.Equal("SD-2026-000123", alert.OrderNumber);
        Assert.Equal("OrderPlaced", alert.TemplateCode);
        Assert.Equal(AdminAlert.EmailOutboxEntityType, alert.RelatedEntityType);
        Assert.Equal("4821", alert.RelatedEntityId);
    }

    /// <summary>
    /// <b>An order that could not be found arrives as "public id set, number null"</b>, which is the pair
    /// the banner reads as "no order to open".
    /// </summary>
    /// <remarks>
    /// <c>DeadEmailAlert</c> writes <c>orderNumber</c> only when <c>DeadEmailAlerts</c> resolved it, so
    /// this shape is the raiser saying the order was not in the database. It has to survive the mapping
    /// intact: collapsing it to "no order at all" would lose the fact that a message named one, and
    /// filling the number in from the id would put a 404 behind a link.
    /// </remarks>
    [Fact]
    public async Task An_order_that_could_not_be_found_keeps_its_id_and_no_number()
    {
        var reader = new FakeReader(
            [Row($"{{\"templateCode\":\"OrderPlaced\",\"outboxMessageId\":4821,\"orderPublicId\":\"{OrderPublicId}\"}}")]);

        var handler = new GetOpenAdminAlertsQueryHandler(Permissions(grantsAll: true), reader);

        var banner = await handler.HandleAsync(new GetOpenAdminAlertsQuery(42), Token);

        var alert = Assert.Single(banner.Alerts);
        Assert.Equal(OrderPublicId, alert.OrderPublicId);
        Assert.Null(alert.OrderNumber);
        Assert.Equal("OrderPlaced", alert.TemplateCode);
    }

    /// <summary>
    /// A details payload whose order id is unreadable still yields the template code.
    /// </summary>
    /// <remarks>
    /// They are independent facts. A Super Admin with a broken order id can still be told which message
    /// died, which is two of G-37's three.
    /// </remarks>
    [Fact]
    public async Task An_unreadable_order_id_costs_the_order_and_not_the_template_code()
    {
        var reader = new FakeReader(
            [Row("{\"templateCode\":\"OrderShipped\",\"orderPublicId\":\"not-a-guid\"}")]);

        var handler = new GetOpenAdminAlertsQueryHandler(Permissions(grantsAll: true), reader);

        var banner = await handler.HandleAsync(new GetOpenAdminAlertsQuery(42), Token);

        var alert = Assert.Single(banner.Alerts);
        Assert.Null(alert.OrderPublicId);
        Assert.Null(alert.OrderNumber);
        Assert.Equal("OrderShipped", alert.TemplateCode);
    }

    /// <summary>
    /// A details payload this build does not understand costs its own link and nothing else.
    /// </summary>
    /// <remarks>
    /// Six of the seven alert types are written by tasks that do not exist, the column has an
    /// <c>ISJSON</c> check and nothing more (doc 08 §3.8), and a banner that threw on one row's details
    /// would hide every other alert in the shop — which is the opposite of what this table is for.
    /// <para>
    /// <b>The last two cases are malformed JSON, which <c>CK_AdminAlerts_DetailsJson</c> makes
    /// unreachable from this database — and they are here because a mutation survived without them.</b>
    /// Turning the <c>catch (JsonException)</c> into a catch of something else left every other case
    /// green: all of them are <i>valid</i> JSON of a shape this method does not want, which the
    /// <c>ValueKind</c> and <c>TryGetProperty</c> checks handle without the catch ever running. The catch
    /// guards a narrower thing — SQL Server's <c>ISJSON</c> and <c>System.Text.Json</c> are two parsers
    /// over one column and they do not have to agree about every input — and this method is Application
    /// code that can be handed a string by anything. A banner that 500s on one row would hide every
    /// alert in the shop, so the guard stays and now has a test that reaches it.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("[1,2,3]")]
    [InlineData("\"a string\"")]
    [InlineData("{\"orderPublicId\":123}")]
    [InlineData("{\"orderPublicId\":\"not-a-guid\"}")]
    [InlineData("{\"orderPublicId\":\"../../etc/passwd\"}")]
    [InlineData("{\"templateCode\":\"OrderPlaced\"}")]
    [InlineData("{not json at all")]
    [InlineData("{\"orderPublicId\":")]
    public async Task Details_it_cannot_read_cost_the_link_and_nothing_else(string? detailsJson)
    {
        var reader = new FakeReader([Row(detailsJson)]);
        var handler = new GetOpenAdminAlertsQueryHandler(Permissions(grantsAll: true), reader);

        var banner = await handler.HandleAsync(new GetOpenAdminAlertsQuery(42), Token);

        var alert = Assert.Single(banner.Alerts);
        Assert.Null(alert.OrderPublicId);
        Assert.Null(alert.OrderNumber);

        // The row itself still reaches the banner, title and all. That is the point.
        Assert.Contains("SD-2026-000123", alert.Title, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>The public id is validated as a GUID before it becomes a link target.</b>
    /// </summary>
    /// <remarks>
    /// It is interpolated into an admin route (<c>/orders/{id}</c>) by the banner, and it comes from a
    /// column whose only constraint is that it holds JSON. A value that is not a GUID is dropped rather
    /// than passed through, and the normalised <c>D</c> form is what is sent, so a braced or
    /// upper-case spelling cannot produce two different links to one order.
    /// </remarks>
    [Fact]
    public async Task A_public_id_is_normalised_and_an_unbracketed_guid_is_the_only_shape_accepted()
    {
        var braced = $"{{\"orderPublicId\":\"{{{OrderPublicId}}}\"}}";
        var upper = $"{{\"orderPublicId\":\"{OrderPublicId.ToUpperInvariant()}\"}}";

        var reader = new FakeReader([Row(braced, 7), Row(upper, 8)]);
        var handler = new GetOpenAdminAlertsQueryHandler(Permissions(grantsAll: true), reader);

        var banner = await handler.HandleAsync(new GetOpenAdminAlertsQuery(42), Token);

        // `D` is the exact format the parse demands, so the braced `B` form is refused…
        Assert.Null(banner.Alerts[0].OrderPublicId);

        // …and the case-insensitive `D` form is accepted and normalised to lower case.
        Assert.Equal(OrderPublicId, banner.Alerts[1].OrderPublicId);
    }

    /// <summary>
    /// "There are more" is the reader's answer and is passed through, not inferred from the page length.
    /// </summary>
    /// <remarks>
    /// The handler cannot work it out for itself — it does not know the cap the reader was given — and
    /// inferring it from <c>rows.Count == MaxAlerts</c> would be wrong in the one case that matters:
    /// exactly ten open alerts and no more.
    /// </remarks>
    [Fact]
    public async Task Whether_there_are_more_is_the_readers_answer_and_is_passed_through()
    {
        var more = new FakeReader([Row(null)]) { MoreThanTake = true };
        var handler = new GetOpenAdminAlertsQueryHandler(Permissions(grantsAll: true), more);

        var banner = await handler.HandleAsync(new GetOpenAdminAlertsQuery(42), Token);

        Assert.True(banner.MoreThanShown);
        Assert.Single(banner.Alerts);

        var exactly = new FakeReader([Row(null)]);
        banner = await new GetOpenAdminAlertsQueryHandler(Permissions(grantsAll: true), exactly)
            .HandleAsync(new GetOpenAdminAlertsQuery(42), Token);

        Assert.False(banner.MoreThanShown);
    }

    // ------------------------------------------------------------ the acknowledge

    [Fact]
    public async Task Acknowledging_passes_the_visible_types_and_the_actor()
    {
        var acknowledger = new FakeAcknowledger();
        var handler = new AcknowledgeAdminAlertCommandHandler(Permissions(grantsAll: true), acknowledger);

        var result = await handler.HandleAsync(new AcknowledgeAdminAlertCommand(7, Actor), Token);

        var acknowledged = Assert.IsType<AcknowledgeAdminAlertResult.Acknowledged>(result);
        Assert.Equal(7, acknowledged.AlertId);

        Assert.Equal(AdminAlertTypes.All, acknowledger.VisibleTypes);
        Assert.Same(Actor, acknowledger.Actor);
    }

    /// <summary>
    /// <b>The command carries no acknowledgement note</b>, and nothing between here and the database can
    /// put one in.
    /// </summary>
    /// <remarks>
    /// Owner's decision, 2026-10-09. Doc 08 §3.8 defines <c>AcknowledgementNote</c> and the migration
    /// creates it; nothing writes it, because <c>ops.AdminAlerts</c> is not in doc 08 §3.1 step 2's
    /// anonymisation deletion list and doc 11 §4 says these rows are not scrubbed — so a free-text box
    /// beside a customer's order number was the one place in this feature where a telephone number could
    /// be pasted into a table nothing can clean, and nothing read it back.
    /// <para>
    /// <b>Asserted by reflection over the command's own shape</b>, not by a behaviour. Behaviour cannot
    /// see the absence of a field: an acknowledgement with no note looks exactly like one whose note was
    /// dropped. What this pins is that no caller <i>can</i> supply one, so adding the parameter back is a
    /// red test rather than a silent re-opening. The route's own refusal is pinned in
    /// <c>AdminAlertEndpointTests</c>.
    /// </para>
    /// </remarks>
    [Fact]
    public void The_acknowledge_command_has_no_note_to_carry()
    {
        var names = typeof(AcknowledgeAdminAlertCommand)
            .GetProperties()
            .Select(property => property.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(["Actor", "AlertId"], names);
    }

    /// <summary>
    /// And the acknowledger's port takes none either, so no implementation has somewhere to put one.
    /// </summary>
    [Fact]
    public void The_acknowledger_port_takes_no_note()
    {
        var parameters = typeof(IAdminAlertAcknowledger)
            .GetMethod(nameof(IAdminAlertAcknowledger.AcknowledgeAsync))!
            .GetParameters()
            .Select(parameter => parameter.Name ?? string.Empty)
            .ToArray();

        Assert.Equal(["alertId", "visibleTypes", "actor", "cancellationToken"], parameters);
    }

    /// <summary>
    /// An alert cannot be acknowledged by a request with no identified staff user.
    /// </summary>
    /// <remarks>
    /// Doc 08 §3.8 makes <c>AcknowledgedByUserId</c> nullable, so the database would take it. A row saying
    /// the problem was seen that cannot say by whom is not an acknowledgement, and doc 11 §6.1 reads these
    /// rows to escalate: "an alert nobody has acknowledged for more than 24 hours is escalated".
    /// </remarks>
    [Fact]
    public async Task An_acknowledgement_with_nobody_in_it_is_refused()
    {
        var acknowledger = new FakeAcknowledger();
        var handler = new AcknowledgeAdminAlertCommandHandler(Permissions(grantsAll: true), acknowledger);

        var result = await handler.HandleAsync(
            new AcknowledgeAdminAlertCommand(7, AdminActor.Unknown), Token);

        Assert.IsType<AcknowledgeAdminAlertResult.NoActor>(result);
        Assert.Equal(0, acknowledger.Calls);
    }

    [Fact]
    public async Task A_staff_member_with_no_alert_audience_cannot_acknowledge_anything()
    {
        var acknowledger = new FakeAcknowledger();
        var handler = new AcknowledgeAdminAlertCommandHandler(
            Permissions(grantsAll: false, SpencerDenim.Domain.Identity.Permissions.Settings.Edit),
            acknowledger);

        var result = await handler.HandleAsync(new AcknowledgeAdminAlertCommand(7, Actor), Token);

        Assert.IsType<AcknowledgeAdminAlertResult.NotFound>(result);
        Assert.Equal(0, acknowledger.Calls);
    }

    [Fact]
    public async Task Somebody_else_getting_there_first_is_reported_with_their_time_and_is_not_a_failure()
    {
        var earlier = Raised.AddMinutes(3);
        var acknowledger = new FakeAcknowledger
        {
            Outcome = AcknowledgeAlertOutcome.AlreadyAcknowledged,
            At = earlier,
        };

        var handler = new AcknowledgeAdminAlertCommandHandler(Permissions(grantsAll: true), acknowledger);

        var result = await handler.HandleAsync(new AcknowledgeAdminAlertCommand(7, Actor), Token);

        var already = Assert.IsType<AcknowledgeAdminAlertResult.AlreadyAcknowledged>(result);
        Assert.Equal(earlier, already.AcknowledgedAtUtc);
        Assert.Equal(AdminAlertTypes.EmailDeliveryFailed, already.AlertType);
    }

    [Fact]
    public async Task A_missing_alert_is_a_not_found()
    {
        var acknowledger = new FakeAcknowledger { Outcome = AcknowledgeAlertOutcome.NotFound };
        var handler = new AcknowledgeAdminAlertCommandHandler(Permissions(grantsAll: true), acknowledger);

        var result = await handler.HandleAsync(new AcknowledgeAdminAlertCommand(7, Actor), Token);

        Assert.IsType<AcknowledgeAdminAlertResult.NotFound>(result);
    }

    // ------------------------------------------------------------------- fakes

    private static FakePermissions Permissions(bool grantsAll, params string[] permissions) =>
        new(new EffectivePermissions(grantsAll, permissions));

    private sealed class FakePermissions(EffectivePermissions permissions) : IEffectivePermissionsProvider
    {
        public Task<EffectivePermissions> GetEffectivePermissionsAsync(
            int userId,
            CancellationToken cancellationToken) => Task.FromResult(permissions);
    }

    private sealed class FakeReader(IReadOnlyList<AdminAlertRow> rows) : IAdminAlertReader
    {
        public int Calls { get; private set; }

        public IReadOnlyList<string>? AskedFor { get; private set; }

        public int AskedTake { get; private set; }

        public bool MoreThanTake { get; init; }

        public Task<(bool MoreThanTake, IReadOnlyList<AdminAlertRow> Alerts)> GetOpenAsync(
            IReadOnlyList<string> alertTypes,
            int take,
            CancellationToken cancellationToken)
        {
            Calls++;
            AskedFor = alertTypes;
            AskedTake = take;

            return Task.FromResult((MoreThanTake, rows));
        }
    }

    private sealed class FakeAcknowledger : IAdminAlertAcknowledger
    {
        public AcknowledgeAlertOutcome Outcome { get; init; } = AcknowledgeAlertOutcome.Acknowledged;

        public DateTime At { get; init; } = Raised;

        public int Calls { get; private set; }

        public IReadOnlyList<string>? VisibleTypes { get; private set; }

        public AdminActor? Actor { get; private set; }

        public Task<(AcknowledgeAlertOutcome Outcome, AcknowledgedAlert? Alert)> AcknowledgeAsync(
            int alertId,
            IReadOnlyList<string> visibleTypes,
            AdminActor actor,
            CancellationToken cancellationToken)
        {
            Calls++;
            VisibleTypes = visibleTypes;
            Actor = actor;

            return Task.FromResult<(AcknowledgeAlertOutcome, AcknowledgedAlert?)>(
                Outcome == AcknowledgeAlertOutcome.NotFound
                    ? (Outcome, null)
                    : (Outcome, new AcknowledgedAlert(alertId, AdminAlertTypes.EmailDeliveryFailed, At)));
        }
    }
}
