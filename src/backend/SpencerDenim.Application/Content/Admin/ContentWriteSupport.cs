namespace SpencerDenim.Application.Content.Admin;

/// <summary>
/// The one decision every ADM-17 content write makes the same way: is the caller's row version current.
/// <para>
/// Deliberately a second copy of <c>CatalogWriteSupport.CheckRowVersion</c>'s <i>logic</i> rather than a
/// call into it: that type is <c>internal</c> to the catalogue module and returns a
/// <c>CatalogWriteResult</c>, so sharing it would mean either widening its visibility or making this
/// module's refusals carry the catalogue's result type. The duplicated part is six lines of base64
/// comparison; the part worth sharing — <i>why</i> a missing token is refused rather than ignored — is
/// stated in both places because it is the part a future reader would otherwise undo.
/// </para>
/// </summary>
internal static class ContentWriteSupport
{
    /// <summary>
    /// A conflict when the caller's token is not the row's current one, otherwise <see langword="null"/>.
    /// <para>
    /// This is the <i>first</i> of two concurrency checks and it earns its place: it turns "somebody else
    /// edited this while your form was open" into a clear answer before anything is written, where the
    /// <c>RowVersion</c> predicate EF puts on the UPDATE can only fire for a change that lands between the
    /// handler's read and its save. Keeping both means the common case gets the good message and the
    /// narrow race still cannot overwrite anybody.
    /// </para>
    /// <para>
    /// <b>A missing or malformed token is refused, not taken as "no opinion".</b> A client that did not
    /// send one cannot have seen the current row, and silently allowing that write is how optimistic
    /// concurrency becomes decorative. Doc 11 P13's own edge case is this: "Two editors save the same page
    /// → the second is told it changed and must reload (row version)."
    /// </para>
    /// </summary>
    public static ContentWriteResult.Conflict? CheckRowVersion(byte[]? current, string? supplied)
    {
        var expected = ReadRowVersion(supplied);

        if (expected is not null && current is not null && expected.AsSpan().SequenceEqual(current))
        {
            return null;
        }

        return new ContentWriteResult.Conflict(
            ContentConflictCodes.StaleRowVersion,
            expected is null
                ? "This request did not carry the page's current version, so it cannot be applied safely. "
                  + "Reload the page and try again."
                : "Somebody else changed this page while you were editing it. Reload the page to see their "
                  + "change, then re-apply yours.");
    }

    /// <summary>
    /// A base64 <c>rowversion</c> as bytes, or <see langword="null"/> when it is absent or not base64.
    /// Sixty-four bytes of stack is ample for an eight-byte token and bounds a caller-supplied string.
    /// </summary>
    private static byte[]? ReadRowVersion(string? base64)
    {
        if (string.IsNullOrWhiteSpace(base64))
        {
            return null;
        }

        Span<byte> buffer = stackalloc byte[64];

        return Convert.TryFromBase64String(base64, buffer, out var written) && written > 0
            ? buffer[..written].ToArray()
            : null;
    }
}
