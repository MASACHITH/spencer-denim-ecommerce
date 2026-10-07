namespace SpencerDenim.Application.Sales.Payments.Abstractions;

/// <summary>
/// Doc 08 §3.3 N10's upload limit: "5 per hour per order and per IP address".
/// <para>
/// Doc 11 P7 spells out what each half is for — "the per-order limit stops one customer flooding their own
/// order, the per-IP limit stops a script walking order links" — and they are genuinely different
/// defences, so both are asked.
/// </para>
/// <para>
/// A port rather than ASP.NET Core's rate-limiting middleware, for the reason
/// <c>INewsletterSignupThrottle</c> gives: <b>when</b> the permit is taken matters, and only the use case
/// knows. The middleware takes it before the endpoint runs, so an upload refused for being a 6 MB video
/// would have spent a permit the customer needs for the slip they are about to choose correctly. The order
/// here is: find the order, check the file, then take the permits, then store.
/// </para>
/// </summary>
public interface IPaymentSlipUploadThrottle
{
    /// <summary>
    /// Takes one permit for this order and one for this address, or answers which of them is exhausted.
    /// </summary>
    /// <param name="orderPublicId">The order's token, which is the per-order partition.</param>
    /// <param name="ipAddress">The caller's address, or null when there is none.</param>
    SlipUploadPermit TryAcquire(Guid orderPublicId, string? ipAddress);

    /// <summary>How long a refused caller should wait, for the <c>Retry-After</c> header.</summary>
    TimeSpan Window { get; }
}

/// <summary>Whether an upload may proceed, and which limit refused it.</summary>
public enum SlipUploadPermit
{
    /// <summary>Go ahead.</summary>
    Granted,

    /// <summary>
    /// This order has had its five uploads this hour. The message can be specific, because the customer
    /// knows what they have been doing.
    /// </summary>
    OrderLimitReached,

    /// <summary>
    /// This address has. The message stays vague: a script learns nothing from being told which limit it
    /// hit, and a genuine customer behind a shared connection is better served by "try again shortly" than
    /// by a sentence about other people's orders.
    /// </summary>
    AddressLimitReached,
}
