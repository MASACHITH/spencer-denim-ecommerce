using SpencerDenim.Application.Admin.Auditing;

namespace SpencerDenim.UnitTests.Application.Admin.Auditing;

/// <summary>
/// Validation and normalisation of the audit log viewer's query (ADM-03, W1-09). Everything here is pure,
/// so the filter and paging rules are covered without a database — the same approach
/// <c>ProductListQueryTests</c> takes for the catalogue listing.
/// </summary>
public sealed class GetAuditLogsQueryTests
{
    private static GetAuditLogsQuery Create(AuditLogListCriteria criteria)
    {
        var result = GetAuditLogsQuery.Create(criteria);
        Assert.True(result.IsSuccess, string.Join("; ", result.Errors.Select(error => $"{error.Field}: {error.Message}")));
        return result.Value;
    }

    private static List<string> FailureFields(AuditLogListCriteria criteria)
    {
        var result = GetAuditLogsQuery.Create(criteria);
        Assert.False(result.IsSuccess);
        return result.Errors.Select(error => error.Field).ToList();
    }

    [Fact]
    public void An_empty_query_uses_the_defaults()
    {
        var query = Create(new AuditLogListCriteria());

        Assert.Null(query.EntityType);
        Assert.Null(query.EntityId);
        Assert.Null(query.UserId);
        Assert.Null(query.Action);
        Assert.Null(query.FromUtc);
        Assert.Null(query.ToUtc);
        Assert.Equal(1, query.Page);
        Assert.Equal(GetAuditLogsQuery.DefaultPageSize, query.PageSize);
        Assert.Equal(0, query.Skip);
    }

    [Fact]
    public void Text_filters_are_trimmed()
    {
        var query = Create(new AuditLogListCriteria(EntityType: "  Product  ", EntityId: " 42 ", Action: " Update "));

        Assert.Equal("Product", query.EntityType);
        Assert.Equal("42", query.EntityId);
        Assert.Equal("Update", query.Action);
    }

    [Fact]
    public void Blank_text_filters_are_treated_as_absent()
    {
        var query = Create(new AuditLogListCriteria(EntityType: "   ", EntityId: string.Empty, Action: null));

        Assert.Null(query.EntityType);
        Assert.Null(query.EntityId);
        Assert.Null(query.Action);
    }

    [Fact]
    public void A_valid_user_id_is_parsed()
    {
        var query = Create(new AuditLogListCriteria(UserId: "7"));

        Assert.Equal(7, query.UserId);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("-1")]
    [InlineData("0")]
    [InlineData("1.5")]
    public void An_invalid_user_id_is_rejected(string userId)
    {
        Assert.Contains("userId", FailureFields(new AuditLogListCriteria(UserId: userId)));
    }

    [Fact]
    public void An_entity_type_longer_than_the_column_is_rejected()
    {
        var value = new string('a', GetAuditLogsQuery.MaxEntityTypeLength + 1);

        Assert.Contains("entityType", FailureFields(new AuditLogListCriteria(EntityType: value)));
    }

    [Fact]
    public void An_entity_id_longer_than_the_column_is_rejected()
    {
        var value = new string('a', GetAuditLogsQuery.MaxEntityIdLength + 1);

        Assert.Contains("entityId", FailureFields(new AuditLogListCriteria(EntityId: value)));
    }

    [Fact]
    public void An_action_longer_than_the_column_is_rejected()
    {
        var value = new string('a', GetAuditLogsQuery.MaxActionLength + 1);

        Assert.Contains("action", FailureFields(new AuditLogListCriteria(Action: value)));
    }

    [Fact]
    public void A_date_range_is_parsed_and_normalised_to_utc()
    {
        var query = Create(new AuditLogListCriteria(FromUtc: "2026-09-01", ToUtc: "2026-09-30T23:59:59Z"));

        Assert.Equal(new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc), query.FromUtc);
        Assert.Equal(DateTimeKind.Utc, query.FromUtc!.Value.Kind);
        Assert.Equal(new DateTime(2026, 9, 30, 23, 59, 59, DateTimeKind.Utc), query.ToUtc);
    }

    [Theory]
    [InlineData("not-a-date")]
    [InlineData("2026-13-40")]
    public void An_unparsable_date_is_rejected(string value)
    {
        Assert.Contains("fromUtc", FailureFields(new AuditLogListCriteria(FromUtc: value)));
    }

    [Fact]
    public void A_from_date_after_the_to_date_is_rejected()
    {
        Assert.Contains(
            "fromUtc",
            FailureFields(new AuditLogListCriteria(FromUtc: "2026-09-30", ToUtc: "2026-09-01")));
    }

    [Fact]
    public void The_same_bare_date_for_from_and_to_spans_the_whole_day()
    {
        var query = Create(new AuditLogListCriteria(FromUtc: "2026-09-01", ToUtc: "2026-09-01"));

        Assert.Equal(new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc), query.FromUtc);
        Assert.Equal(new DateTime(2026, 9, 1, 23, 59, 59, 999, DateTimeKind.Utc).AddTicks(9999), query.ToUtc);
    }

    /// <summary>
    /// Regression test: an HTML <c>&lt;input type="date"&gt;</c> — what the admin screen's "To" filter
    /// uses — sends a bare date with no time-of-day. Treating that literally as midnight would make
    /// <c>toUtc=2026-09-30</c> match only rows at exactly 2026-09-30T00:00:00.000, silently excluding the
    /// rest of the day a caller plainly meant to include.
    /// </summary>
    [Fact]
    public void A_bare_to_date_is_rolled_to_the_end_of_that_day()
    {
        var query = Create(new AuditLogListCriteria(ToUtc: "2026-09-30"));

        Assert.Equal(new DateTime(2026, 9, 30, 0, 0, 0, DateTimeKind.Utc).AddDays(1).AddTicks(-1), query.ToUtc);
        Assert.Equal(DateTimeKind.Utc, query.ToUtc!.Value.Kind);
    }

    /// <summary>A bare from-date is left at midnight: it is already the start of the day.</summary>
    [Fact]
    public void A_bare_from_date_stays_at_midnight()
    {
        var query = Create(new AuditLogListCriteria(FromUtc: "2026-09-01"));

        Assert.Equal(new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc), query.FromUtc);
    }

    /// <summary>A timestamp that already carries a time-of-day is trusted exactly as given, even as the upper bound.</summary>
    [Fact]
    public void A_to_timestamp_with_an_explicit_time_of_day_is_not_rolled_forward()
    {
        var query = Create(new AuditLogListCriteria(ToUtc: "2026-09-30T12:00:00Z"));

        Assert.Equal(new DateTime(2026, 9, 30, 12, 0, 0, DateTimeKind.Utc), query.ToUtc);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("abc")]
    public void An_invalid_page_is_rejected(string page)
    {
        Assert.Contains("page", FailureFields(new AuditLogListCriteria(Page: page)));
    }

    [Fact]
    public void A_page_beyond_the_maximum_is_rejected()
    {
        var page = (GetAuditLogsQuery.MaxPage + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);

        Assert.Contains("page", FailureFields(new AuditLogListCriteria(Page: page)));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("abc")]
    public void An_invalid_page_size_is_rejected(string pageSize)
    {
        Assert.Contains("pageSize", FailureFields(new AuditLogListCriteria(PageSize: pageSize)));
    }

    [Fact]
    public void A_page_size_beyond_the_maximum_is_rejected()
    {
        var pageSize = (GetAuditLogsQuery.MaxPageSize + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);

        Assert.Contains("pageSize", FailureFields(new AuditLogListCriteria(PageSize: pageSize)));
    }

    [Fact]
    public void Skip_is_computed_from_page_and_page_size()
    {
        var query = Create(new AuditLogListCriteria(Page: "3", PageSize: "10"));

        Assert.Equal(20, query.Skip);
    }

    [Fact]
    public void A_null_criteria_throws()
    {
        Assert.Throws<ArgumentNullException>(() => GetAuditLogsQuery.Create(null!));
    }

    [Fact]
    public void Every_error_is_collected_in_one_pass()
    {
        var fields = FailureFields(new AuditLogListCriteria(UserId: "abc", Page: "0", PageSize: "0"));

        Assert.Contains("userId", fields);
        Assert.Contains("page", fields);
        Assert.Contains("pageSize", fields);
    }
}
