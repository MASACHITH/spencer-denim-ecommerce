namespace SpencerDenim.Domain.Marketing;

/// <summary><c>marketing.NewsletterSubscribers.Status</c> (doc 08 §3.6).</summary>
public enum NewsletterSubscriberStatus : byte
{
    Subscribed = 1,
    Unsubscribed = 2,
}

/// <summary>
/// <c>marketing.NewsletterSubscribers.Source</c> (doc 08 §3.6, <c>varchar(30)</c>): where the
/// address was given, which is what an unsubscribe complaint is answered with.
/// </summary>
public static class NewsletterSource
{
    /// <summary>The homepage signup block (HOME-10).</summary>
    public const string Homepage = "homepage";

    /// <summary>The footer signup (GLB-07), when it is wired up.</summary>
    public const string Footer = "footer";
}
