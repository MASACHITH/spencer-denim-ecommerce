using SpencerDenim.Application.Abstractions.Auditing;

namespace SpencerDenim.Application.Sales.Orders.Abstractions;

/// <summary>
/// Adding, editing and deactivating couriers (<c>sales.Couriers</c>, ADM-09 / ADM-22; doc 11 P9's
/// precondition "active couriers with tracking URL templates exist").
/// <para>
/// <b>There is no delete and that is deliberate.</b> An order that shipped keeps a <i>snapshot</i> of the
/// courier's name (doc 11 P9), so deleting the row would not corrupt a past dispatch — but the order also
/// keeps <c>CourierId</c>, and the tracking <i>link</i> is rendered from the courier's current template
/// (doc 08 §3.5), so deleting one turns every live parcel's link into plain text with no explanation.
/// Deactivating removes it from the picker and leaves the links working, which is what the <c>IsActive</c>
/// column is for.
/// </para>
/// </summary>
public interface ICourierAdminStore
{
    /// <summary>
    /// Creates a courier. Answers <see cref="CourierWriteResult.Duplicate"/> when the name is taken —
    /// the name is unique because two couriers with one name would make an order's dispatch ambiguous.
    /// </summary>
    Task<CourierWriteResult> CreateAsync(
        CourierDraft draft,
        AdminActor actor,
        DateTime nowUtc,
        CancellationToken cancellationToken);

    /// <summary>
    /// Updates one courier's name, template, telephone number, active flag and sort order.
    /// </summary>
    /// <remarks>
    /// <b>Every field is replaced, and the use case is what makes that safe.</b> This takes a complete
    /// draft rather than a set of optional fields, which is the shape that silently cleared four
    /// collections elsewhere in this project — so the <i>endpoint</i> refuses a request that does not carry
    /// every field rather than reading an absent one as null. A courier has five editable fields on one
    /// small form; partial updates buy nothing here and cost the one defect this codebase keeps repeating.
    /// </remarks>
    Task<CourierWriteResult> UpdateAsync(
        int courierId,
        CourierDraft draft,
        AdminActor actor,
        DateTime nowUtc,
        CancellationToken cancellationToken);
}

/// <summary>
/// A courier as a staff member filled the form in.
/// </summary>
/// <param name="Name">What staff and customers call them.</param>
/// <param name="TrackingUrlTemplate">
/// A URL containing <c>{trackingNumber}</c>, or <see langword="null"/> for a courier with no online
/// tracking. <b>Validated in the use case and checked again by the database</b>
/// (<c>CK_Couriers_TrackingUrlTemplate</c>), because a template without the placeholder would send every
/// customer to the courier's home page.
/// </param>
/// <param name="Phone">Who to ring about a parcel, or <see langword="null"/>.</param>
/// <param name="IsActive">Whether a new dispatch may choose it.</param>
/// <param name="SortOrder">Where it sits in the picker, lowest first.</param>
public sealed record CourierDraft(
    string Name,
    string? TrackingUrlTemplate,
    string? Phone,
    bool IsActive,
    short SortOrder);

/// <summary>What a courier write did, or why it could not.</summary>
public abstract record CourierWriteResult
{
    private CourierWriteResult()
    {
    }

    /// <summary>Committed.</summary>
    /// <param name="CourierId">The row's id, which a create has only just allocated.</param>
    public sealed record Applied(int CourierId) : CourierWriteResult;

    /// <summary>No such courier (an update aimed at a row that is not there).</summary>
    public sealed record NotFound : CourierWriteResult;

    /// <summary>
    /// Another courier already has that name.
    /// <para>
    /// <b>Detected by the unique index and not by a pre-check</b>, so two staff members adding the same
    /// carrier at once cannot both succeed. A read-then-insert would be a race with a 2627 at the end of
    /// it; catching the 2627 and answering this is the same outcome without the window.
    /// </para>
    /// </summary>
    public sealed record Duplicate(string Name) : CourierWriteResult;
}
