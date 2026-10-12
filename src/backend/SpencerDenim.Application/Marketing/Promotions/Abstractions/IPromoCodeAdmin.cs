using SpencerDenim.Application.Marketing.Promotions.Contracts;
using SpencerDenim.Domain.Marketing;

namespace SpencerDenim.Application.Marketing.Promotions.Abstractions;

/// <summary>Read port for the admin promo-code screen (ADM-13, <c>Promotions.View</c>).</summary>
public interface IPromoCodeAdminReader
{
    /// <summary>
    /// Every code that has not been removed, newest first.
    /// <para>
    /// <b>Inactive and ended codes are included</b>, unlike the storefront's lookup: the list is where a
    /// staff member ends an expired code or turns one back on (doc 11 §P-daily step 17, "check upcoming
    /// campaign dates and code limits; end expired codes"), and a list that hid them could not.
    /// Soft-deleted rows are not, because removing one is how a staff member says it is finished with.
    /// </para>
    /// </summary>
    Task<IReadOnlyList<AdminPromoCodeDto>> ListAsync(CancellationToken cancellationToken);

    /// <summary>One code by id, or <see langword="null"/> when there is none or it was removed.</summary>
    Task<AdminPromoCodeDto?> FindAsync(int promoCodeId, CancellationToken cancellationToken);
}

/// <summary>
/// Write port for the admin promo-code screen (ADM-13).
/// </summary>
/// <remarks>
/// Hands back tracked entities and takes one <see cref="SaveAsync"/> — the arrangement
/// <c>IContentPageStore</c> and <c>ICatalogStructureStore</c> established — so a change and its audit row
/// are one transaction. <b>Nothing here validates.</b> Every rule (the code's spelling, the discount
/// value's range for its type, "end after start", "per-customer ≤ total") lives in
/// <c>PromoCodeWriteHandlers</c>, where it is unit-testable without a database and where a failure is a
/// named field in a 400 rather than a constraint violation. The port's only judgements are the two the
/// database alone can make: a row somebody else changed first, and a unique index.
/// </remarks>
public interface IPromoCodeAdminStore
{
    /// <summary>One undeleted code for update, or <see langword="null"/>.</summary>
    Task<PromoCode?> FindAsync(int promoCodeId, CancellationToken cancellationToken);

    /// <summary>Adds a new code to the unit of work. Written by <see cref="SaveAsync"/>.</summary>
    void Add(PromoCode promoCode);

    /// <summary>
    /// <c>UX_PromoCodes_Code</c> (filtered <c>IsDeleted = 0</c>). Checked so the refusal is a named field
    /// error; the index is what makes it safe under a race, and <see cref="PromoSaveOutcome.DuplicateKey"/>
    /// is that race arriving.
    /// </summary>
    Task<bool> CodeTakenAsync(string code, int? exceptPromoCodeId, CancellationToken cancellationToken);

    /// <summary>
    /// Commits the unit of work, stamping the audit columns of doc 08 §1.5.
    /// </summary>
    /// <param name="actingUserId">
    /// Who is saving. Passed in rather than read from a context, because the Application layer has no
    /// request to read one from.
    /// </param>
    /// <param name="cancellationToken">Cancellation.</param>
    Task<PromoSaveOutcome> SaveAsync(int? actingUserId, CancellationToken cancellationToken);
}

/// <summary>How a promo-code save ended.</summary>
public enum PromoSaveOutcome
{
    Saved = 0,

    /// <summary>The row version did not match; nothing was written.</summary>
    ConcurrencyConflict = 1,

    /// <summary>
    /// <c>UX_PromoCodes_Code</c> refused it — two staff members created the same code at once. Not a
    /// substitute for <see cref="IPromoCodeAdminStore.CodeTakenAsync"/>: that is what makes the common
    /// case a field error, and this is what makes the race safe rather than lucky.
    /// </summary>
    DuplicateKey = 2,
}
