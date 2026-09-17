namespace SpencerDenim.Application.Diagnostics.Ping;

/// <summary>Query for the lightweight API reachability check (<c>GET /api/ping</c>).</summary>
public sealed record PingQuery;

/// <summary>Result of <see cref="PingQuery"/>.</summary>
/// <param name="Status">Always <c>ok</c> when the API process answers.</param>
/// <param name="Service">Service name.</param>
/// <param name="Version">Version of the running build.</param>
/// <param name="ServerTimeUtc">Server time in UTC.</param>
public sealed record PingResponse(string Status, string Service, string Version, DateTimeOffset ServerTimeUtc);
