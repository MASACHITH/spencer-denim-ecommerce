namespace SpencerDenim.Domain.Marketing;

/// <summary>
/// <c>marketing.NewsletterSubscribers</c> (doc 08 §3.6; HOME-10, GLB-07, ADM-21).
/// <para>
/// Single opt-in, because that is the shape doc 08 gives it: the status column has two values and
/// no "pending confirmation". A double opt-in would need a third status and the email outbox of
/// §3.8, neither of which exists yet.
/// </para>
/// <para>
/// The table is not marked [A] in doc 08, so it carries <c>CreatedAtUtc</c> alone: a subscriber row
/// is written by the visitor, not edited by staff.
/// </para>
/// </summary>
public sealed class NewsletterSubscriber
{
    public int Id { get; set; }

    /// <summary>As the visitor typed it, trimmed.</summary>
    public string Email { get; set; } = string.Empty;

    /// <summary>Upper-case form behind <c>UX_NewsletterSubscribers_EmailNormalized</c>.</summary>
    public string EmailNormalized { get; set; } = string.Empty;

    /// <summary>The customer account, when the address belongs to one. No account is needed.</summary>
    public int? UserId { get; set; }

    public NewsletterSubscriberStatus Status { get; set; } = NewsletterSubscriberStatus.Subscribed;

    /// <summary>Where the address was given (<see cref="NewsletterSource"/>).</summary>
    public string Source { get; set; } = NewsletterSource.Homepage;

    public DateTime ConsentAtUtc { get; set; }

    public string? ConsentIpAddress { get; set; }

    /// <summary>The unguessable token in the unsubscribe link (doc 08 §1.3).</summary>
    public Guid UnsubscribeToken { get; set; }

    public DateTime? UnsubscribedAtUtc { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    /// <summary>
    /// Records a fresh consent on an existing row. Re-subscribing after an unsubscribe is a new
    /// consent, so the time, the source and the IP are all replaced and the unsubscribe is cleared.
    /// </summary>
    public void Resubscribe(string source, DateTime utcNow, string? ipAddress)
    {
        Status = NewsletterSubscriberStatus.Subscribed;
        Source = source;
        ConsentAtUtc = utcNow;
        ConsentIpAddress = ipAddress;
        UnsubscribedAtUtc = null;
    }
}
