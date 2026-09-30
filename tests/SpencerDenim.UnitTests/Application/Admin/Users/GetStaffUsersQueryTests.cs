using SpencerDenim.Application.Admin.Users;

namespace SpencerDenim.UnitTests.Application.Admin.Users;

/// <summary>The staff user listing query's validation (ADM-02, W1-08), built the same way <c>GetAuditLogsQueryTests</c> is for the audit log viewer.</summary>
public sealed class GetStaffUsersQueryTests
{
    [Fact]
    public void Defaults_apply_when_nothing_is_given()
    {
        var result = GetStaffUsersQuery.Create(new StaffUserListCriteria());

        Assert.True(result.IsSuccess);
        Assert.Equal(1, result.Value.Page);
        Assert.Equal(GetStaffUsersQuery.DefaultPageSize, result.Value.PageSize);
        Assert.Null(result.Value.Search);
        Assert.Null(result.Value.RoleId);
        Assert.Null(result.Value.IsActive);
    }

    [Fact]
    public void Every_invalid_field_is_reported_together()
    {
        var criteria = new StaffUserListCriteria(
            Search: new string('a', GetStaffUsersQuery.MaxSearchLength + 1),
            RoleId: "not-a-number",
            IsActive: "maybe",
            Page: "0",
            PageSize: "0");

        var result = GetStaffUsersQuery.Create(criteria);

        Assert.False(result.IsSuccess);
        Assert.Equal(5, result.Errors.Count);
    }

    [Fact]
    public void PageSize_above_the_maximum_is_rejected()
    {
        var result = GetStaffUsersQuery.Create(new StaffUserListCriteria(PageSize: (GetStaffUsersQuery.MaxPageSize + 1).ToString(System.Globalization.CultureInfo.InvariantCulture)));

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Errors, e => e.Field == "pageSize");
    }

    [Fact]
    public void Valid_filters_are_trimmed_and_parsed()
    {
        var result = GetStaffUsersQuery.Create(new StaffUserListCriteria(Search: "  ada  ", RoleId: "3", IsActive: "true", Page: "2", PageSize: "10"));

        Assert.True(result.IsSuccess);
        Assert.Equal("ada", result.Value.Search);
        Assert.Equal(3, result.Value.RoleId);
        Assert.True(result.Value.IsActive);
        Assert.Equal(2, result.Value.Page);
        Assert.Equal(10, result.Value.PageSize);
        Assert.Equal(10, result.Value.Skip); // (page 2 - 1) * pageSize 10
    }
}
