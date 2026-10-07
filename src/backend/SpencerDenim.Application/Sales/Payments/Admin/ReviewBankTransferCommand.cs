using SpencerDenim.Application.Abstractions.Auditing;
using SpencerDenim.Application.Abstractions.Messaging;
using SpencerDenim.Application.Abstractions.Results;
using SpencerDenim.Application.Abstractions.Time;
using SpencerDenim.Application.Sales.Payments.Abstractions;

namespace SpencerDenim.Application.Sales.Payments.Admin;

/// <summary>Which of doc 11 P7's three staff decisions this is.</summary>
public enum BankTransferDecisionKind
{
    /// <summary>P7 step 6a: the credit is on the statement and the amount matches.</summary>
    Approve,

    /// <summary>P7 step 6b: no credit, the wrong amount, or an unreadable slip.</summary>
    Reject,

    /// <summary>P7 step 8: the money arrived after the order expired.</summary>
    AcceptLate,
}

/// <summary>
/// One staff decision about one order's bank transfer (doc 11 P7 steps 6 and 8; <c>Orders.Approve</c>).
/// </summary>
/// <param name="OrderPublicId">
/// The order's public token, which is what the admin screen and every link carry. <b>Not a payment id</b>
/// — see <see cref="IBankTransferReviewStore"/> for why a payment id on the wire is a stale value.
/// </param>
/// <param name="Kind">Which decision.</param>
/// <param name="Note">
/// The staff note. Optional for an approval and a late acceptance, <b>required</b> for a rejection: P7
/// step 6b says "enters a reason", and the reason is the only record of why money a customer believes they
/// sent was refused.
/// </param>
/// <param name="Actor">Who is deciding, for the history row and the audit row.</param>
public sealed record ReviewBankTransferCommand(
    Guid OrderPublicId,
    BankTransferDecisionKind Kind,
    string? Note,
    AdminActor Actor);

/// <summary>
/// Validates a staff decision and hands it to the one transaction that applies it.
/// </summary>
/// <remarks>
/// <b>One handler for three decisions, because the validation is one question and the differences are all
/// inside the transaction.</b> Splitting them would mean three copies of "is the note required", three
/// copies of the token-to-id lookup and three places for those to drift; the effects that genuinely differ
/// — which states the claim allows, whether stock is deducted or re-acquired, whether an invoice number is
/// issued — are statements in <see cref="IBankTransferReviewStore"/>, where they can be read side by side.
/// <para>
/// <b>What this handler does not do is check the permission.</b> <c>Orders.Approve</c> is enforced by the
/// endpoint's policy against the user's <i>effective</i> permissions (doc 03 §3a, doc 08 §3.1 R4), which is
/// the security boundary. A second check here would be a second place to get it wrong, and the settings
/// handlers' per-row check exists only because a settings row carries its own permission in a column.
/// </para>
/// </remarks>
public sealed class ReviewBankTransferCommandHandler(
    IBankTransferReader reader,
    IBankTransferReviewStore store,
    IClock clock)
    : ICommandHandler<ReviewBankTransferCommand, ReviewBankTransferResult>
{
    /// <summary>
    /// <c>Payments.ApprovalNote</c> is <c>nvarchar(500)</c> (doc 08 §3.5).
    /// <para>
    /// Refused rather than clipped, unlike the history row's note: this value is a staff member's stated
    /// reason and the thing a customer may be read back over the phone, so silently losing its last
    /// sentence would be worse than asking them to shorten it. The history row clips because it is a
    /// derived sentence with a prefix, not the reason itself.
    /// </para>
    /// </summary>
    public const int NoteMaxLength = 500;

    public async Task<ReviewBankTransferResult> HandleAsync(
        ReviewBankTransferCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var note = string.IsNullOrWhiteSpace(command.Note) ? null : command.Note.Trim();

        if (command.Kind == BankTransferDecisionKind.Reject && note is null)
        {
            return new ReviewBankTransferResult.Invalid(
            [
                new ValidationError(
                    "note",
                    "Say why the transfer is being rejected. The reason is kept with the order and is what "
                    + "staff read back if the customer asks."),
            ]);
        }

        if (note is { Length: > NoteMaxLength })
        {
            return new ReviewBankTransferResult.Invalid(
            [
                new ValidationError(
                    "note",
                    $"Keep the note to {NoteMaxLength} characters or fewer."),
            ]);
        }

        // Turns the public token into an internal id, and answers 404 before a transaction is opened. Every
        // *state* it reports is re-read under the order row's own lock inside the store, so nothing is
        // decided from this read.
        if (await reader.FindTargetAsync(command.OrderPublicId, cancellationToken) is not { } target)
        {
            return new ReviewBankTransferResult.NotFound();
        }

        var decision = new BankTransferDecision(
            target.OrderId,
            command.Actor,
            note,
            clock.UtcNow.UtcDateTime);

        var outcome = command.Kind switch
        {
            BankTransferDecisionKind.Approve => await store.ApproveAsync(decision, cancellationToken),
            BankTransferDecisionKind.Reject => await store.RejectAsync(decision, cancellationToken),
            BankTransferDecisionKind.AcceptLate => await store.AcceptLateAsync(decision, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(
                nameof(command),
                command.Kind,
                "Unknown bank transfer decision."),
        };

        return outcome switch
        {
            BankTransferDecisionResult.Applied applied => new ReviewBankTransferResult.Applied(
                applied.OrderNumber,
                applied.InvoiceNumber),

            BankTransferDecisionResult.NotFound => new ReviewBankTransferResult.NotFound(),

            BankTransferDecisionResult.Refused refused => new ReviewBankTransferResult.Refused(
                refused.Code,
                refused.Message,
                refused.OrderStatus.ToString(),
                refused.PaymentStatus?.ToString()),

            BankTransferDecisionResult.ShortStock shortStock => new ReviewBankTransferResult.ShortStock(
                shortStock.Lines,
                shortStock.OrderStatus.ToString()),

            _ => throw new InvalidOperationException($"Unhandled decision outcome {outcome.GetType().Name}."),
        };
    }
}

/// <summary>What the API should answer.</summary>
public abstract record ReviewBankTransferResult
{
    private ReviewBankTransferResult()
    {
    }

    /// <summary>Applied. 200, with the order number and — for an approval — the invoice number.</summary>
    public sealed record Applied(string OrderNumber, string? InvoiceNumber) : ReviewBankTransferResult;

    /// <summary>No such bank-transfer order. A bare 404.</summary>
    public sealed record NotFound : ReviewBankTransferResult;

    /// <summary>400, naming the field.</summary>
    public sealed record Invalid(IReadOnlyList<ValidationError> Errors) : ReviewBankTransferResult;

    /// <summary>
    /// 409. Somebody or something else acted first. The code and the two statuses are what let the screen
    /// offer the right next action instead of "something went wrong" (doc 11 P7's business rule).
    /// </summary>
    public sealed record Refused(
        string Code,
        string Message,
        string OrderStatus,
        string? PaymentStatus) : ReviewBankTransferResult;

    /// <summary>
    /// 409. A late acceptance refused by the stock re-check, with every short line <b>and the order's
    /// status</b> — see <see cref="BankTransferDecisionResult.ShortStock"/> for why the status is not
    /// optional here.
    /// </summary>
    public sealed record ShortStock(
        IReadOnlyList<ShortStockLine> Lines,
        string OrderStatus) : ReviewBankTransferResult;
}
