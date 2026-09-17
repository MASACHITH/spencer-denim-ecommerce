namespace SpencerDenim.Domain.Common;

/// <summary>
/// The soft-delete columns and the only transitions allowed on them, shared by every
/// <see cref="ISoftDeletable"/> entity so the rule lives in one place.
/// </summary>
public sealed class SoftDeleteState
{
    public bool IsDeleted { get; private set; }

    public DateTime? DeletedAtUtc { get; private set; }

    public int? DeletedByUserId { get; private set; }

    public void Delete(DateTime utcNow, int? userId)
    {
        if (IsDeleted)
        {
            return;
        }

        IsDeleted = true;
        DeletedAtUtc = utcNow;
        DeletedByUserId = userId;
    }

    public void Restore()
    {
        IsDeleted = false;
        DeletedAtUtc = null;
        DeletedByUserId = null;
    }
}
