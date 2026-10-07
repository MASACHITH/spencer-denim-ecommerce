using SpencerDenim.Application.Abstractions.Messaging;
using SpencerDenim.Application.Abstractions.Results;
using SpencerDenim.Application.Abstractions.Time;
using SpencerDenim.Application.Sales.Payments.Abstractions;
using SpencerDenim.Domain.Sales;

namespace SpencerDenim.Application.Sales.Payments.Customer;

/// <summary>
/// The bank reference a customer quotes for their transfer (doc 11 P7 step 3).
/// </summary>
/// <param name="OrderPublicId">
/// The order's token — a guest's only credential, and the only way to reach this (doc 08 §3.3).
/// </param>
/// <param name="Reference">
/// What their bank gave them. <see langword="null"/> or blank clears it, which is a real action: a customer
/// who mistyped it should be able to remove it rather than leave a wrong reference for staff to match
/// against.
/// </param>
public sealed record SubmitBankTransferReferenceCommand(Guid OrderPublicId, string? Reference);

/// <summary>
/// Writes the customer's reference on to the order's open bank payment.
/// </summary>
/// <remarks>
/// <b>It writes to the order's open row, and opens one when there is none — which is the fix to a defect
/// the first version shipped.</b> That version required an open row and answered 404 without one, with a
/// remark arguing that a reference only ever corrects an attempt in progress. Doc 11 P7 step 6b says
/// otherwise: after a rejection the order <i>stays</i> <c>PendingPayment</c> and the customer may transfer
/// again. There is no open row then, so the shopper saw the pay panel, an empty reference box, and
/// <i>"This order can no longer be paid"</i> when they pressed Save — about an order that was perfectly
/// payable, with no email in this build to explain any of it. The slip upload opened a row in exactly that
/// situation and the reference did not, which made the two halves of one panel disagree.
/// <para>
/// So <b>storing</b> a reference goes through <see cref="IBankTransferProofStore.OpenPaymentAsync"/>, which
/// is still doc 08 §3.5 N7's rule — "while a bank payment is Pending, a new reference updates that record;
/// a new record is created only after a rejection" — and still one open row, because that method's whole job
/// is to answer with the single open row, creating it only when none exists and treating a concurrent
/// insert's 2627 as expected.
/// </para>
/// <para>
/// <b>Clearing is a separate branch, and the first version did not have one — it created a phantom
/// payment.</b> <see cref="IBankTransferProofStore.OpenPaymentAsync"/> was called unconditionally, so a shopper pressing Save on the empty box
/// after a rejection — by reflex, or to take back a typo — opened a <c>Pending</c> row with no reference and
/// no slip, which <b>put the order back in the staff queue</b> showing "None given / None uploaded". Staff
/// then checked the statement, found nothing, and rejected it again. There was nothing to clear and nothing
/// to look for; the row was pure noise aimed at the people doing the money. So a null reference now reads
/// the order and its open row instead: it clears the row if there is one, answers success if there is none
/// (nothing to clear is not a failure), and still answers 404 when the order cannot be paid at all —
/// because "saved" about a paid or expired order would be a lie whichever value it carried.
/// </para>
/// <para>
/// <b>No throttle here, and that is deliberate.</b> Doc 08 §3.3 N10 rate-limits <i>uploads</i>, because an
/// upload costs disk and a row. This route writes one short column, and the only row it can create is the
/// single open payment the order is entitled to — <c>UX_Payments_OneOpen</c> caps it at that, and a
/// repeated save after that rewrites the same column. What protects it beyond that is that the only way in
/// is the order's unguessable token, and the only orders it can touch are ones already awaiting payment.
/// Doc 11 §7 G-41 asks for the order <i>link</i> to be rate-limited, which would cover this route too and
/// is an architect decision rather than this task's.
/// </para>
/// </remarks>
public sealed class SubmitBankTransferReferenceCommandHandler(
    IBankTransferReader reader,
    IBankTransferProofStore store,
    IClock clock)
    : ICommandHandler<SubmitBankTransferReferenceCommand, SubmitBankTransferReferenceResult>
{
    /// <summary>
    /// <c>Payments.PayerReference</c> is <c>nvarchar(100)</c> (doc 08 §3.5).
    /// </summary>
    public const int ReferenceMaxLength = 100;

    public async Task<SubmitBankTransferReferenceResult> HandleAsync(
        SubmitBankTransferReferenceCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var reference = string.IsNullOrWhiteSpace(command.Reference) ? null : command.Reference.Trim();

        if (reference is { Length: > ReferenceMaxLength })
        {
            return new SubmitBankTransferReferenceResult.Invalid(
            [
                new ValidationError(
                    "reference",
                    $"A bank reference cannot be longer than {ReferenceMaxLength} characters."),
            ]);
        }

        // A control character here would travel into the staff queue's HTML and into whatever a staff
        // member pastes it into. Line breaks included: a reference is one line.
        if (reference is not null && reference.Any(char.IsControl))
        {
            return new SubmitBankTransferReferenceResult.Invalid(
            [
                new ValidationError(
                    "reference",
                    "That reference contains characters we cannot read. Type it in rather than pasting it."),
            ]);
        }

        var nowUtc = clock.UtcNow.UtcDateTime;

        int paymentId;

        if (reference is null)
        {
            // **Clearing never opens a payment**, for the reason in this class's remarks: a row with no
            // reference and no slip is a phantom in the staff queue.
            if (await reader.FindTargetAsync(command.OrderPublicId, cancellationToken)
                is not { OrderStatus: OrderStatus.PendingPayment })
            {
                // No such bank-transfer order, or one that can no longer be paid. Answering "cleared"
                // here would tell a customer their order is still open when it is not.
                return new SubmitBankTransferReferenceResult.NotFound();
            }

            if (await reader.FindOpenPaymentAsync(command.OrderPublicId, cancellationToken)
                is not { } open)
            {
                // Nothing to clear — after a rejection there is no open attempt — and that is a success,
                // not a failure: what the customer asked for is already true.
                return new SubmitBankTransferReferenceResult.Saved(null);
            }

            paymentId = open.PaymentId;
        }
        else
        {
            // The open row, or a new one when the last attempt was rejected — the same call the slip
            // upload makes, so one panel cannot accept a slip and refuse a reference about the same order.
            //
            // null means the order is not PendingPayment: paid, expired, cancelled or not an order at all.
            // That is the only state in which "this order can no longer be paid" is true.
            if (await store.OpenPaymentAsync(command.OrderPublicId, nowUtc, cancellationToken)
                is not { } opened)
            {
                return new SubmitBankTransferReferenceResult.NotFound();
            }

            paymentId = opened;
        }

        var written = await store.SetReferenceAsync(
            paymentId,
            reference,
            nowUtc,
            cancellationToken);

        // False means the payment stopped being Pending between the read above and the statement — a staff
        // member approved or rejected it, or the expiry job ran. Reporting success would tell the customer
        // their reference is with the shop when it is not.
        return written
            ? new SubmitBankTransferReferenceResult.Saved(reference)
            : new SubmitBankTransferReferenceResult.NotFound();
    }
}

/// <summary>What the API should answer.</summary>
public abstract record SubmitBankTransferReferenceResult
{
    private SubmitBankTransferReferenceResult()
    {
    }

    /// <summary>Written. 200, echoing the stored value so the page shows what was kept.</summary>
    public sealed record Saved(string? Reference) : SubmitBankTransferReferenceResult;

    /// <summary>
    /// No such order, or it can no longer be paid. <b>One answer for both</b>, and a bare 404: the token
    /// never expires (doc 11 §7 G-41) and is a guest's only credential, so a stranger holding a guessed
    /// GUID must not learn that it names a real order which happens to be paid.
    /// </summary>
    public sealed record NotFound : SubmitBankTransferReferenceResult;

    /// <summary>400, naming the field.</summary>
    public sealed record Invalid(IReadOnlyList<ValidationError> Errors) : SubmitBankTransferReferenceResult;
}
