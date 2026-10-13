using SpencerDenim.Application.Marketing.Promotions.Contracts;
using SpencerDenim.Domain.Marketing;

namespace SpencerDenim.Application.Marketing.Promotions.Abstractions;

/// <summary>Read port for the admin sale-campaign screen (ADM-14, <c>Promotions.View</c>).</summary>
public interface ISaleCampaignAdminReader
{
    /// <summary>
    /// Every campaign that has not been removed, newest first, with its targets.
    /// <para>
    /// <b>Inactive, scheduled and ended campaigns are included</b>, unlike the pricing reader's: the list
    /// is where a staff member ends a sale early or turns one back on (doc 11 P3 step 5, and doc 11 §6.4
    /// item 17's weekly "check upcoming campaign dates"). Soft-deleted rows are not, because removing one
    /// is how a staff member says it is finished with.
    /// </para>
    /// </summary>
    Task<IReadOnlyList<AdminSaleCampaignDto>> ListAsync(CancellationToken cancellationToken);

    /// <summary>One campaign by id, or <see langword="null"/> when there is none or it was removed.</summary>
    Task<AdminSaleCampaignDto?> FindAsync(int campaignId, CancellationToken cancellationToken);
}

/// <summary>
/// Which of the rows a target may point at actually exist, so a bad id is a named field error rather than
/// a foreign-key violation.
/// </summary>
/// <remarks>
/// <b>Three sets and not three <c>bool</c> lookups</b>, so validating a campaign with twenty targets is
/// three round-trips rather than twenty. Soft-deleted categories and products are <b>excluded</b>: the
/// foreign key would accept one, and a campaign targeting a product nobody can buy is a staff member's
/// mistake worth saying out loud.
/// </remarks>
public readonly record struct SaleCampaignTargetExistence(
    IReadOnlySet<int> DepartmentIds,
    IReadOnlySet<int> CategoryIds,
    IReadOnlySet<int> ProductIds);

/// <summary>
/// Write port for the admin sale-campaign screen (ADM-14).
/// </summary>
/// <remarks>
/// Hands back tracked entities and takes one <see cref="SaveAsync"/> — the arrangement
/// <see cref="IPromoCodeAdminStore"/> and <c>IContentPageStore</c> established — so a change and its audit
/// row are one transaction. <b>Nothing here validates.</b> Every rule lives in
/// <c>SaleCampaignWriteHandlers</c>, where it is unit-testable without a database and where a failure is a
/// named field in a 400.
/// </remarks>
public interface ISaleCampaignAdminStore
{
    /// <summary>One undeleted campaign for update, with its targets loaded, or <see langword="null"/>.</summary>
    Task<SaleCampaign?> FindAsync(int campaignId, CancellationToken cancellationToken);

    /// <summary>Adds a new campaign to the unit of work. Written by <see cref="SaveAsync"/>.</summary>
    void Add(SaleCampaign campaign);

    /// <summary>
    /// Which of these ids exist and are reachable (see <see cref="SaleCampaignTargetExistence"/>).
    /// </summary>
    Task<SaleCampaignTargetExistence> WhichTargetsExistAsync(
        IReadOnlyCollection<int> departmentIds,
        IReadOnlyCollection<int> categoryIds,
        IReadOnlyCollection<int> productIds,
        CancellationToken cancellationToken);

    /// <summary>
    /// Whether this <c>media.MediaAssets</c> id exists, is ready and is not deleted — doc 11 P3 step 1's
    /// optional banner.
    /// </summary>
    Task<bool> BannerExistsAsync(int mediaAssetId, CancellationToken cancellationToken);

    /// <summary>Commits the unit of work, stamping the audit columns of doc 08 §1.5.</summary>
    /// <param name="actingUserId">
    /// Who is saving. Passed in rather than read from a context, because the Application layer has no
    /// request to read one from.
    /// </param>
    /// <param name="cancellationToken">Cancellation.</param>
    Task<SaleCampaignSaveOutcome> SaveAsync(int? actingUserId, CancellationToken cancellationToken);
}

/// <summary>
/// How a sale-campaign save ended.
/// </summary>
/// <remarks>
/// <b>There is no <c>DuplicateKey</c>, and that is a fact about the schema rather than an omission.</b>
/// Doc 08 §3.6 gives <c>marketing.SaleCampaigns</c> no unique index — two campaigns may share a name, and
/// two campaigns may target the same thing, because "lowest resulting price wins" makes that a legal and
/// ordinary arrangement rather than a conflict. So the only judgement the database makes here that the
/// handler cannot is the row version.
/// </remarks>
public enum SaleCampaignSaveOutcome
{
    Saved = 0,

    /// <summary>The row version did not match; nothing was written.</summary>
    ConcurrencyConflict = 1,
}
