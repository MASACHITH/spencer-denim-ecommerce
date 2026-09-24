namespace SpencerDenim.Application.Marketing.Newsletter;

/// <summary>
/// How many signups one caller may make in a window (HOME-10, GLB-07).
/// <para>
/// A port rather than middleware, because <b>when</b> it is asked matters and only the use case
/// knows: an address this store will not even store must not consume anybody's budget. ASP.NET
/// Core's rate limiter takes its permit before the endpoint runs, so ten typos would have spent
/// ten permits and — behind a reverse proxy, where every visitor shares one partition — locked
/// the form for everyone else. The order here is validate, then take a permit, then write.
/// </para>
/// </summary>
public interface INewsletterSignupThrottle
{
    /// <summary>
    /// Takes a permit for <paramref name="partitionKey"/>, or answers <c>false</c> when that
    /// partition has none left. Called only for an address that is about to be written.
    /// </summary>
    /// <param name="partitionKey">The caller's address, or null when there is none.</param>
    bool TryAcquire(string? partitionKey);

    /// <summary>How long a rejected caller should wait, for the <c>Retry-After</c> header.</summary>
    TimeSpan Window { get; }
}
