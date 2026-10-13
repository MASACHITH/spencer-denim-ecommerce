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
        // Newest first: a staff member who has just created a code expects to see it, and the list is
        // short enough that no paging is warranted (doc 02 line 118 scopes ADM-13 to one screen).
        //
        // **The ordering is applied before the projection, and it has to be** — the same EF Core limit
        // `FindAsync` documents: `OrderByDescending(row => row.Id)` on the projected `PromoCodeRow` is a
        // member access EF cannot see through a positional record's constructor, and the whole list
        // request answers 500. That was a real defect from W3-05 (12 Oct) until W3-05c.
        var rows = await Projection(descendingById: true).ToListAsync(cancellationToken);

        var now = clock.UtcNow.UtcDateTime;

        return rows.Select(row => ToDto(row, now)).ToList().AsReadOnly();
    }

    /// <summary>
    /// One row, or null.
    /// </summary>
    /// <remarks>
    /// <b>The id is filtered before the projection, and it has to be.</b> Written the other way round —
    /// <c>Projection().FirstOrDefaultAsync(candidate =&gt; candidate.Id == promoCodeId)</c> — the predicate
    /// is a member access on a <see cref="PromoCodeRow"/> that EF Core has to see through a positional
    /// record's constructor, which it cannot: it answers
    /// <i>"could not be translated"</i> and the request becomes a 500. That was a real defect from
    /// W3-05 (12 Oct) until W3-05c: <c>PromoCodeWriteHandlers.CommitAsync</c> re-reads through this method
    /// after every save, so <b>every create and every edit of a promo code answered 500</b> while the row
    /// was written. Nothing caught it because the promo-code screen's only coverage was a Playwright suite
    /// against a Node stub, and #126 is the standing note that Playwright never starts the .NET API.
    /// <c>AdminPromoCodeEndpointTests</c> is the test that now would.
    /// </remarks>
    public async Task<AdminPromoCodeDto?> FindAsync(int promoCodeId, CancellationToken cancellationToken)
    {
        var row = await Projection(code => code.Id == promoCodeId)
            .FirstOrDefaultAsync(cancellationToken);

        return row is null ? null : ToDto(row, clock.UtcNow.UtcDateTime);
    }

    /// <summary>
    /// Every undeleted row, projected to the columns the screen uses.
    /// </summary>
    /// <remarks>
    /// <c>IsDeleted</c> is excluded both ways: <c>PromoCode</c> carries the global soft-delete filter
    /// (<c>ApplySoftDelete</c>, doc 08 §1.5), and the predicate is also written out here. That is not
    /// belt-and-braces for its own sake — it is what survives an <c>IgnoreQueryFilters()</c>, which
    /// <c>CheckoutReader.ReadCartAsync</c> uses two methods away for a documented reason, so the next
    /// person to need one on this query does not silently start matching removed codes.
    /// </remarks>
    /// <param name="filter">
    /// An extra predicate on the <b>entity</b>, applied before the projection.
    /// </param>
    /// <param name="descendingById">Newest first, ordered on the entity.</param>
    /// <remarks>
    /// Both the filter and the ordering are taken here, on the entity, rather than applied to what this
    /// method returns. EF Core cannot translate a member access on the projected <see cref="PromoCodeRow"/>
    /// — it would have to see through a positional record's constructor — so either one written after the
    /// <c>Select</c> turns the request into a 500. See <see cref="FindAsync"/> for what that cost.
    /// </remarks>
    private IQueryable<PromoCodeRow> Projection(
        System.Linq.Expressions.Expression<Func<PromoCode, bool>>? filter = null,
        bool descendingById = false)
    {
        var codes = dbContext.PromoCodes
            .AsNoTracking()
            .Where(code => !code.IsDeleted);

        if (filter is not null)
        {
            codes = codes.Where(filter);
        }

        if (descendingById)
        {
            codes = codes.OrderByDescending(code => code.Id);
        }

        return codes
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
                code.ExcludeSaleItems,
                code.TotalUsageLimit,
                code.PerCustomerLimit,
                code.UsageCount,
                code.IsActive,
                code.RowVersion));
    }

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
            row.ExcludeSaleItems,
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
        bool ExcludeSaleItems,
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
