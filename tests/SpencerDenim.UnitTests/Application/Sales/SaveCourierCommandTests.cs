using SpencerDenim.Application.Abstractions.Auditing;
using SpencerDenim.Application.Abstractions.Time;
using SpencerDenim.Application.Sales.Orders.Abstractions;
using SpencerDenim.Application.Sales.Orders.Admin;
using SpencerDenim.Domain.Sales;

namespace SpencerDenim.UnitTests.Application.Sales;

/// <summary>
/// Validating a courier (ADM-09 / ADM-22, doc 11 P9's precondition).
/// <para>
/// <b>The one rule the client's money depends on is that nothing here invents a courier.</b> W3-03 shipped
/// a real bank's name and the client's own legal name as placeholder text and both were Major findings, so
/// there is a test for the <i>absence</i> of an example as well as for the validation.
/// </para>
/// </summary>
public sealed class SaveCourierCommandTests
{
    private static readonly AdminActor Actor = new(7, "staff@example.com", "203.0.113.7", "xunit", null);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_courier_needs_a_name(string? name)
    {
        var result = await HandleAsync(new FakeCourierStore(), name: name);

        var invalid = Assert.IsType<SaveCourierResult.Invalid>(result);
        Assert.Contains(invalid.Errors, error => error.Field == "name");
    }

    /// <summary>
    /// <b>A template without the placeholder is refused, and the reason is specific.</b>
    /// </summary>
    /// <remarks>
    /// Doc 08 §3.5 renders a tracking link from the template plus the order's tracking number, so a
    /// template with no placeholder would send every customer to the courier's home page. The database's
    /// <c>CK_Couriers_TrackingUrlTemplate</c> refuses it too; this check exists so the staff member gets a
    /// sentence naming the field rather than a 500 out of a constraint.
    /// </remarks>
    [Fact]
    public async Task A_tracking_address_must_contain_the_placeholder()
    {
        var result = await HandleAsync(
            new FakeCourierStore(),
            template: "https://example.test/track");

        var invalid = Assert.IsType<SaveCourierResult.Invalid>(result);
        var error = Assert.Single(invalid.Errors, candidate => candidate.Field == "trackingUrlTemplate");

        Assert.Contains(Courier.TrackingNumberPlaceholder, error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>http://</c> is refused: the link is sent to customers, and a browser would warn about or block it.
    /// </summary>
    [Theory]
    [InlineData("http://example.test/track?no={trackingNumber}")]
    [InlineData("example.test/track?no={trackingNumber}")]
    [InlineData("ftp://example.test/{trackingNumber}")]
    public async Task A_tracking_address_must_be_https(string template)
    {
        var result = await HandleAsync(new FakeCourierStore(), template: template);

        var invalid = Assert.IsType<SaveCourierResult.Invalid>(result);
        Assert.Contains(invalid.Errors, error => error.Field == "trackingUrlTemplate");
    }

    /// <summary>
    /// An empty template is accepted: that is how a staff member says "this courier has no online
    /// tracking", and doc 08 §3.5 then shows the number as text.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public async Task A_courier_may_have_no_online_tracking(string? template)
    {
        var store = new FakeCourierStore();

        var result = await HandleAsync(store, template: template);

        Assert.IsType<SaveCourierResult.Applied>(result);
        Assert.Null(store.LastDraft!.TrackingUrlTemplate);
    }

    [Fact]
    public async Task A_valid_https_template_with_the_placeholder_is_saved_as_typed()
    {
        var store = new FakeCourierStore();
        const string template = "https://track.example.test/parcel/{trackingNumber}";

        var result = await HandleAsync(store, template: template);

        Assert.IsType<SaveCourierResult.Applied>(result);
        Assert.Equal(template, store.LastDraft!.TrackingUrlTemplate);
    }

    /// <summary>
    /// A telephone number with a character a <c>varchar</c> column cannot hold is refused rather than
    /// mangled.
    /// </summary>
    /// <remarks>
    /// <c>Couriers.Phone</c> is <c>varchar(20)</c>, and SQL Server does not error on a non-ASCII character
    /// — it substitutes <c>?</c>. A number stored with question marks in it does not dial and nothing
    /// would have said so.
    /// </remarks>
    [Fact]
    public async Task A_telephone_number_that_the_column_would_mangle_is_refused()
    {
        var result = await HandleAsync(new FakeCourierStore(), phone: "011 2345678");

        var invalid = Assert.IsType<SaveCourierResult.Invalid>(result);
        Assert.Contains(invalid.Errors, error => error.Field == "phone");
    }

    [Theory]
    [InlineData("011 234 5678")]
    [InlineData("+94-11-2345678")]
    [InlineData("(011) 2345678")]
    public async Task An_ordinary_telephone_number_is_accepted_however_it_is_written(string phone)
    {
        // Several shapes, because refusing one a staff member actually uses would be a box they could not
        // get past — the argument SettingKeys.Bank.AccountNumber records about the same kind of field.
        var result = await HandleAsync(new FakeCourierStore(), phone: phone);

        Assert.IsType<SaveCourierResult.Applied>(result);
    }

    /// <summary>
    /// A name the unique index already holds comes back as a validation error on <c>name</c>, not a 500.
    /// </summary>
    [Fact]
    public async Task A_duplicate_name_is_reported_on_the_name_field()
    {
        var store = new FakeCourierStore { Outcome = new CourierWriteResult.Duplicate("Taken") };

        var result = await HandleAsync(store, name: "Taken");

        var invalid = Assert.IsType<SaveCourierResult.Invalid>(result);
        var error = Assert.Single(invalid.Errors, candidate => candidate.Field == "name");

        Assert.Contains("Taken", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_edit_aimed_at_a_missing_courier_is_not_found()
    {
        var store = new FakeCourierStore { Outcome = new CourierWriteResult.NotFound() };

        var result = await HandleAsync(store, courierId: 99);

        Assert.IsType<SaveCourierResult.NotFound>(result);
    }

    [Fact]
    public async Task A_create_goes_to_create_and_an_edit_goes_to_update()
    {
        var create = new FakeCourierStore();
        await HandleAsync(create);
        Assert.Equal("create", create.LastCall);

        var edit = new FakeCourierStore();
        await HandleAsync(edit, courierId: 4);
        Assert.Equal("update", edit.LastCall);
        Assert.Equal(4, edit.LastCourierId);
    }

    /// <summary>
    /// <b>The only example this code may show is the placeholder itself.</b>
    /// </summary>
    /// <remarks>
    /// A sample like <c>https://somecourier.lk/track?no={trackingNumber}</c> would be an invented
    /// carrier's address rendered beside the box the real one is typed into — a value mistakable for data,
    /// which is the criterion both of this project's Major findings about invented client data turned on.
    /// The accepted convention is an obviously-fake shape, and a bare placeholder is one.
    /// </remarks>
    [Fact]
    public void The_example_shown_to_a_staff_member_is_a_shape_and_not_an_address()
    {
        Assert.Equal(Courier.TrackingNumberPlaceholder, SaveCourierCommandHandler.TemplateExample);

        // No scheme, no host, no path: there is nothing in it a person could mistake for a real carrier.
        Assert.DoesNotContain("http", SaveCourierCommandHandler.TemplateExample, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(".", SaveCourierCommandHandler.TemplateExample, StringComparison.Ordinal);
        Assert.DoesNotContain("/", SaveCourierCommandHandler.TemplateExample, StringComparison.Ordinal);
    }

    /// <summary>
    /// The lengths this handler refuses are the columns' own, which the integration suite proves against
    /// the real table.
    /// </summary>
    [Fact]
    public async Task An_over_long_value_is_refused_before_it_reaches_the_column()
    {
        var longName = await HandleAsync(
            new FakeCourierStore(),
            name: new string('n', SaveCourierCommandHandler.NameMaxLength + 1));
        Assert.Contains(
            Assert.IsType<SaveCourierResult.Invalid>(longName).Errors,
            error => error.Field == "name");

        var longTemplate = await HandleAsync(
            new FakeCourierStore(),
            template: "https://e.test/{trackingNumber}"
                + new string('x', SaveCourierCommandHandler.TemplateMaxLength));
        Assert.Contains(
            Assert.IsType<SaveCourierResult.Invalid>(longTemplate).Errors,
            error => error.Field == "trackingUrlTemplate");

        var longPhone = await HandleAsync(
            new FakeCourierStore(),
            phone: new string('1', SaveCourierCommandHandler.PhoneMaxLength + 1));
        Assert.Contains(
            Assert.IsType<SaveCourierResult.Invalid>(longPhone).Errors,
            error => error.Field == "phone");
    }

    private static Task<SaveCourierResult> HandleAsync(
        FakeCourierStore store,
        int? courierId = null,
        string? name = "A courier",
        string? template = "https://track.example.test/{trackingNumber}",
        string? phone = null,
        bool isActive = true,
        short sortOrder = 0) =>
        new SaveCourierCommandHandler(store, new FixedClock()).HandleAsync(
            new SaveCourierCommand(courierId, name, template, phone, isActive, sortOrder, Actor),
            TestContext.Current.CancellationToken);

    private sealed class FakeCourierStore : ICourierAdminStore
    {
        public CourierWriteResult Outcome { get; init; } = new CourierWriteResult.Applied(1);

        public CourierDraft? LastDraft { get; private set; }

        public string? LastCall { get; private set; }

        public int? LastCourierId { get; private set; }

        public Task<CourierWriteResult> CreateAsync(
            CourierDraft draft,
            AdminActor actor,
            DateTime nowUtc,
            CancellationToken cancellationToken)
        {
            LastCall = "create";
            LastDraft = draft;
            return Task.FromResult(Outcome);
        }

        public Task<CourierWriteResult> UpdateAsync(
            int courierId,
            CourierDraft draft,
            AdminActor actor,
            DateTime nowUtc,
            CancellationToken cancellationToken)
        {
            LastCall = "update";
            LastCourierId = courierId;
            LastDraft = draft;
            return Task.FromResult(Outcome);
        }
    }

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => new(2026, 10, 8, 9, 0, 0, TimeSpan.Zero);
    }
}
