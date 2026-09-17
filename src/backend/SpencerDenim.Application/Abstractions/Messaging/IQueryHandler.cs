namespace SpencerDenim.Application.Abstractions.Messaging;

/// <summary>
/// Handles one read-only use case. Hand-written handlers replace MediatR (commercial licence since 2025).
/// </summary>
/// <typeparam name="TQuery">The query (input) type.</typeparam>
/// <typeparam name="TResult">The result (output) type.</typeparam>
public interface IQueryHandler<in TQuery, TResult>
{
    Task<TResult> HandleAsync(TQuery query, CancellationToken cancellationToken);
}
