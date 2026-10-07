using SpencerDenim.Application.Abstractions.Messaging;
using SpencerDenim.Application.Abstractions.Results;
using SpencerDenim.Application.Abstractions.Time;
using SpencerDenim.Application.Sales.Payments.Abstractions;

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
/// <b>It updates the open row and never creates one.</b> Doc 08 §3.5 N7 is explicit, and the difference
/// matters: a reference is a correction to an attempt in progress, so creating a second <c>Pending</c> row
/// for it would both violate <c>UX_Payments_OneOpen</c> and tell staff there were two transfers to look
/// for. Opening a row after a rejection is the <i>slip upload's</i> job, where the customer is submitting
/// new evidence rather than editing old evidence — see <see cref="IBankTransferProofStore.OpenPaymentAsync"/>.
/// <para>
/// <b>No throttle here, and that is deliberate.</b> Doc 08 §3.3 N10 rate-limits <i>uploads</i>, because an
/// upload costs disk and a row; a reference is one short column on a row that already exists, and rewriting
/// it a hundred times costs nothing a sane limit would be protecting. What does protect it is that the only
/// way in is the order's unguessable token, and the only orders it can touch are ones already awaiting
/// payment. Doc 11 §7 G-41 asks for the order <i>link</i> to be rate-limited, which would cover this route
/// too and is an architect decision rather than this task's.
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

        if (await reader.FindOpenPaymentAsync(command.OrderPublicId, cancellationToken) is not { } payment)
        {
            return new SubmitBankTransferReferenceResult.NotFound();
        }

        var written = await store.SetReferenceAsync(
            payment.PaymentId,
            reference,
            clock.UtcNow.UtcDateTime,
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
