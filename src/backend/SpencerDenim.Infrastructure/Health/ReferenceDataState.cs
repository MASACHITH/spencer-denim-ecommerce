namespace SpencerDenim.Infrastructure.Health;

/// <summary>
/// Whether this host managed to establish doc 08 §9's reference data, shared between the seeder that
/// tries and the readiness check that reports it.
/// </summary>
/// <remarks>
/// A singleton with one writer — <c>CatalogSeedHostedService</c>, which attempts once at startup and then
/// retries on a timer — and one reader, <see cref="ReferenceDataHealthCheck"/>. The fields are
/// <see langword="volatile"/> rather than lock-guarded because there is no invariant across them that a
/// reader could catch half-applied: it reads <see cref="IsEstablished"/> first, and a stale
/// <see cref="Failure"/> is only ever shown with <see cref="IsEstablished"/> still false, which is the
/// truth at that moment.
/// </remarks>
public sealed class ReferenceDataState
{
    private volatile bool _established;
    private volatile string? _failure;

    /// <summary>True once the reference data is in the database and the shop can take an order.</summary>
    public bool IsEstablished => _established;

    /// <summary>Why the last attempt failed, or <see langword="null"/> if none has failed.</summary>
    public string? Failure => _failure;

    /// <summary>The reference data is in place. Clears any earlier failure: a retry that works is the end of it.</summary>
    public void Established()
    {
        _failure = null;
        _established = true;
    }

    /// <summary>
    /// An attempt failed. Recorded as a sentence rather than the exception, because it is served to
    /// whoever curls <c>/health/ready</c> — a stack trace belongs in the log, which already has it.
    /// </summary>
    public void Failed(string reason) => _failure = reason;
}
