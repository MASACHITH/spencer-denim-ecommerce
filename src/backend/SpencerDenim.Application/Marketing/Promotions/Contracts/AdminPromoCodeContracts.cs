using SpencerDenim.Domain.Marketing;

namespace SpencerDenim.Application.Marketing.Promotions.Contracts;

/// <summary>
/// One <c>marketing.PromoCodes</c> row as the admin screen draws and edits it (ADM-13, doc 11 P3 B).
/// </summary>
/// <remarks>
/// <b>One shape for the list and the form, deliberately.</b> The screen is a list with an inline add/edit
/// panel — the arrangement the couriers screen uses — so a second, narrower list DTO would only be a
/// second place for a field to go missing from. Every column a staff member can set is here, and the
/// derived <see cref="Status"/> and the cached <see cref="UsageCount"/> come with it so the list can be
/// read without a second request.
/// <para>
/// <b><c>ExcludeSaleItems</c> is here as of W3-05c, and was a gap before it.</b> W3-05 left it off
/// because the flag could only exclude a line a sale campaign priced and <c>marketing.SaleCampaigns</c>
/// did not exist (ADM-14) — a checkbox that cannot change any total is a promise the shop would not keep.
/// ADM-14 merged, the flag really does exclude a line
/// (<c>CheckoutCampaignBehaviourTests.Exclude_sale_items_drops_the_campaign_line_and_keeps_the_markdown_line</c>
/// proves it), and the shop was applying a rule no staff member could set. It is now settable.
/// </para>
/// </remarks>
/// <param name="StartsAt">
/// <b>Colombo wall-clock time, not UTC.</b> The column is <c>StartsAtUtc</c>; this is what
/// <c>PromoCodeSchedule.ToColombo</c> made of it, because a staff member who typed "12 October 23:59"
/// meant 23:59 in the shop. Sent back the same way on a save. Doc 06 stores UTC and displays Colombo, and
/// the order emails already say so to customers.
/// </param>
/// <param name="EndsAt">Also Colombo wall-clock. Null is "no end".</param>
/// <param name="ExcludeSaleItems">
/// Doc 08 §6 step 7 and doc 11 §7 G-07: <b>lines a sale campaign priced are out of the eligible base,
/// and lines that only carry a markdown stay in</b>. The distinction is the whole of the field — see
/// <see cref="PromoEvaluator"/>'s <c>IsEligible</c>, which tests <c>UnitPrice &gt;= UnitListPrice</c>
/// because only doc 08 §6 step 3's campaign moves a unit price below its list price.
/// </param>
/// <param name="UsageCount">
/// Held uses (doc 08 §3.6), which is what the total limit is measured against. It is not editable: the
/// two statements in <c>OrderPromoUse</c> are the only things that move it.
/// </param>
/// <param name="Status">
/// Doc 11 P3's derived status. Not stored — see <see cref="PromoCodeSchedule.StatusOf"/> — so the list
/// cannot disagree with what a shopper's code does.
/// </param>
/// <param name="RowVersion">
/// Base64 <c>rowversion</c>, round-tripped as an ETag through <c>If-Match</c> or the body, so two staff
/// members editing one code do not silently overwrite each other (doc 08 §1.5).
/// </param>
public sealed record AdminPromoCodeDto(
    int Id,
    string Code,
    string? Description,
    PromoDiscountType DiscountType,
    decimal? DiscountValue,
    decimal? MaxDiscountAmount,
    decimal? MinSpendAmount,
    DateTime StartsAt,
    DateTime? EndsAt,
    bool ExcludeSaleItems,
    int? TotalUsageLimit,
    int? PerCustomerLimit,
    int UsageCount,
    bool IsActive,
    PromoCodeStatus Status,
    string RowVersion);

/// <summary>
/// What a save answers: the row, plus anything the staff member has to be told although it worked.
/// </summary>
/// <param name="Warnings">
/// Sentences about a save that <b>succeeded</b> — today two of them, from
/// <c>PromoCodeWriteHandlers.Warnings</c>:
/// <list type="bullet">
/// <item><description>
/// Doc 08 §3.6 DB19's "lowering <c>TotalUsageLimit</c> below <c>UsageCount</c> is allowed with a
/// warning": the save worked, and the staff member still has to know the figure they typed is below the
/// uses already held, so the code is now blocked for new customers while the pending orders keep theirs.
/// </description></item>
/// <item><description>
/// A code whose end date has already passed, which doc 08 does not ask for — it is here because saving a
/// code nobody can use is the one mistake this form makes silently, and the derived status on the row is
/// easy to miss.
/// </description></item>
/// </list>
/// They are on the success path on purpose. A silent save that did something other than what the staff
/// member expected is the shape of defect this project has shipped three times.
/// </param>
public sealed record AdminPromoCodeSavedDto(AdminPromoCodeDto PromoCode, IReadOnlyList<string> Warnings);
