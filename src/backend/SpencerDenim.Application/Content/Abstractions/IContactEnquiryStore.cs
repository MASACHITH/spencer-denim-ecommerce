using SpencerDenim.Domain.Content;

namespace SpencerDenim.Application.Content.Abstractions;

/// <summary>
/// Write port for <c>content.ContactSubmissions</c> (GLB-10).
/// <para>
/// Narrow on purpose: the public form inserts, and the admin inbox reads one, changes its status or
/// deletes it. There is no update of the enquiry's own fields, because nobody may edit what a customer
/// wrote — the only columns staff can move are <c>Status</c>, <c>HandledByUserId</c> and
/// <c>HandledAtUtc</c>, and <see cref="ContactSubmission.SetStatus"/> is the only thing that moves them.
/// </para>
/// </summary>
public interface IContactEnquiryStore
{
    /// <summary>Inserts and commits one enquiry.</summary>
    Task AddAsync(ContactSubmission submission, CancellationToken cancellationToken);

    /// <summary>One enquiry for update, or <see langword="null"/>.</summary>
    Task<ContactSubmission?> FindAsync(int submissionId, CancellationToken cancellationToken);

    /// <summary>Commits whatever the caller changed on a tracked enquiry.</summary>
    Task SaveAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Removes one enquiry <b>for good</b> — doc 08 §3.7: "it must be removed rather than merely
    /// hidden", because the case this exists for is a customer having pasted card or bank account
    /// details into the message. Set-based (<c>ExecuteDeleteAsync</c>), as doc 08 §10.2 requires of every
    /// hard delete, so nothing is tracked and nothing cascades by surprise. Answers false when the row
    /// has already gone.
    /// </summary>
    Task<bool> DeleteAsync(int submissionId, CancellationToken cancellationToken);
}

/// <summary>
/// How many enquiries one caller may submit in a window (GLB-10, doc 11 §7 G-06: "spam protection (rate
/// limit + honeypot)").
/// <para>
/// A port rather than middleware, and asked <i>after</i> validation, for the reason
/// <c>INewsletterSignupThrottle</c> gives in full: ASP.NET Core's rate limiter takes its permit before the
/// endpoint runs, so a visitor mistyping their email three times would have spent three permits — and
/// until W0-15 configures the reverse proxy's forwarded headers, every visitor shares one partition, so
/// those permits are the whole site's.
/// </para>
/// </summary>
public interface IContactEnquiryThrottle
{
    /// <summary>
    /// Takes a permit for <paramref name="partitionKey"/>, or answers false when that partition has none
    /// left. Called only for an enquiry that is about to be written.
    /// </summary>
    /// <param name="partitionKey">The caller's address, or null when there is none.</param>
    bool TryAcquire(string? partitionKey);

    /// <summary>How long a rejected caller should wait, for the <c>Retry-After</c> header.</summary>
    TimeSpan Window { get; }
}
