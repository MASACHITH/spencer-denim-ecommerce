namespace SpencerDenim.Application.Abstractions.Auditing;

/// <summary>
/// Who is making an admin write, and from where — everything <c>ops.AuditLogs</c> records about the actor
/// (doc 08 §3.8), gathered once at the endpoint and carried on the command.
/// <para>
/// It is a parameter rather than something a handler reaches for, because the Application layer has no
/// <c>HttpContext</c> to reach into and should not grow one. The same shape also makes the handlers'
/// audit assertions testable without a web host.
/// </para>
/// </summary>
/// <param name="UserId">
/// <c>identity.Users.Id</c> of the signed-in staff user, or <c>null</c> when a command runs outside a
/// request (a CLI verb, a seeder). Doc 08 §1.5 allows the audit columns to be NULL.
/// </param>
/// <param name="Email">A snapshot of their email, because a later rename must not rewrite history.</param>
public sealed record AdminActor(
    int? UserId,
    string? Email,
    string? IpAddress,
    string? UserAgent,
    string? CorrelationId)
{
    /// <summary>An actor nothing is known about — used by tests and by non-request callers.</summary>
    public static AdminActor Unknown { get; } = new(null, null, null, null, null);
}
