using Microsoft.Extensions.Logging;

using SpencerDenim.Application.Sales.Payments;
using SpencerDenim.Application.Sales.Payments.Abstractions;
using SpencerDenim.Application.Sales.Payments.Contracts;
using SpencerDenim.Domain.Sales;

namespace SpencerDenim.Infrastructure.Sales.Payments;

/// <summary>
/// PAY-01: bank / online transfer, the client's primary method and the first
/// <see cref="IPaymentProvider"/> (doc 03 §3c, doc 11 P7).
/// <para>
/// <b>It takes no money and calls nothing.</b> The customer transfers at their bank, outside the system,
/// and a staff member with <c>Orders.Approve</c> confirms it against the statement. So this class's whole
/// job is the two questions PAY-03 asks of a provider: what the opening payment row looks like, and what
/// to tell the customer. Approving and rejecting are not here — they are not operations any gateway has,
/// and <c>IPaymentProvider</c> deliberately does not declare them (see that interface).
/// </para>
/// </summary>
internal sealed partial class BankTransferProvider(
    IBankTransferSettingsReader settings,
    BankTransferOptions options,
    ILogger<BankTransferProvider> logger) : IPaymentProvider
{
    public string Code => PaymentProviderCodes.BankTransfer;

    /// <summary>
    /// Doc 08 §4.1's bank-transfer placement effects: an open <see cref="PaymentStatus.Pending"/> row in
    /// LKR, due at placement + <c>Orders.BankTransferExpiryHours</c> (48).
    /// </summary>
    /// <remarks>
    /// <b>No provider currency, amount, rate or buffer</b>, and they are left null rather than filled with
    /// the LKR figures. A bank transfer charges in the order's own currency, so <c>ProviderAmount</c>
    /// duplicating <c>Amount</c> would make "this payment was converted" indistinguishable from "it was
    /// not", and doc 08 §4.2 step 3.1 compares a captured amount against <c>ProviderAmount</c> — a column
    /// that must be null for anything that was never converted.
    /// <para>
    /// <b>The window is read per call, not captured at construction.</b> It is configuration today
    /// (<see cref="BankTransferOptions"/>) and an <c>ops.Settings</c> key tomorrow; reading it here means
    /// the move is a change to the port behind this line and not to its callers.
    /// </para>
    /// </remarks>
    public ValueTask<PaymentInitiation> InitiateAsync(
        PaymentInitiationRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        return ValueTask.FromResult(new PaymentInitiation(
            PaymentStatus.Pending,
            request.PlacedAtUtc.AddHours(options.BankTransferExpiryHours)));
    }

    /// <summary>
    /// Doc 11 P7 step 1's list, minus the email nothing can send yet: the bank details, the exact amount,
    /// <b>the order number to quote as the reference</b>, the due date and the order link.
    /// </summary>
    /// <remarks>
    /// <b>The reference is the order number and nothing else.</b> It is the only thing that ties an
    /// anonymous credit on a bank statement to an order — P7 step 5 has staff matching "amount and
    /// reference" — so it is taken from the order rather than generated, and the copy button beside it
    /// exists because a shopper retypes it into online banking.
    /// <para>
    /// <b>Null when the details are not set, and the three required ones are checked by
    /// <see cref="BankTransferDetails"/> itself.</b> Doc 11 P7 makes "bank details set by a Super Admin" a
    /// precondition; a shop can be deployed before the client sends them, and in that window a shopper
    /// must be shown that the details are coming rather than a form with an empty account number.
    /// </para>
    /// </remarks>
    public async Task<PaymentInstructions?> DescribeAsync(
        PaymentInstructionsRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (await settings.ReadAsync(cancellationToken) is not { } bank)
        {
            // **Warned, not only degraded.** Doc 08 §3.5 says enabling a provider requires valid settings
            // and nothing enforces it, so a fresh deployment happily takes bank-transfer orders that
            // nobody can pay: the order page says the details are coming, which is honest to the shopper
            // and silent to everybody who could fix it. There is no ops.AdminAlerts table in this build
            // (it is not this task's), so the log is the channel there is — at Warning, naming the keys,
            // on the one code path that proves a real customer has hit it.
            LogDetailsNotSet(logger, request.OrderNumber);
            return null;
        }

        var fields = new List<PaymentInstructionField>(4)
        {
            new("Bank", bank.BankName),
        };

        if (bank.Branch is { } branch)
        {
            fields.Add(new PaymentInstructionField("Branch", branch));
        }

        fields.Add(new PaymentInstructionField("Account name", bank.AccountName));

        // Copyable: the one value where a transposed digit sends real money somewhere else.
        fields.Add(new PaymentInstructionField("Account number", bank.AccountNumber, Copyable: true));

        return new PaymentInstructions(
            Code,
            request.OrderNumber,
            request.AmountLkr,
            request.PaymentDueAtUtc,
            fields,
            bank.Instructions,

            // A bank transfer never redirects anywhere: the customer leaves for their own bank's site or a
            // branch, which is not a URL we can know or should guess at.
            RedirectUrl: null,

            // Doc 11 P7 step 3: the reference and the slip are optional and this is the provider that
            // takes them.
            AcceptsProof: true);
    }

    [LoggerMessage(
        EventId = 3305,
        Level = LogLevel.Warning,
        Message = "Order {OrderNumber} is awaiting a bank transfer, but the shop's bank details are not "
            + "set, so the customer has been told the details are still coming and cannot pay. Fill in "
            + "Settings.Payments: Bank.BankName, Bank.AccountName and Bank.AccountNumber (doc 08 §3.5, "
            + "doc 11 P7's precondition).")]
    private static partial void LogDetailsNotSet(ILogger logger, string orderNumber);
}
