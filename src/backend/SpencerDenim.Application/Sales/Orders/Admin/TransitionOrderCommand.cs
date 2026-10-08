using SpencerDenim.Application.Abstractions.Auditing;
using SpencerDenim.Application.Abstractions.Messaging;
using SpencerDenim.Application.Abstractions.Results;
using SpencerDenim.Application.Abstractions.Time;
using SpencerDenim.Application.Sales.Orders.Abstractions;
using SpencerDenim.Application.Sales.Orders.Contracts;
using SpencerDenim.Domain.Sales;

namespace SpencerDenim.Application.Sales.Orders.Admin;

/// <summary>Which of doc 11 P9's staff acts this is.</summary>
/// <remarks>
/// <b>Every member moves an order forwards or changes nothing, and that is the whole list.</b> Doc 08
/// §4.1 and doc 11 P9 both state that orders never move backwards; there is deliberately no member for
/// "back to Processing" or "un-ship", so the API has no shape in which to express one. A mistake is
/// corrected with <see cref="SetInternalNote"/> or <see cref="UpdateDispatch"/>, which is what the
/// documents prescribe.
/// </remarks>
public enum OrderTransitionKind
{
    /// <summary>Doc 11 P9 step 2: Paid → Processing.</summary>
    StartProcessing,

    /// <summary>
    /// Doc 11 P9 steps 4–5: Processing → Shipped for a delivery order, with a courier and a tracking
    /// number.
    /// </summary>
    MarkShipped,

    /// <summary>
    /// Doc 11 P9's pickup variant, step 2: Processing → Shipped for a pickup order, shown as "Ready for
    /// collection" and needing no courier or tracking number.
    /// </summary>
    ReadyForCollection,

    /// <summary>
    /// Doc 11 P9 step 8, and the pickup variant's step 4: Shipped → Delivered. "Mark as collected" for a
    /// pickup order — the same transition, different words.
    /// </summary>
    MarkDelivered,

    /// <summary>
    /// Doc 11 P9's "wrong tracking number entered": corrects the courier or the tracking number on an
    /// order that has already shipped. The status does not move.
    /// </summary>
    UpdateDispatch,

    /// <summary>
    /// Replaces the staff-only internal note. Doc 08 §4.1's correction mechanism, since orders never move
    /// backwards.
    /// </summary>
    SetInternalNote,
}

/// <summary>
/// One staff act on one order (doc 11 P9; <c>Orders.Edit</c>).
/// </summary>
/// <param name="OrderPublicId">
/// The order's public token, which is what the admin screen and every link carry. <b>Not an internal
/// id</b>, and not a payment id — the reader turns it into one under the order's own lock.
/// </param>
/// <param name="Kind">Which act.</param>
/// <param name="CourierId">
/// For a dispatch: which courier. <see langword="null"/> for everything else.
/// </param>
/// <param name="TrackingNumber">For a dispatch: what the courier gave.</param>
/// <param name="Note">
/// For a transition, an optional staff note that joins the history row's sentence. For
/// <see cref="OrderTransitionKind.SetInternalNote"/> it <b>is</b> the new note, and
/// <see langword="null"/> clears it.
/// </param>
/// <param name="Actor">Who is acting, for the history row and the audit row.</param>
public sealed record TransitionOrderCommand(
    Guid OrderPublicId,
    OrderTransitionKind Kind,
    int? CourierId,
    string? TrackingNumber,
    string? Note,
    AdminActor Actor);

/// <summary>
/// Validates a staff act and hands it to the one transaction that applies it.
/// </summary>
/// <remarks>
/// <b>One handler for six acts, because the validation is one question and the differences are all inside
/// the transaction.</b> The same argument <c>ReviewBankTransferCommandHandler</c> makes: splitting them
/// would mean six copies of the token-to-id lookup and six places for "is a tracking number required" to
/// drift, while the effects that genuinely differ are statements in <see cref="IOrderFulfilmentStore"/>
/// where they can be read side by side.
/// <para>
/// <b>This handler does not check the permission.</b> <c>Orders.Edit</c> is enforced by the endpoint's
/// policy against the user's <i>effective</i> permissions (doc 03 §3a, doc 08 §3.1 R4), which is the
/// security boundary. A second check here would be a second place to get it wrong.
/// </para>
/// <para>
/// <b>Which refusal a caller gets does not depend on whether the submitted value would have changed
/// anything.</b> ADM-22 shipped a one-bit oracle of that shape, so the order of decisions here is fixed:
/// 404 for an unknown token, then the <i>shape</i> of the request (is a tracking number present, is a
/// courier allowed for this method), then the transaction's own state guard. Nothing before the state
/// guard reads the order's status, and nothing after it reveals a value — a refusal says where the order
/// is, which a caller with <c>Orders.Edit</c> can read from the detail screen anyway.
/// </para>
/// </remarks>
public sealed class TransitionOrderCommandHandler(
    IAdminOrderReader reader,
    IOrderFulfilmentStore store,
    IClock clock)
    : ICommandHandler<TransitionOrderCommand, TransitionOrderResult>
{
    /// <summary>
    /// <c>Orders.TrackingNumber</c> is <c>varchar(60)</c> (doc 08 §3.5).
    /// <para>
    /// Refused rather than clipped: a truncated tracking number is a link that does not work, and the
    /// customer is the one who finds out. The history note clips because it is a derived sentence.
    /// </para>
    /// <para>
    /// <b><c>varchar</c>, so a non-ASCII character is silently turned into <c>?</c> by SQL Server rather
    /// than refused</b> — which for a tracking number means a link that 404s at the courier and a customer
    /// who is told to use it. <see cref="IsStorableAscii"/> refuses it here instead.
    /// </para>
    /// </summary>
    public const int TrackingNumberMaxLength = 60;

    /// <summary><c>Orders.InternalNote</c> is <c>nvarchar(2000)</c> (doc 08 §3.5).</summary>
    public const int InternalNoteMaxLength = 2000;

    public async Task<TransitionOrderResult> HandleAsync(
        TransitionOrderCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        // Turns the public token into an internal id and lets the API answer 404 before a transaction is
        // opened. Its *status* is used only to decide which transition "mark shipped" means for a pickup
        // order, and every guard is re-evaluated under the order row's own lock inside the store.
        if (await reader.FindTargetAsync(command.OrderPublicId, cancellationToken) is not { } target)
        {
            return new TransitionOrderResult.NotFound();
        }

        if (Validate(command, target) is { Count: > 0 } errors)
        {
            return new TransitionOrderResult.Invalid(errors);
        }

        var transition = new OrderTransition(
            target.OrderId,
            command.Actor,

            // Trimmed, and whitespace treated as absent: " " in a note box is a staff member who cleared
            // it, not a note made of a space.
            string.IsNullOrWhiteSpace(command.Note) ? null : command.Note.Trim(),
            clock.UtcNow.UtcDateTime);

        var dispatch = command.Kind is OrderTransitionKind.MarkShipped or OrderTransitionKind.UpdateDispatch
            ? new OrderDispatchDetails(command.CourierId!.Value, command.TrackingNumber!.Trim())
            : null;

        var outcome = command.Kind switch
        {
            OrderTransitionKind.StartProcessing =>
                await store.StartProcessingAsync(transition, cancellationToken),

            // Both of these are doc 08 §4.1's one `Processing -> Shipped` row; the pickup variant is the
            // same transition with no dispatch, which is why they share a store method rather than
            // duplicating a conditional UPDATE that must stay identical.
            OrderTransitionKind.MarkShipped =>
                await store.MarkShippedAsync(transition, dispatch, cancellationToken),
            OrderTransitionKind.ReadyForCollection =>
                await store.MarkShippedAsync(transition, null, cancellationToken),

            OrderTransitionKind.MarkDelivered =>
                await store.MarkDeliveredAsync(transition, cancellationToken),

            OrderTransitionKind.UpdateDispatch =>
                await store.UpdateDispatchAsync(transition, dispatch!, cancellationToken),

            OrderTransitionKind.SetInternalNote =>
                await store.SetInternalNoteAsync(transition, transition.Note, cancellationToken),

            _ => throw new ArgumentOutOfRangeException(
                nameof(command),
                command.Kind,
                "Unknown order transition."),
        };

        return outcome switch
        {
            OrderTransitionResult.Applied applied => new TransitionOrderResult.Applied(
                applied.OrderNumber,
                applied.Status.ToString(),
                OrderStatusLabels.For(applied.Status, target.IsPickup)),

            OrderTransitionResult.NotFound => new TransitionOrderResult.NotFound(),

            OrderTransitionResult.Refused refused => new TransitionOrderResult.Refused(
                refused.Code,
                refused.Message,
                refused.Status.ToString(),
                refused.StatusLabel),

            _ => throw new InvalidOperationException($"Unhandled transition outcome {outcome.GetType().Name}."),
        };
    }

    /// <summary>
    /// Everything that can be decided from the request and the order's <i>immutable</i> facts.
    /// </summary>
    /// <remarks>
    /// <b>Only the delivery method is read from the target, and only because it cannot change.</b> Doc 11
    /// P9 makes the delivery method immutable after placement, so "is this a pickup" is as safe to decide
    /// out here as the request's own shape; the status is not, so nothing in this method looks at it.
    /// </remarks>
    private static List<ValidationError> Validate(TransitionOrderCommand command, AdminOrderTarget target)
    {
        var errors = new List<ValidationError>();

        switch (command.Kind)
        {
            case OrderTransitionKind.MarkShipped or OrderTransitionKind.UpdateDispatch:

                // **A pickup order has no courier, in either direction.** Refusing "mark shipped" is
                // obvious; refusing the *correction* matters just as much and is easy to miss, because a
                // pickup order genuinely is in `Shipped` (doc 11 P9 shows it to staff as "Ready for
                // collection"), so the store's own status guard would be satisfied and a courier would be
                // written on to an order nobody is couriering — and the detail screen would then draw a
                // tracking link for a parcel sitting on a shelf.
                if (target.IsPickup)
                {
                    errors.Add(new ValidationError(
                        "courierId",
                        command.Kind == OrderTransitionKind.MarkShipped
                            ? "This is a store pickup order, so it has no courier. Use Ready for "
                              + "collection."
                            : "This is a store pickup order, so it has no courier or tracking number to "
                              + "correct. Use the internal note instead."));
                    break;
                }

                if (command.CourierId is null or <= 0)
                {
                    errors.Add(new ValidationError(
                        "courierId",
                        "Choose the courier carrying this order. If the list is empty, add a courier in "
                        + "Settings first."));
                }

                if (string.IsNullOrWhiteSpace(command.TrackingNumber))
                {
                    errors.Add(new ValidationError(
                        "trackingNumber",
                        "Enter the tracking number the courier gave you. The customer has no way to "
                        + "follow the parcel without it."));
                }
                else if (command.TrackingNumber.Trim().Length > TrackingNumberMaxLength)
                {
                    errors.Add(new ValidationError(
                        "trackingNumber",
                        $"Keep the tracking number to {TrackingNumberMaxLength} characters or fewer."));
                }
                else if (!IsStorableAscii(command.TrackingNumber.Trim()))
                {
                    errors.Add(new ValidationError(
                        "trackingNumber",
                        "Use letters, digits and dashes only. The column cannot store other characters "
                        + "and would turn them into question marks, which breaks the tracking link."));
                }

                break;

            case OrderTransitionKind.ReadyForCollection:
                if (!target.IsPickup)
                {
                    errors.Add(new ValidationError(
                        "deliveryMethod",
                        "This order is being delivered, not collected. Use Mark as shipped and enter the "
                        + "courier and tracking number."));
                }

                // A courier or a tracking number sent with a pickup dispatch is refused rather than
                // ignored. Ignoring it would write nothing and answer 200, so a client with the wrong
                // button wired up would look like it was working.
                if (command.CourierId is not null || !string.IsNullOrWhiteSpace(command.TrackingNumber))
                {
                    errors.Add(new ValidationError(
                        "courierId",
                        "A store pickup order has no courier and no tracking number."));
                }

                break;

            case OrderTransitionKind.SetInternalNote:
                if (command.Note is { Length: > InternalNoteMaxLength })
                {
                    errors.Add(new ValidationError(
                        "internalNote",
                        $"Keep the note to {InternalNoteMaxLength} characters or fewer."));
                }

                break;

            case OrderTransitionKind.StartProcessing:
            case OrderTransitionKind.MarkDelivered:

                // Nothing to validate: the only guard is the order's status, and that belongs in the
                // transaction. Listed explicitly rather than swept into `default` so that adding a member
                // to the enum is a compile-time decision about validation rather than a silent "no rules".
                break;

            default:
                throw new ArgumentOutOfRangeException(
                    nameof(command),
                    command.Kind,
                    "Unknown order transition.");
        }

        return errors;
    }

    /// <summary>
    /// Whether every character of <paramref name="value"/> survives a <c>varchar</c> column.
    /// </summary>
    /// <remarks>
    /// Printable ASCII only, and a storability check rather than a format rule: couriers number their
    /// parcels however they like, and refusing a shape this code had not seen would be a box a staff member
    /// could not get past with the number in their hand.
    /// </remarks>
    private static bool IsStorableAscii(string value) =>
        value.All(character => character is >= ' ' and <= '~');
}

/// <summary>What the API should answer.</summary>
public abstract record TransitionOrderResult
{
    private TransitionOrderResult()
    {
    }

    /// <summary>Applied. 200, with the order number and where the order now is.</summary>
    public sealed record Applied(
        string OrderNumber,
        string Status,
        string StatusLabel) : TransitionOrderResult;

    /// <summary>No such order. A bare 404.</summary>
    public sealed record NotFound : TransitionOrderResult;

    /// <summary>400, naming the field.</summary>
    public sealed record Invalid(IReadOnlyList<ValidationError> Errors) : TransitionOrderResult;

    /// <summary>
    /// 409. Somebody or something else acted first. The code and the current status are what let the
    /// screen redraw the right action instead of "something went wrong".
    /// </summary>
    public sealed record Refused(
        string Code,
        string Message,
        string Status,
        string StatusLabel) : TransitionOrderResult;
}
