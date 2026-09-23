namespace SpencerDenim.Application.Abstractions.Results;

/// <summary>
/// One rejected input, named by the query-string parameter it came from, so the API can return an
/// RFC 9457 <c>errors</c> dictionary the client can attach to the right control.
/// </summary>
/// <param name="Field">The query-string parameter, e.g. <c>pageSize</c>.</param>
/// <param name="Message">A message that is safe to show to an end user.</param>
public sealed record ValidationError(string Field, string Message);
