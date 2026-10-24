using SpencerDenim.Application.Admin.CurrentUser;
using SpencerDenim.Application.Admin.Users;
using SpencerDenim.Application.Admin.Users.Abstractions;
using SpencerDenim.Application.Admin.Users.Contracts;
using SpencerDenim.Application.Catalog.Contracts;
using SpencerDenim.Domain.Identity;

namespace SpencerDenim.UnitTests.Application.Admin.Users;

/// <summary>
/// The read side of the "protected targets are invisible" rule (tester round 2, defect B). A viewer who does not
/// hold GrantsAll must never be shown a GrantsAll holder, so each read handler has to resolve the viewer's own
/// permissions from the actor id it is given and tell its reader whether GrantsAll holders may be included. The
/// filtering itself is SQL and is proven against a real database in <c>UserEndpointTests</c>; these tests pin the
/// part that cannot be seen from there, which is <i>how the decision is made</i>: from the viewer's real
/// effective permissions, per request, never from a default and never from the principal.
/// </summary>
public sealed class StaffUserReadVisibilityTests
{
    private const int ViewerId = 7;

    private static readonly EffectivePermissions SuperAdmin = new(true, []);

    private static StubAdminUserLookup ViewerLookup(string email = "viewer@example.com") =>
        new(new AdminUserSummary(ViewerId, email, "Viewer", null, true, false, "viewer-stamp"));

    private static readonly EffectivePermissions UsersViewOnly = new(false, [Permissions.Users.View]);

    // A holder of every Users.* permission is still not a Super Admin, and that is the point of the rule.
    private static readonly EffectivePermissions UsersManager = new(false, [Permissions.Users.View, Permissions.Users.Manage]);

    public static TheoryData<EffectivePermissions, bool> Viewers => new()
    {
        { SuperAdmin, true },
        { UsersViewOnly, false },
        { UsersManager, false },
    };

    [Theory]
    [MemberData(nameof(Viewers))]
    public async Task The_user_list_includes_GrantsAll_holders_only_for_a_GrantsAll_viewer(EffectivePermissions viewer, bool expectedInclude)
    {
        var reader = new RecordingStaffUserDirectoryReader();
        var provider = new RecordingPermissionsProvider(viewer);
        var handler = new GetStaffUsersQueryHandler(reader, provider, ViewerLookup(), new StubProtectedStaffAccounts());
        var query = GetStaffUsersQuery.Create(new StaffUserListCriteria(), ViewerId).Value;

        await handler.HandleAsync(query, CancellationToken.None);

        Assert.Equal(expectedInclude, reader.ListIncludedGrantsAllHolders);
        Assert.Equal(ViewerId, provider.LastUserIdRequested);
    }

    [Theory]
    [MemberData(nameof(Viewers))]
    public async Task The_user_detail_includes_GrantsAll_holders_only_for_a_GrantsAll_viewer(EffectivePermissions viewer, bool expectedInclude)
    {
        var reader = new RecordingStaffUserDirectoryReader();
        var provider = new RecordingPermissionsProvider(viewer);
        var handler = new GetStaffUserByIdQueryHandler(reader, provider, ViewerLookup(), new StubProtectedStaffAccounts());

        await handler.HandleAsync(new GetStaffUserByIdQuery(UserId: 3, ActorUserId: ViewerId), CancellationToken.None);

        Assert.Equal(expectedInclude, reader.GetByIdIncludedGrantsAllHolders);
        Assert.Equal(3, reader.LastUserIdRequested);
        Assert.Equal(ViewerId, provider.LastUserIdRequested);
    }

    [Theory]
    [MemberData(nameof(Viewers))]
    public async Task The_role_list_includes_the_GrantsAll_role_only_for_a_GrantsAll_viewer(EffectivePermissions viewer, bool expectedInclude)
    {
        var reader = new RecordingRoleDirectoryReader();
        var provider = new RecordingPermissionsProvider(viewer);
        var handler = new GetRolesQueryHandler(reader, provider);

        await handler.HandleAsync(new GetRolesQuery(ViewerId), CancellationToken.None);

        Assert.Equal(expectedInclude, reader.ListIncludedGrantsAllHolders);
        Assert.Equal(ViewerId, provider.LastUserIdRequested);
    }

    [Theory]
    [MemberData(nameof(Viewers))]
    public async Task The_role_detail_includes_the_GrantsAll_role_only_for_a_GrantsAll_viewer(EffectivePermissions viewer, bool expectedInclude)
    {
        var reader = new RecordingRoleDirectoryReader();
        var provider = new RecordingPermissionsProvider(viewer);
        var handler = new GetRoleByIdQueryHandler(reader, provider);

        await handler.HandleAsync(new GetRoleByIdQuery(RoleId: 2, ActorUserId: ViewerId), CancellationToken.None);

        Assert.Equal(expectedInclude, reader.GetByIdIncludedGrantsAllHolders);
        Assert.Equal(2, reader.LastRoleIdRequested);
        Assert.Equal(ViewerId, provider.LastUserIdRequested);
    }

    [Fact]
    public async Task A_missing_target_and_a_hidden_target_are_the_same_answer_to_the_caller()
    {
        // The reader returns null for both a missing id and a hidden GrantsAll holder, and the handler must pass
        // that through untouched. If it ever substituted its own answer for the hidden case, the two would stop
        // being indistinguishable, which is the whole purpose of the rule.
        var reader = new RecordingStaffUserDirectoryReader { DetailToReturn = null };
        var handler = new GetStaffUserByIdQueryHandler(reader, new RecordingPermissionsProvider(UsersViewOnly), ViewerLookup(), new StubProtectedStaffAccounts());

        var result = await handler.HandleAsync(new GetStaffUserByIdQuery(UserId: 1, ActorUserId: ViewerId), CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task A_hidden_page_is_the_empty_page_the_reader_returned_not_a_marked_one()
    {
        var reader = new RecordingStaffUserDirectoryReader();
        var handler = new GetStaffUsersQueryHandler(reader, new RecordingPermissionsProvider(UsersViewOnly), ViewerLookup(), new StubProtectedStaffAccounts());
        var query = GetStaffUsersQuery.Create(new StaffUserListCriteria(Page: "2", PageSize: "10"), ViewerId).Value;

        var page = await handler.HandleAsync(query, CancellationToken.None);

        Assert.Empty(page.Items);
        Assert.Equal(2, page.Page);
        Assert.Equal(10, page.PageSize);
        Assert.Equal(0, page.TotalCount);
    }

    private sealed class RecordingPermissionsProvider(EffectivePermissions permissions) : IEffectivePermissionsProvider
    {
        public int? LastUserIdRequested { get; private set; }

        public Task<EffectivePermissions> GetEffectivePermissionsAsync(int userId, CancellationToken cancellationToken)
        {
            LastUserIdRequested = userId;
            return Task.FromResult(permissions);
        }
    }

    private sealed class RecordingStaffUserDirectoryReader : IStaffUserDirectoryReader
    {
        public bool? ListIncludedGrantsAllHolders { get; private set; }

        public bool? GetByIdIncludedGrantsAllHolders { get; private set; }

        public int? LastUserIdRequested { get; private set; }

        public StaffUserDetailDto? DetailToReturn { get; init; }

        public Task<PagedResult<StaffUserSummaryDto>> ListAsync(GetStaffUsersQuery query, bool includeGrantsAllHolders, CancellationToken cancellationToken)
        {
            ListIncludedGrantsAllHolders = includeGrantsAllHolders;
            return Task.FromResult(PagedResult.Empty<StaffUserSummaryDto>(query.Page, query.PageSize));
        }

        public Task<StaffUserDetailDto?> GetByIdAsync(int userId, bool includeGrantsAllHolders, CancellationToken cancellationToken)
        {
            GetByIdIncludedGrantsAllHolders = includeGrantsAllHolders;
            LastUserIdRequested = userId;
            return Task.FromResult(DetailToReturn);
        }
    }

    private sealed class RecordingRoleDirectoryReader : IRoleDirectoryReader
    {
        public bool? ListIncludedGrantsAllHolders { get; private set; }

        public bool? GetByIdIncludedGrantsAllHolders { get; private set; }

        public int? LastRoleIdRequested { get; private set; }

        public Task<IReadOnlyList<RoleSummaryDto>> ListAsync(bool includeGrantsAllHolders, CancellationToken cancellationToken)
        {
            ListIncludedGrantsAllHolders = includeGrantsAllHolders;
            return Task.FromResult<IReadOnlyList<RoleSummaryDto>>([]);
        }

        public Task<RoleDetailDto?> GetByIdAsync(int roleId, bool includeGrantsAllHolders, CancellationToken cancellationToken)
        {
            GetByIdIncludedGrantsAllHolders = includeGrantsAllHolders;
            LastRoleIdRequested = roleId;
            return Task.FromResult<RoleDetailDto?>(null);
        }
    }
}
