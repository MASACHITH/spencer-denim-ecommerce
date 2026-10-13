using SpencerDenim.Application.Sales.Payments.Abstractions;
using SpencerDenim.Application.Sales.Payments.Contracts;
using SpencerDenim.Domain.Sales;

namespace SpencerDenim.UnitTests.Support;

/// <summary>
/// A registry holding one controllable <see cref="IPaymentProvider"/>, for the checkout tests (PAY-03).
/// </summary>
/// <remarks>
/// <b>It is a stub rather than the real <c>PaymentProviderRegistry</c> over the real
/// <c>BankTransferProvider</c>, because the real provider reads <c>ops.Settings</c>.</b> A unit test of
/// checkout's own logic should not need a database to find out what a provider says — and the point of
/// PAY-03 is that checkout does not care what it says.
/// </remarks>
internal sealed class StubPaymentProviderRegistry(IPaymentProvider? provider = null) : IPaymentProviderRegistry
{
    private readonly IPaymentProvider _provider = provider ?? new StubPaymentProvider();

    public IReadOnlyList<string> ImplementedCodes => [_provider.Code];

    /// <summary>
    /// Case-insensitive, like the real one — so a test that uses the wrong casing passes here and in
    /// production alike rather than only here.
    /// </summary>
    public IPaymentProvider? Find(string? code) =>
        code is not null && string.Equals(code.Trim(), _provider.Code, StringComparison.OrdinalIgnoreCase)
            ? _provider
            : null;
}

/// <summary>A provider that answers whatever a test told it to, and records that it was asked.</summary>
internal sealed class StubPaymentProvider(
    string? code = null,
    PaymentStatus status = PaymentStatus.Pending,
    TimeSpan? window = null,
    PaymentInstructions? instructions = null) : IPaymentProvider
{
    private readonly TimeSpan _window = window ?? TimeSpan.FromHours(48);

    public string Code { get; } = code ?? PaymentProviderCodes.BankTransfer;

    /// <summary>How many times checkout asked for an initiation. Zero proves it refused before asking.</summary>
    public int Initiations { get; private set; }

    public ValueTask<PaymentInitiation> InitiateAsync(
        PaymentInitiationRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        Initiations++;

        return ValueTask.FromResult(new PaymentInitiation(status, request.PlacedAtUtc + _window));
    }

    /// <summary>Every request checkout or the order page made, in order, so a test can assert what was asked.</summary>
    public List<PaymentInstructionsRequest> DescribeRequests { get; } = [];

    public Task<PaymentInstructions?> DescribeAsync(
        PaymentInstructionsRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        DescribeRequests.Add(request);

        return Task.FromResult(instructions);
    }
}
