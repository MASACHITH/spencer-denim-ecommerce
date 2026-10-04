using System.Globalization;
using SpencerDenim.Application.Abstractions.Results;

namespace SpencerDenim.Application.Admin.Users;

/// <summary>
/// A validated, normalised staff user listing query (ADM-02), built the same way
/// <c>GetAuditLogsQuery</c> is: everything is already trimmed and inside its bounds, so the read store
/// composes predicates without re-checking anything.
/// </summary>
public sealed record GetStaffUsersQuery
{
    public const int MaxSearchLength = 256;

    public const int DefaultPageSize = 25;

    public const int MaxPageSize = 100;

    public const int MaxPage = 100_000;

    private GetStaffUsersQuery()
    {
    }

    /// <summary>
    /// The signed-in staff user asking. The handler resolves their effective permissions from this id (never
    /// from the principal) to decide whether GrantsAll holders are visible to them at all — see
    /// <see cref="GetStaffUsersQueryHandler"/>.
    /// </summary>
    public int ActorUserId { get; private init; }

    public string? Search { get; private init; }

    public int? RoleId { get; private init; }

    public bool? IsActive { get; private init; }

    public int Page { get; private init; } = 1;

    public int PageSize { get; private init; } = DefaultPageSize;

    public int Skip => (Page - 1) * PageSize;

    public static Result<GetStaffUsersQuery> Create(StaffUserListCriteria criteria, int actorUserId)
    {
        ArgumentNullException.ThrowIfNull(criteria);

        var errors = new List<ValidationError>();

        var search = NormaliseSearch(criteria.Search, errors);
        var roleId = ParseWholeNumber(criteria.RoleId, "roleId", 1, int.MaxValue, errors);
        var isActive = ParseBoolean(criteria.IsActive, "isActive", errors);
        var page = ParseWholeNumber(criteria.Page, "page", 1, MaxPage, errors) ?? 1;
        var pageSize = ParseWholeNumber(criteria.PageSize, "pageSize", 1, MaxPageSize, errors) ?? DefaultPageSize;

        if (errors.Count > 0)
        {
            return Result.Failure<GetStaffUsersQuery>(errors.AsReadOnly());
        }

        return Result.Success(new GetStaffUsersQuery
        {
            ActorUserId = actorUserId,
            Search = search,
            RoleId = roleId,
            IsActive = isActive,
            Page = page,
            PageSize = pageSize,
        });
    }

    private static string? NormaliseSearch(string? raw, List<ValidationError> errors)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var value = raw.Trim();
        if (value.Length > MaxSearchLength)
        {
            errors.Add(new ValidationError("search", $"search must be {MaxSearchLength} characters or fewer."));
            return null;
        }

        return value;
    }

    private static int? ParseWholeNumber(string? raw, string field, int minimum, int maximum, List<ValidationError> errors)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        if (!int.TryParse(raw.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value)
            || value < minimum || value > maximum)
        {
            errors.Add(new ValidationError(field, $"{field} must be a whole number between {minimum} and {maximum}."));
            return null;
        }

        return value;
    }

    private static bool? ParseBoolean(string? raw, string field, List<ValidationError> errors)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        if (!bool.TryParse(raw.Trim(), out var value))
        {
            errors.Add(new ValidationError(field, $"{field} must be true or false."));
            return null;
        }

        return value;
    }
}
