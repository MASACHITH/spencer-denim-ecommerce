namespace SpencerDenim.Domain.Common;

/// <summary>
/// Audit columns from doc 08 §1.5 (tables marked <c>[A]</c>). The values are written by the
/// <c>SaveChanges</c> interceptor, never by callers. There is no FK on the user columns, because
/// users are never hard-deleted.
/// </summary>
public interface IAuditable
{
    DateTime CreatedAtUtc { get; set; }

    int? CreatedByUserId { get; set; }

    DateTime? UpdatedAtUtc { get; set; }

    int? UpdatedByUserId { get; set; }
}
