using SpencerDenim.Domain.Common;

namespace SpencerDenim.Domain.Sales;

/// <summary>
/// <c>sales.Couriers</c> (doc 08 §3.5, ADM-09/ADM-22/ACC-02) [A]: who carries a shipped order.
/// </summary>
public sealed class Courier : IAuditable
{
    /// <summary>
    /// The placeholder <see cref="TrackingUrlTemplate"/> must contain, checked in the database.
    /// <para>
    /// Doc 08 §3.5 says tracking links are "rendered from the current template" — the order keeps
    /// <c>CourierName</c> and <c>TrackingNumber</c>, never a built URL. A template with no placeholder
    /// would therefore send every customer to the courier's home page, and the CK is what stops that
    /// from being saved in the first place.
    /// </para>
    /// </summary>
    public const string TrackingNumberPlaceholder = "{trackingNumber}";

    public int Id { get; set; }

    /// <summary>Unique: two couriers with one name would make an order list ambiguous.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// A URL containing <see cref="TrackingNumberPlaceholder"/>, or <see langword="null"/> for a courier
    /// with no online tracking (then the number is shown as text).
    /// </summary>
    public string? TrackingUrlTemplate { get; set; }

    public string? Phone { get; set; }

    public bool IsActive { get; set; } = true;

    public short SortOrder { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public int? CreatedByUserId { get; set; }

    public DateTime? UpdatedAtUtc { get; set; }

    public int? UpdatedByUserId { get; set; }
}
