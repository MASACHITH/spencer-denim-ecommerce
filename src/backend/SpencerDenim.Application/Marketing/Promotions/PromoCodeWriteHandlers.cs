using System.Globalization;
using SpencerDenim.Application.Abstractions.Auditing;
using SpencerDenim.Application.Abstractions.Messaging;
using SpencerDenim.Application.Abstractions.Results;
using SpencerDenim.Application.Abstractions.Time;
using SpencerDenim.Application.Content.Admin;
using SpencerDenim.Application.Marketing.Promotions.Abstractions;
using SpencerDenim.Application.Marketing.Promotions.Contracts;
using SpencerDenim.Domain.Auditing;
using SpencerDenim.Domain.Marketing;

namespace SpencerDenim.Application.Marketing.Promotions;

/// <summary>
/// The fields a staff member sets on a promo code (ADM-13, doc 11 P3 steps 6 and 7).
/// </summary>
/// <remarks>
/// <b>Every field is nullable and every field is sent on every save.</b> The first is issue #47 — a JSON
/// body that omits a property binds null into a non-nullable one and the first thing to touch it throws,
/// which the API answered as a 500 until a tester found it. The second is this project's other repeated
/// defect: "a field this body does not carry is left as it is" and "a field it carries as null is cleared"
/// are different instructions, and collapsing them is how W1-12 wiped every category image. A promo code
/// has no partial-edit screen, so the endpoint requires every key to be <i>present</i> and this record
/// then means "clear it" by null, with no ambiguity left to resolve.
/// </remarks>
/// <param name="StartsAt">
/// Colombo wall-clock, as the form's <c>datetime-local</c> input produces it. Converted by
/// <see cref="PromoCodeSchedule.ToUtc"/>, which is where Asia/Colombo lives and nowhere else.
/// </param>
/// <param name="ExcludeSaleItems">
/// Doc 11 P3 step 7's "exclude sale items". <b>No rule validates it, and that is a finding rather than
/// an omission.</b> It is meaningful under all three discount types — including <c>FreeDelivery</c>,
/// where it cannot change the delivery discount but can still empty the eligible base and so refuse the
/// code with <c>PromoRefusal.NoEligibleItems</c> (<see cref="PromoEvaluator"/>'s
/// <c>eligibleBase &lt;= 0m</c> check) — so there is no type it has to be refused for, and no range to
/// bound. Null is read as off, the same reading <see cref="IsActive"/> gets and for the same reason.
/// </param>
public sealed record PromoCodeInput(
    string? Code,
    string? Description,
    PromoDiscountType? DiscountType,
    decimal? DiscountValue,
    decimal? MaxDiscountAmount,
    decimal? MinSpendAmount,
    DateTime? StartsAt,
    DateTime? EndsAt,
    bool? ExcludeSaleItems,
    int? TotalUsageLimit,
    int? PerCustomerLimit,
    bool? IsActive);

/// <summary>Create a promo code (ADM-13, <c>Promotions.Create</c>).</summary>
public sealed record CreatePromoCodeCommand(PromoCodeInput Fields, AdminActor Actor);

/// <summary>Edit a promo code (ADM-13, <c>Promotions.Edit</c>).</summary>
public sealed record UpdatePromoCodeCommand(int PromoCodeId, PromoCodeInput Fields, string? RowVersion, AdminActor Actor);

/// <summary>Remove a promo code — a soft delete (ADM-13, <c>Promotions.Delete</c>).</summary>
public sealed record DeletePromoCodeCommand(int PromoCodeId, string? RowVersion, AdminActor Actor);

/// <summary>
/// What an ADM-13 write decided. The same five answers <c>ContentWriteResult</c> has, for the same reason:
/// these use cases have outcomes a client must tell apart that a validation-only result cannot express.
/// </summary>
public abstract record PromoWriteResult
{
    private PromoWriteResult()
    {
    }

    /// <summary>The write happened. <paramref name="Saved"/> is the row and any warnings about it.</summary>
    public sealed record Succeeded(AdminPromoCodeSavedDto Saved) : PromoWriteResult;

    /// <summary>Rejected input, one entry per field, so the screen attaches each message to its control.</summary>
    public sealed record Invalid(IReadOnlyList<ValidationError> Errors) : PromoWriteResult
    {
        public Invalid(string field, string message)
            : this(new List<ValidationError> { new(field, message) }.AsReadOnly())
        {
        }
    }

    /// <summary>No such code, or it has been removed.</summary>
    public sealed record NotFound(string Message) : PromoWriteResult;

    /// <summary>Well formed and still impossible: a stale row version, or a unique index losing a race.</summary>
    public sealed record Conflict(string Code, string Message) : PromoWriteResult;
}

/// <summary>The <see cref="PromoWriteResult.Conflict.Code"/> values these use cases produce.</summary>
public static class PromoConflictCodes
{
    /// <summary>The caller's row version is not the row's current one; reload and re-apply (doc 08 §1.5).</summary>
    public const string StaleRowVersion = "stale_row_version";

    /// <summary>A unique index refused the write after the handler's own check passed — the race.</summary>
    public const string DuplicateKey = "duplicate_key";
}

/// <summary>
/// Create, edit and remove a promo code (ADM-13; doc 11 P3 B, doc 08 §3.6).
/// <para>
/// <b>Three writes in one class, because they share every rule.</b> The code's spelling, the discount
/// value's range for its type, "end after start" and "per-customer ≤ total" are the same checks whichever
/// way the row arrives, and three copies would be three chances for one of them to drift — the argument
/// <c>ContentPageWriteHandlers</c> makes for the same arrangement. It is registered once per interface so
/// each endpoint still asks for the one command it sends.
/// </para>
/// <para>
/// <b>Validation is here and not in the database.</b> <c>CK_PromoCodes_DiscountValue</c> and friends exist
/// for the support script, the data fix and the restore — the paths that do not come through a use case —
/// and every rule below is also one of them. Leaving a rule to the constraint alone would turn a typo
/// into a 500 instead of a named field error beside the control.
/// </para>
/// </summary>
public sealed class PromoCodeWriteHandlers(
    IPromoCodeAdminStore store,
    IPromoCodeAdminReader reader,
    IAuditLogWriter auditLog,
    IClock clock)
    : ICommandHandler<CreatePromoCodeCommand, PromoWriteResult>,
        ICommandHandler<UpdatePromoCodeCommand, PromoWriteResult>,
        ICommandHandler<DeletePromoCodeCommand, PromoWriteResult>
{
    /// <summary><c>ops.AuditLogs.EntityType</c> for a promo code.</summary>
    public const string EntityType = "PromoCode";

    public async Task<PromoWriteResult> HandleAsync(
        CreatePromoCodeCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var (fields, errors) = Validate(command.Fields);

        if (errors.Count > 0)
        {
            return new PromoWriteResult.Invalid(errors);
        }

        if (await store.CodeTakenAsync(fields.Code, null, cancellationToken))
        {
            return new PromoWriteResult.Invalid("code", "A promo code with that code already exists.");
        }

        var promoCode = new PromoCode();
        Apply(promoCode, fields);
        store.Add(promoCode);

        auditLog.Enlist(ContentAuditTrail.Row(
            AuditActions.Create,
            EntityType,

            // The code and not the id: the row has no id until the save, and the code is what a staff
            // member looking through the trail would search for anyway.
            fields.Code,
            null,
            Snapshot(promoCode),
            command.Actor,
            clock.UtcNow.UtcDateTime));

        return await CommitAsync(promoCode, fields, command.Actor.UserId, cancellationToken);
    }

    public async Task<PromoWriteResult> HandleAsync(
        UpdatePromoCodeCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var promoCode = await store.FindAsync(command.PromoCodeId, cancellationToken);

        if (promoCode is null)
        {
            return NotFound(command.PromoCodeId);
        }

        if (RowVersionMismatch(promoCode, command.RowVersion) is { } stale)
        {
            return stale;
        }

        var (fields, errors) = Validate(command.Fields);

        if (errors.Count > 0)
        {
            return new PromoWriteResult.Invalid(errors);
        }

        if (await store.CodeTakenAsync(fields.Code, promoCode.Id, cancellationToken))
        {
            return new PromoWriteResult.Invalid("code", "A promo code with that code already exists.");
        }

        var before = Snapshot(promoCode);

        Apply(promoCode, fields);

        var audit = ContentAuditTrail.ForChange(
            AuditActions.Update,
            EntityType,
            promoCode.Id.ToString(CultureInfo.InvariantCulture),
            before,
            Snapshot(promoCode),
            command.Actor,
            clock.UtcNow.UtcDateTime);

        if (audit is not null)
        {
            auditLog.Enlist(audit);
        }

        // Saved even when nothing moved, unlike the content page's equivalent: the row version has to come
        // back for the form to stay usable, and re-reading it is the same round trip as saving it. An
        // unchanged save writes no audit row, which is what `audit is null` above means.
        return await CommitAsync(promoCode, fields, command.Actor.UserId, cancellationToken);
    }

    public async Task<PromoWriteResult> HandleAsync(
        DeletePromoCodeCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var promoCode = await store.FindAsync(command.PromoCodeId, cancellationToken);

        if (promoCode is null)
        {
            return NotFound(command.PromoCodeId);
        }

        if (RowVersionMismatch(promoCode, command.RowVersion) is { } stale)
        {
            return stale;
        }

        var now = clock.UtcNow.UtcDateTime;
        var before = Snapshot(promoCode);

        // A soft delete, which releases the code string for reuse (UX_PromoCodes_Code is filtered on
        // IsDeleted = 0) and leaves every order that used it alone — the order keeps `PromoCodeText` as
        // its own snapshot, so an invoice still prints the code (doc 08 §3.6 DB19). The foreign key is NO
        // ACTION and never blocks, because nothing is ever hard-deleted here.
        //
        // **It does not give back the uses that pending orders hold.** Doc 08 §3.6 DB19 is explicit that
        // deactivating or ending a code does not affect orders already placed, and those orders still
        // decrement the count when they expire or are cancelled (`OrderPromoUse.ReturnAsync`), which the
        // `UsageCount > 0` guard keeps safe on a deleted row.
        promoCode.SoftDelete(now, command.Actor.UserId);

        auditLog.Enlist(ContentAuditTrail.Row(
            AuditActions.Delete,
            EntityType,
            promoCode.Id.ToString(CultureInfo.InvariantCulture),
            before,
            null,
            command.Actor,
            now));

        var outcome = await store.SaveAsync(command.Actor.UserId, cancellationToken);

        return outcome switch
        {
            PromoSaveOutcome.Saved => new PromoWriteResult.Succeeded(
                new AdminPromoCodeSavedDto(ToDto(promoCode, now), [])),
            PromoSaveOutcome.ConcurrencyConflict => StaleRowVersion(),

            // Unreachable: a delete writes no code, so UX_PromoCodes_Code has nothing to refuse. An
            // exception rather than a comment claiming so.
            _ => throw new InvalidOperationException(
                $"Removing promo code {promoCode.Id} answered {outcome}, which a delete cannot produce."),
        };
    }

    private async Task<PromoWriteResult> CommitAsync(
        PromoCode promoCode,
        ValidFields fields,
        int? actingUserId,
        CancellationToken cancellationToken)
    {
        var outcome = await store.SaveAsync(actingUserId, cancellationToken);

        if (outcome == PromoSaveOutcome.ConcurrencyConflict)
        {
            return StaleRowVersion();
        }

        if (outcome == PromoSaveOutcome.DuplicateKey)
        {
            return new PromoWriteResult.Conflict(
                PromoConflictCodes.DuplicateKey,
                "Another staff member created a promo code with that code a moment ago. Reload and try "
                + "again.");
        }

        var now = clock.UtcNow.UtcDateTime;

        // Re-read, so the screen is shown what the database holds rather than what we meant to write. The
        // row version in particular has to be the one the save produced, or the next edit from this form
        // would be refused as stale.
        var saved = await reader.FindAsync(promoCode.Id, cancellationToken)
            ?? throw new InvalidOperationException(
                $"Promo code {promoCode.Id} was saved but could not be read back.");

        return new PromoWriteResult.Succeeded(
            new AdminPromoCodeSavedDto(saved, Warnings(promoCode, fields, now)));
    }

    /// <summary>
    /// Doc 08 §3.6 DB19: "Lowering <c>TotalUsageLimit</c> below <c>UsageCount</c> is allowed with a
    /// warning. It only blocks <b>new</b> uses; pending orders keep their held use until they are paid,
    /// expire or are cancelled."
    /// </summary>
    /// <remarks>
    /// Allowed rather than refused because the staff member's intent is clear and legitimate — stop this
    /// code being used — and refusing would leave them no way to express it except deactivating the code,
    /// which also stops the customers who are mid-checkout. The warning is the whole of the difference
    /// between that and a silent save that looks like it did nothing.
    /// </remarks>
    private static List<string> Warnings(PromoCode promoCode, ValidFields fields, DateTime utcNow)
    {
        var warnings = new List<string>();

        if (fields.TotalUsageLimit is { } limit && promoCode.UsageCount > limit)
        {
            warnings.Add(string.Create(CultureInfo.InvariantCulture, $"This code has already been used {promoCode.UsageCount} times, which is above the new total limit of {limit}. No new customer can use it; orders already placed keep their use."));
        }

        if (PromoCodeSchedule.StatusOf(fields.IsActive, fields.StartsAtUtc, fields.EndsAtUtc, utcNow)
            == PromoCodeStatus.Ended)
        {
            warnings.Add("This code's end date has already passed, so no shopper can use it.");
        }

        return warnings;
    }

    private static PromoWriteResult.NotFound NotFound(int promoCodeId) =>
        new(string.Create(CultureInfo.InvariantCulture, $"No promo code has the id {promoCodeId}."));

    private static PromoWriteResult.Conflict StaleRowVersion() =>
        new(
            PromoConflictCodes.StaleRowVersion,
            "Somebody else changed this promo code while you were editing it. Reload and re-apply your "
            + "changes.");

    /// <summary>
    /// The optimistic-concurrency check (doc 08 §1.5). A caller that sends no row version is refused
    /// rather than allowed to write blind: these screens all round-trip one, and "I did not send it"
    /// cannot be told from "I am working from an old copy".
    /// </summary>
    private static PromoWriteResult? RowVersionMismatch(PromoCode promoCode, string? rowVersion)
    {
        if (string.IsNullOrWhiteSpace(rowVersion))
        {
            return new PromoWriteResult.Invalid(
                "rowVersion",
                "Send the promo code's row version so a concurrent edit is not overwritten.");
        }

        var current = promoCode.RowVersion is { } bytes ? Convert.ToBase64String(bytes) : null;

        // An ETag may arrive quoted, and `If-Match: "AAAA..."` is the correct spelling of one. Compared
        // ordinally on the base64 text: it is case-sensitive data, and a case-insensitive comparison
        // would call two different row versions equal.
        var supplied = rowVersion.Trim().Trim('"');

        return string.Equals(current, supplied, StringComparison.Ordinal) ? null : StaleRowVersion();
    }

    /// <summary>
    /// Every rule doc 11 P3 step 8 and doc 08 §3.6 state, as named field errors.
    /// </summary>
    /// <remarks>
    /// <b>All of them at once, never the first.</b> A staff member filling in a form with three mistakes
    /// in it should be told three times rather than three times over.
    /// </remarks>
    private static (ValidFields Fields, IReadOnlyList<ValidationError> Errors) Validate(PromoCodeInput input)
    {
        var errors = new List<ValidationError>();

        var code = PromoCodeText.Normalise(input.Code);

        if (code is null)
        {
            errors.Add(new ValidationError("code", "Enter the code shoppers will type."));
        }
        else if (!PromoCodeText.IsWellFormed(code))
        {
            errors.Add(new ValidationError("code", string.Create(CultureInfo.InvariantCulture, $"A code may be up to {PromoCode.MaxCodeLength} letters, numbers, hyphens or underscores, with no spaces.")));
        }

        var description = string.IsNullOrWhiteSpace(input.Description) ? null : input.Description.Trim();

        if (description is { Length: > PromoCode.MaxDescriptionLength })
        {
            errors.Add(new ValidationError("description", string.Create(CultureInfo.InvariantCulture, $"Keep the description to {PromoCode.MaxDescriptionLength} characters or fewer.")));
        }

        if (input.DiscountType is not { } discountType
            || discountType is not (PromoDiscountType.Percent
                or PromoDiscountType.FixedAmount
                or PromoDiscountType.FreeDelivery))
        {
            errors.Add(new ValidationError(
                "discountType",
                "Choose a percentage, a fixed amount off, or free delivery."));
            discountType = PromoDiscountType.Percent;
        }

        // Doc 08 §3.6's CK, in the app so a typo is a field error: "Percent 0 < v ≤ 100; Fixed > 0;
        // FreeDelivery NULL". Doc 11 P3 step 6 adds the reason for the strict lower bound — "0% is
        // rejected as meaningless".
        var discountValue = ValidateDiscountValue(input, discountType, errors);

        if (discountType != PromoDiscountType.Percent && input.MaxDiscountAmount is not null)
        {
            errors.Add(new ValidationError(
                "maxDiscountAmount",
                "A maximum discount only applies to a percentage code."));
        }
        else if (input.MaxDiscountAmount is { } cap && cap <= 0m)
        {
            errors.Add(new ValidationError("maxDiscountAmount", "A maximum discount must be above zero."));
        }

        if (input.MinSpendAmount is { } minimumSpend && minimumSpend <= 0m)
        {
            errors.Add(new ValidationError(
                "minSpendAmount",
                "A minimum spend must be above zero. Leave it empty for no minimum."));
        }

        if (input.StartsAt is null)
        {
            errors.Add(new ValidationError("startsAt", "Choose when the code starts working."));
        }
        else if (input.EndsAt is { } endsAt && endsAt <= input.StartsAt.Value)
        {
            // Doc 11 P3 step 8: "end after start". The same strictness as CK_PromoCodes_Window, so a row
            // the form accepts is a row the database accepts.
            errors.Add(new ValidationError("endsAt", "The end must be after the start."));
        }

        if (input.TotalUsageLimit is { } total && total < 1)
        {
            errors.Add(new ValidationError(
                "totalUsageLimit",
                "A total usage limit must be at least 1. Leave it empty for no limit."));
        }

        if (input.PerCustomerLimit is { } perCustomer && perCustomer < 1)
        {
            errors.Add(new ValidationError(
                "perCustomerLimit",
                "A per-customer limit must be at least 1. Leave it empty for no limit."));
        }
        else if (input.PerCustomerLimit is { } limit
            && input.TotalUsageLimit is { } totalLimit
            && limit > totalLimit)
        {
            // Doc 11 P3 step 8: "per-customer limit ≤ total limit when both set". A validator rule and not
            // a constraint, which doc 08 §3.6 DB19 says in as many words ("limits are validated in the
            // app"), because the total limit may legitimately be lowered below it later.
            errors.Add(new ValidationError(
                "perCustomerLimit",
                "A customer cannot be allowed more uses than the code has in total."));
        }

        var fields = new ValidFields(
            code ?? string.Empty,
            description,
            discountType,
            discountValue,
            discountType == PromoDiscountType.Percent ? input.MaxDiscountAmount : null,
            input.MinSpendAmount,
            PromoCodeSchedule.ToUtc(input.StartsAt ?? default),
            input.EndsAt is { } end ? PromoCodeSchedule.ToUtc(end) : null,

            // Null is "off", as `IsActive` below. A half-filled request that did not say to narrow the
            // eligible base has not asked for the narrower reading of the code.
            input.ExcludeSaleItems ?? false,
            input.TotalUsageLimit,
            input.PerCustomerLimit,

            // Null is "off": a form that did not send the flag has not asked for a live code, and the
            // safe reading of a half-filled request is the one that does not put a discount on the shop.
            input.IsActive ?? false);

        return (fields, errors.AsReadOnly());
    }

    private static decimal? ValidateDiscountValue(
        PromoCodeInput input,
        PromoDiscountType discountType,
        List<ValidationError> errors)
    {
        switch (discountType)
        {
            case PromoDiscountType.Percent:
                if (input.DiscountValue is not { } percent || percent <= 0m || percent > 100m)
                {
                    errors.Add(new ValidationError(
                        "discountValue",
                        "Enter a percentage above 0 and up to 100."));
                    return null;
                }

                return percent;

            case PromoDiscountType.FixedAmount:
                if (input.DiscountValue is not { } amount || amount <= 0m)
                {
                    errors.Add(new ValidationError("discountValue", "Enter an amount above zero."));
                    return null;
                }

                return amount;

            default:
                // FreeDelivery. A value sent with it is refused rather than ignored: silently dropping it
                // would let a staff member believe they had set a 20%-off-and-free-delivery code.
                if (input.DiscountValue is not null)
                {
                    errors.Add(new ValidationError(
                        "discountValue",
                        "A free-delivery code has no amount. Clear the value or choose another type."));
                }

                return null;
        }
    }

    private static void Apply(PromoCode promoCode, ValidFields fields)
    {
        promoCode.Code = fields.Code;
        promoCode.Description = fields.Description;
        promoCode.DiscountType = fields.DiscountType;
        promoCode.DiscountValue = fields.DiscountValue;
        promoCode.MaxDiscountAmount = fields.MaxDiscountAmount;
        promoCode.MinSpendAmount = fields.MinSpendAmount;
        promoCode.StartsAtUtc = fields.StartsAtUtc;
        promoCode.EndsAtUtc = fields.EndsAtUtc;
        promoCode.ExcludeSaleItems = fields.ExcludeSaleItems;
        promoCode.TotalUsageLimit = fields.TotalUsageLimit;
        promoCode.PerCustomerLimit = fields.PerCustomerLimit;
        promoCode.IsActive = fields.IsActive;

        // UsageCount is deliberately not assignable from a form. It is a cache of held uses (doc 08 §3.6)
        // and the only two things that may move it are the take at placement and the return when an
        // unpaid order closes; a staff member who could type a number into it could hand out uses the
        // orders table does not agree exist.
    }

    /// <summary>
    /// The audit snapshot. <b>Every field a staff member can change and nothing else</b> — no
    /// <c>UsageCount</c>, which moves on its own and would make every shopper's checkout look like an
    /// admin edit in the trail.
    /// </summary>
    private static Dictionary<string, object?> Snapshot(PromoCode promoCode) =>
        new(StringComparer.Ordinal)
        {
            ["code"] = promoCode.Code,
            ["description"] = promoCode.Description,
            ["discountType"] = promoCode.DiscountType.ToString(),
            ["discountValue"] = promoCode.DiscountValue,
            ["maxDiscountAmount"] = promoCode.MaxDiscountAmount,
            ["minSpendAmount"] = promoCode.MinSpendAmount,

            // UTC in the trail, like every other timestamp in ops.AuditLogs, and the Colombo conversion
            // stays at the screen's edge. A trail with two timezones in it is a trail nobody can order.
            ["startsAtUtc"] = promoCode.StartsAtUtc,
            ["endsAtUtc"] = promoCode.EndsAtUtc,
            ["excludeSaleItems"] = promoCode.ExcludeSaleItems,
            ["totalUsageLimit"] = promoCode.TotalUsageLimit,
            ["perCustomerLimit"] = promoCode.PerCustomerLimit,
            ["isActive"] = promoCode.IsActive,
        };

    /// <summary>
    /// The row for a <b>delete</b>'s response, built from the entity rather than re-read: the reader
    /// filters soft-deleted rows out, so there would be nothing to read back.
    /// </summary>
    private static AdminPromoCodeDto ToDto(PromoCode promoCode, DateTime utcNow) =>
        new(
            promoCode.Id,
            promoCode.Code,
            promoCode.Description,
            promoCode.DiscountType,
            promoCode.DiscountValue,
            promoCode.MaxDiscountAmount,
            promoCode.MinSpendAmount,
            PromoCodeSchedule.ToColombo(promoCode.StartsAtUtc),
            promoCode.EndsAtUtc is { } endsAt ? PromoCodeSchedule.ToColombo(endsAt) : null,
            promoCode.ExcludeSaleItems,
            promoCode.TotalUsageLimit,
            promoCode.PerCustomerLimit,
            promoCode.UsageCount,
            promoCode.IsActive,
            PromoCodeSchedule.StatusOf(
                promoCode.IsActive,
                promoCode.StartsAtUtc,
                promoCode.EndsAtUtc,
                utcNow),
            promoCode.RowVersion is { } bytes ? Convert.ToBase64String(bytes) : string.Empty);

    /// <summary>The input after validation: no nullable intentions left, and the dates in UTC.</summary>
    private sealed record ValidFields(
        string Code,
        string? Description,
        PromoDiscountType DiscountType,
        decimal? DiscountValue,
        decimal? MaxDiscountAmount,
        decimal? MinSpendAmount,
        DateTime StartsAtUtc,
        DateTime? EndsAtUtc,
        bool ExcludeSaleItems,
        int? TotalUsageLimit,
        int? PerCustomerLimit,
        bool IsActive);
}
