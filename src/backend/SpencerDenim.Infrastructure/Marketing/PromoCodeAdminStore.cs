using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using SpencerDenim.Application.Abstractions.Time;
using SpencerDenim.Application.Marketing.Promotions.Abstractions;
using SpencerDenim.Application.Marketing.Promotions.Contracts;
using SpencerDenim.Domain.Marketing;
using SpencerDenim.Infrastructure.Persistence;
using SpencerDenim.Infrastructure.Persistence.Interceptors;

namespace SpencerDenim.Infrastructure.Marketing;

/// <summary>
/// The EF Core side of <see cref="IPromoCodeAdminReader"/> (ADM-13).
/// </summary>
/// <remarks>
/// <b>The two date columns are converted to Colombo wall-clock here, at the edge.</b> Doc 06 stores UTC
/// and displays Colombo; the admin form types and reads a wall-clock time, so the projection converts and
/// nothing between here and the screen has to think about it. The conversion is in memory rather than in
/// SQL (<c>DATEADD</c>, <c>AT TIME ZONE</c>) deliberately: <c>AT TIME ZONE 'Sri Lanka Standard Time'</c>
/// depends on the host's time-zone database, which is exactly the portability problem
/// <c>ColomboTime</c>'s own remarks give as the reason for a fixed offset, and the list is tens of rows.
/// </remarks>
internal sealed class PromoCodeAdminReader(AppDbContext dbContext, IClock clock) : IPromoCodeAdminReader
{
    public async Task<IReadOnlyList<AdminPromoCodeDto>> ListAsync(CancellationToken cancellationToken)
    {
        var rows = await Projection()

            // Newest first: a staff member who has just created a code expects to see it, and the list is
            // short enough that no paging is warranted (doc 02 line 118 scopes ADM-13 to one screen).
            .OrderByDescending(row => row.Id)
            .ToListAsync(cancellationToken);

        var now = clock.UtcNow.UtcDateTime;

        return rows.Select(row => ToDto(row, now)).ToList().AsReadOnly();
    }

    public async Task<AdminPromoCodeDto?> FindAsync(int promoCodeId, CancellationToken cancellationToken)
    {
        var row = await Projection()
            .FirstOrDefaultAsync(candidate => candidate.Id == promoCodeId, cancellationToken);

        return row is null ? null : ToDto(row, clock.UtcNow.UtcDateTime);
    }

    /// <summary>
    /// Every undeleted row, projected to the columns the screen uses.
    /// </summary>
    /// <remarks>
    /// <c>IsDeleted</c> is filtered here and not by a global query filter, because <c>PromoCode</c> has
    /// none — <c>UX_PromoCodes_Code</c> is itself filtered, so "undeleted" is the only scope in which a
    /// code is a unique key, and making it explicit at each use site is what the rest of this codebase
    /// does for the soft-deleted catalogue types.
    /// </remarks>
    private IQueryable<PromoCodeRow> Projection() =>
        dbContext.PromoCodes
            .AsNoTracking()
            .Where(code => !code.IsDeleted)
            .Select(code => new PromoCodeRow(
                code.Id,
                code.Code,
                code.Description,
                code.DiscountType,
                code.DiscountValue,
                code.MaxDiscountAmount,
                code.MinSpendAmount,
                code.StartsAtUtc,
                code.EndsAtUtc,
                code.TotalUsageLimit,
                code.PerCustomerLimit,
                code.UsageCount,
                code.IsActive,
                code.RowVersion));

    private static AdminPromoCodeDto ToDto(PromoCodeRow row, DateTime utcNow) =>
        new(
            row.Id,
            row.Code,
            row.Description,
            row.DiscountType,
            row.DiscountValue,
            row.MaxDiscountAmount,
            row.MinSpendAmount,
            PromoCodeSchedule.ToColombo(row.StartsAtUtc),
            row.EndsAtUtc is { } endsAt ? PromoCodeSchedule.ToColombo(endsAt) : null,
            row.TotalUsageLimit,
            row.PerCustomerLimit,
            row.UsageCount,
            row.IsActive,
            PromoCodeSchedule.StatusOf(row.IsActive, row.StartsAtUtc, row.EndsAtUtc, utcNow),
            row.RowVersion is { } bytes ? Convert.ToBase64String(bytes) : string.Empty);

    /// <summary>The projected row, before the Colombo conversion and the derived status.</summary>
    private sealed record PromoCodeRow(
        int Id,
        string Code,
        string? Description,
        PromoDiscountType DiscountType,
        decimal? DiscountValue,
        decimal? MaxDiscountAmount,
        decimal? MinSpendAmount,
        DateTime StartsAtUtc,
        DateTime? EndsAtUtc,
        int? TotalUsageLimit,
        int? PerCustomerLimit,
        int UsageCount,
        bool IsActive,
        byte[]? RowVersion);
}

/// <summary>
/// The EF Core side of <see cref="IPromoCodeAdminStore"/> (ADM-13).
/// <para>
/// It hands out tracked entities and keeps one unit of work, so <c>PromoCodeWriteHandlers</c> decides what
/// a change is and this class decides only how it reaches SQL Server — the arrangement
/// <c>ContentPageStore</c> established. The two judgements it makes are the two the database alone can
/// make: a row somebody else changed first, and a unique index.
/// </para>
/// </summary>
internal sealed class PromoCodeAdminStore(
    AppDbContext dbContext,
    AuditAndSoftDeleteInterceptor auditColumns) : IPromoCodeAdminStore
{
    /// <summary>SQL Server: 2627 is a unique-constraint violation, 2601 a unique-index one.</summary>
    private static readonly int[] DuplicateKeyErrors = [2601, 2627];

    public Task<PromoCode?> FindAsync(int promoCodeId, CancellationToken cancellationToken) =>
        dbContext.PromoCodes.FirstOrDefaultAsync(
            code => code.Id == promoCodeId && !code.IsDeleted,
            cancellationToken);

    public void Add(PromoCode promoCode)
    {
        ArgumentNullException.ThrowIfNull(promoCode);

        dbContext.PromoCodes.Add(promoCode);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Undeleted rows only, matching <c>UX_PromoCodes_Code</c>'s own filter — so reusing a removed code's
    /// string is allowed, which is the point of filtering the index. The comparison is left to the
    /// database collation (<c>Latin1_General_100_CI_AS_SC</c>, doc 08 §1.1), which is case-insensitive, so
    /// this answers true for <c>welcome10</c> against a stored <c>WELCOME10</c> — and it must, or the save
    /// would pass the check and then be refused by the index.
    /// </remarks>
    public Task<bool> CodeTakenAsync(
        string code,
        int? exceptPromoCodeId,
        CancellationToken cancellationToken) =>
        dbContext.PromoCodes.AnyAsync(
            candidate => !candidate.IsDeleted
                && candidate.Code == code
                && (exceptPromoCodeId == null || candidate.Id != exceptPromoCodeId),
            cancellationToken);

    public async Task<PromoSaveOutcome> SaveAsync(int? actingUserId, CancellationToken cancellationToken)
    {
        auditColumns.CurrentUserId = actingUserId;

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            return PromoSaveOutcome.Saved;
        }
        catch (DbUpdateConcurrencyException)
        {
            // Nothing was written: the UPDATE's RowVersion predicate matched no row, so SQL Server applied
            // none of the batch. The caller reloads and re-applies (doc 08 §1.5).
            return PromoSaveOutcome.ConcurrencyConflict;
        }
        catch (DbUpdateException exception) when (IsDuplicateKey(exception))
        {
            return PromoSaveOutcome.DuplicateKey;
        }
    }

    /// <summary>
    /// Whether a failed save was a unique index refusing the row. The handlers ask first, so reaching here
    /// means two requests raced between the check and the write — the one case a read-then-write cannot
    /// cover, and the reason the answer is 409 rather than a 500 with an index name in it.
    /// </summary>
    private static bool IsDuplicateKey(DbUpdateException exception) =>
        exception.InnerException is SqlException sql && DuplicateKeyErrors.Contains(sql.Number);
}
