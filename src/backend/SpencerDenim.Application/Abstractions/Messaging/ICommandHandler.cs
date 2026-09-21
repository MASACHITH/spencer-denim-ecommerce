namespace SpencerDenim.Application.Abstractions.Messaging;

/// <summary>
/// Handles one state-changing use case. Hand-written handlers replace MediatR (commercial licence since 2025),
/// mirroring <see cref="IQueryHandler{TQuery,TResult}"/> on the write side.
/// </summary>
/// <typeparam name="TCommand">The command (input) type.</typeparam>
/// <typeparam name="TResult">The result (output) type.</typeparam>
public interface ICommandHandler<in TCommand, TResult>
{
    Task<TResult> HandleAsync(TCommand command, CancellationToken cancellationToken);
}
