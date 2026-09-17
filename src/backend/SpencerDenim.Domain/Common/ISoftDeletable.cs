namespace SpencerDenim.Domain.Common;

/// <summary>
/// Soft-delete columns from doc 08 §1.5 (tables marked <c>[SD]</c>).
/// Doc 08 §10.2 (DB5): a row is only ever soft-deleted through <see cref="SoftDelete"/>. The
/// <c>SaveChanges</c> interceptor throws when such an entity is tracked in state <c>Deleted</c>, so
/// EF never cascades a hard delete to children behind our back.
/// </summary>
public interface ISoftDeletable
{
    bool IsDeleted { get; }

    DateTime? DeletedAtUtc { get; }

    int? DeletedByUserId { get; }

    /// <summary>Marks the row deleted. Calling it twice is a no-op, so it is safe to retry.</summary>
    void SoftDelete(DateTime utcNow, int? userId);

    /// <summary>Clears the soft delete (admin "restore").</summary>
    void Restore();
}
