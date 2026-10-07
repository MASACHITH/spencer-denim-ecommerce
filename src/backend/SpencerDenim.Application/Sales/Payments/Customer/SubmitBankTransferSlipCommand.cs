using SpencerDenim.Application.Abstractions.Messaging;
using SpencerDenim.Application.Abstractions.Time;
using SpencerDenim.Application.Media.Abstractions;
using SpencerDenim.Application.Media.Contracts;
using SpencerDenim.Application.Sales.Payments.Abstractions;

namespace SpencerDenim.Application.Sales.Payments.Customer;

/// <summary>
/// A customer's payment slip (doc 11 P7 step 3, doc 08 §3.3 N10).
/// </summary>
/// <param name="OrderPublicId">The order's token — a guest's only credential, and the only way in.</param>
/// <param name="File">
/// The bytes and the filename, already streamed to a temporary file under the endpoint's own byte cap, so
/// nothing here can be asked to buffer an unbounded body.
/// </param>
/// <param name="IpAddress">For doc 08 §3.3 N10's per-address half of the limit.</param>
public sealed record SubmitBankTransferSlipCommand(
    Guid OrderPublicId,
    MediaUploadFile File,
    string? IpAddress);

/// <summary>
/// Stores the slip privately and points the order's open bank payment at it, replacing whatever it held.
/// </summary>
/// <remarks>
/// <b>The order of the four steps is the design.</b>
/// <list type="number">
/// <item><description>
/// <b>Find the open payment, opening one if the last attempt was rejected.</b> Doc 11 P7 step 6b says a
/// rejected order stays payable and a new slip "creates a new Pending payment record", so a customer whose
/// transfer was refused can send better evidence. A refusal here costs nothing and spends no permit.
/// </description></item>
/// <item><description>
/// <b>Then the throttle.</b> After the order is known to exist and to be payable, so a stranger walking
/// order tokens cannot spend a real customer's budget, and before any bytes are written.
/// </description></item>
/// <item><description>
/// <b>Then the upload.</b> Signature-checked, 5 MB, private volume, no renditions; an image goes through
/// the pipeline that strips EXIF, because a phone photo of a deposit slip carries where it was taken.
/// </description></item>
/// <item><description>
/// <b>Then point the payment at it and soft-delete the previous one</b>, in one transaction — "at most one
/// current file per payment" (doc 08 §3.3 N10). <b>Not the other way round:</b> repointing first and
/// uploading second would leave the payment naming an asset that does not exist if the upload failed, and
/// the staff queue would show a slip that cannot be opened.
/// </description></item>
/// </list>
/// <para>
/// <b>What happens if step 4 refuses.</b> The asset is already stored, and it is deliberately <i>not</i>
/// cleaned up: a staff member approving the transfer in the same second is the realistic cause, so the
/// bytes are a customer's evidence for an order that has just been settled, and the retention job
/// (<c>MediaRetention</c>, W3-08) is what removes them. Deleting a stranger's bank document on a race is
/// the more expensive mistake of the two. The customer is told their order can no longer be paid, which is
/// true.
/// </para>
/// </remarks>
public sealed class SubmitBankTransferSlipCommandHandler(
    IBankTransferProofStore store,
    IPaymentSlipUploadThrottle throttle,
    IMediaService media,
    IClock clock)
    : ICommandHandler<SubmitBankTransferSlipCommand, SubmitBankTransferSlipResult>
{
    public async Task<SubmitBankTransferSlipResult> HandleAsync(
        SubmitBankTransferSlipCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var now = clock.UtcNow.UtcDateTime;

        var paymentId = await store.OpenPaymentAsync(command.OrderPublicId, now, cancellationToken);

        if (paymentId is not { } payment)
        {
            return new SubmitBankTransferSlipResult.NotFound();
        }

        var permit = throttle.TryAcquire(command.OrderPublicId, command.IpAddress);

        if (permit != SlipUploadPermit.Granted)
        {
            return new SubmitBankTransferSlipResult.TooManyUploads(permit, throttle.Window);
        }

        var outcome = await media.UploadPaymentSlipAsync(
            new UploadPaymentSlipRequest(command.File, UploadedByUserId: null),
            cancellationToken);

        var asset = outcome switch
        {
            MediaUploadOutcome.Created created => created.Asset,

            // The same bytes are already stored. `UX_MediaAssets_Checksum_Ready` is unique over
            // (Checksum, MediaType, Visibility) for live Ready rows, so a customer re-sending the file they
            // sent a minute ago is answered from the existing asset rather than refused — which is the
            // right answer to "here is my slip" either way.
            MediaUploadOutcome.Duplicate duplicate => duplicate.Asset,

            MediaUploadOutcome.Rejected rejected =>
                null,

            _ => throw new InvalidOperationException($"Unhandled upload outcome {outcome.GetType().Name}."),
        };

        if (asset is null)
        {
            var rejected = (MediaUploadOutcome.Rejected)outcome;
            return new SubmitBankTransferSlipResult.Rejected(rejected.Reason, rejected.Message);
        }

        // The current slip on the payment, re-read now rather than carried from the first lookup: a
        // customer uploading twice in quick succession would otherwise have the second call soft-delete the
        // asset the first one had already replaced, and the live one would survive while a dead one did
        // not.
        var current = await store.CurrentProofAssetIdAsync(payment, cancellationToken);

        var pointed = await store.ReplaceProofAsync(
            payment,
            asset.PublicId,
            current,
            actingUserId: null,
            now,
            cancellationToken);

        return pointed
            ? new SubmitBankTransferSlipResult.Stored(
                asset.PublicId,
                asset.OriginalFileName,
                asset.ContentType,
                now)
            : new SubmitBankTransferSlipResult.NotFound();
    }
}

/// <summary>What the API should answer.</summary>
public abstract record SubmitBankTransferSlipResult
{
    private SubmitBankTransferSlipResult()
    {
    }

    /// <summary>Stored and on the payment. 200, with what the page needs to show it back.</summary>
    public sealed record Stored(
        Guid SlipPublicId,
        string FileName,
        string ContentType,
        DateTime UploadedAtUtc) : SubmitBankTransferSlipResult;

    /// <summary>
    /// No such order, or it can no longer be paid. <b>One answer for both</b>, and a bare 404: the token
    /// never expires (doc 11 §7 G-41) and is a guest's only credential.
    /// </summary>
    public sealed record NotFound : SubmitBankTransferSlipResult;

    /// <summary>The file was refused. The reason maps to a status at the endpoint.</summary>
    public sealed record Rejected(MediaUploadRejection Reason, string Message) : SubmitBankTransferSlipResult;

    /// <summary>Doc 08 §3.3 N10's limit. 429, with <c>Retry-After</c>.</summary>
    public sealed record TooManyUploads(SlipUploadPermit Limit, TimeSpan Window) : SubmitBankTransferSlipResult;
}
