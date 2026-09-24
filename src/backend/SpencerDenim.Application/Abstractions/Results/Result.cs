using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;

namespace SpencerDenim.Application.Abstractions.Results;

/// <summary>
/// The outcome of a use case that can fail validation: either a value, or the list of reasons it was
/// rejected. Use cases never throw for bad input, so the API maps failures to one 400 with all of the
/// problems at once instead of the first.
/// </summary>
/// <typeparam name="TValue">The success value.</typeparam>
public sealed class Result<TValue>
{
    private static readonly ReadOnlyCollection<ValidationError> NoErrors =
        new List<ValidationError>().AsReadOnly();

    private readonly TValue? _value;

    internal Result(TValue value)
    {
        _value = value;
        Errors = NoErrors;
    }

    internal Result(IReadOnlyList<ValidationError> errors)
    {
        ArgumentNullException.ThrowIfNull(errors);

        if (errors.Count == 0)
        {
            throw new ArgumentException("A failed result needs at least one error.", nameof(errors));
        }

        Errors = errors;
    }

    [MemberNotNullWhen(true, nameof(_value))]
    public bool IsSuccess => Errors.Count == 0;

    public IReadOnlyList<ValidationError> Errors { get; }

    /// <summary>The value. Only read it after checking <see cref="IsSuccess"/>.</summary>
    public TValue Value => IsSuccess
        ? _value
        : throw new InvalidOperationException("The result failed validation; read Errors instead.");
}

/// <summary>
/// Factory methods for <see cref="Result{TValue}"/>. They live on a non-generic type so callers write
/// <c>Result.Success(query)</c> and let the compiler infer the type argument.
/// </summary>
public static class Result
{
    public static Result<TValue> Success<TValue>(TValue value) => new(value);

    public static Result<TValue> Failure<TValue>(IReadOnlyList<ValidationError> errors) => new(errors);

    public static Result<TValue> Failure<TValue>(string field, string message) =>
        new(new List<ValidationError> { new(field, message) }.AsReadOnly());
}
