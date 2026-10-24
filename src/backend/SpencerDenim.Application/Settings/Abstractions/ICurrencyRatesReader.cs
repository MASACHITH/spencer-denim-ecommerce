namespace SpencerDenim.Application.Settings.Abstractions;

/// <summary>
/// One display-rate row as stored, before anything has been decided about it.
/// </summary>
/// <param name="Key">The <c>Currency.Display.*</c> key.</param>
/// <param name="Value">The stored text, or <see langword="null"/> when the rate has not been set.</param>
/// <param name="UpdatedAtUtc">The row's own <c>UpdatedAtUtc</c>.</param>
public sealed record StoredCurrencyRate(string Key, string? Value, DateTime? UpdatedAtUtc);

/// <summary>
/// Read port for the five display rates (issue #248). Its own port, like <see cref="ISiteSettingsReader"/>, because
/// it is anonymous and read-only: it has no method that could reach a secret, a permission or any other key.
/// <para>
/// It hands back the rows <b>unjudged</b>. Whether a stored value is a usable rate is a rule
/// (<c>CurrencyRateRules</c>) that the save also applies, so it lives in the use case where it can be tested
/// without a database, not in the query.
/// </para>
/// </summary>
public interface ICurrencyRatesReader
{
    /// <summary>
    /// The rows of the <c>Currency.Display</c> group that exist, in no particular order. A row that does not exist
    /// is simply absent. A broken database connection throws, and the endpoint decides what the page does about it.
    /// </summary>
    Task<IReadOnlyList<StoredCurrencyRate>> ReadAsync(CancellationToken cancellationToken);
}
