using SpencerDenim.Application.Abstractions.Auditing;
using SpencerDenim.Application.Abstractions.Messaging;
using SpencerDenim.Application.Abstractions.Results;
using SpencerDenim.Application.Abstractions.Time;
using SpencerDenim.Application.Sales.Orders.Abstractions;
using SpencerDenim.Domain.Sales;

namespace SpencerDenim.Application.Sales.Orders.Admin;

/// <summary>
/// Adds or edits one courier (<c>sales.Couriers</c>, ADM-09 / ADM-22; <c>Settings.Edit</c> per doc 11 P9's
/// permission table).
/// </summary>
/// <param name="CourierId">
/// The courier to edit, or <see langword="null"/> to add one.
/// </param>
/// <param name="Name">What staff and customers call them.</param>
/// <param name="TrackingUrlTemplate">
/// A URL containing <c>{trackingNumber}</c>, or <see langword="null"/>/blank for a courier with no online
/// tracking.
/// </param>
/// <param name="Phone">Who to ring about a parcel.</param>
/// <param name="IsActive">Whether a new dispatch may choose it.</param>
/// <param name="SortOrder">Where it sits in the picker.</param>
/// <param name="Actor">Who is asking, for the audit row.</param>
public sealed record SaveCourierCommand(
    int? CourierId,
    string? Name,
    string? TrackingUrlTemplate,
    string? Phone,
    bool IsActive,
    short SortOrder,
    AdminActor Actor);

/// <summary>
/// Validates a courier and saves it.
/// </summary>
/// <remarks>
/// <b>Nothing here invents a courier name or a tracking URL.</b> Not a seeded row, not a default, and
/// <b>not a form placeholder that renders</b> — W3-03 shipped a real bank's name and the client's own
/// legal name as placeholder text and both were Major findings. Which carriers the client uses is the
/// client's information; the only example this code is allowed to show is an obviously-fake shape, which
/// is why <see cref="TemplateExample"/> says <c>{trackingNumber}</c> and nothing around it.
/// </remarks>
public sealed class SaveCourierCommandHandler(ICourierAdminStore store, IClock clock)
    : ICommandHandler<SaveCourierCommand, SaveCourierResult>
{
    /// <summary><c>Couriers.Name</c> is <c>nvarchar(80)</c> (doc 08 §3.5).</summary>
    public const int NameMaxLength = 80;

    /// <summary><c>Couriers.TrackingUrlTemplate</c> is <c>nvarchar(500)</c>.</summary>
    public const int TemplateMaxLength = 500;

    /// <summary>
    /// <c>Couriers.Phone</c> is <c>varchar(20)</c>.
    /// <para>
    /// <b><c>varchar</c>, so a non-ASCII character would be silently replaced by <c>?</c> on the way in</b>
    /// — SQL Server does not error, it substitutes. A telephone number has no business containing one, but
    /// a pasted value with a non-breaking space or an Arabic-Indic digit would be stored as a number that
    /// does not dial and nothing would have said so, which is why <see cref="IsStorableAscii"/> refuses it
    /// here rather than leaving the column to mangle it.
    /// </para>
    /// </summary>
    public const int PhoneMaxLength = 20;

    /// <summary>
    /// The one thing a template must contain, shown to the staff member as the shape to copy.
    /// <para>
    /// <b>It is the placeholder alone and not a sample URL.</b> A sample like
    /// <c>https://example.invalid/track?no={trackingNumber}</c> would be an invented carrier's address
    /// rendered beside the box the real one is typed into — a value mistakable for data, which is the
    /// criterion both of this project's Major findings about invented client data turned on. Even in this
    /// comment the host is <c>.invalid</c>, which RFC 2606 reserves, rather than something that reads like
    /// a carrier somebody might go and look up.
    ///
    /// <b>The accepted convention is an obviously-fake <i>shape</i></b> — W3-03's <c>0000 0000 0000</c>
    /// for an account number. A URL has no equivalent that is both fake-looking and a valid URL, so what
    /// is shown is the placeholder token on its own: no scheme, no host, nothing to mistake.
    /// </para>
    /// </summary>
    public static string TemplateExample => Courier.TrackingNumberPlaceholder;

    public async Task<SaveCourierResult> HandleAsync(
        SaveCourierCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var errors = new List<ValidationError>();

        var name = Clean(command.Name);
        if (name is null)
        {
            errors.Add(new ValidationError("name", "Give the courier a name."));
        }
        else if (name.Length > NameMaxLength)
        {
            errors.Add(new ValidationError(
                "name",
                $"Keep the courier's name to {NameMaxLength} characters or fewer."));
        }

        var template = Clean(command.TrackingUrlTemplate);
        if (template is not null)
        {
            if (template.Length > TemplateMaxLength)
            {
                errors.Add(new ValidationError(
                    "trackingUrlTemplate",
                    $"Keep the tracking address to {TemplateMaxLength} characters or fewer."));
            }
            else if (!template.Contains(Courier.TrackingNumberPlaceholder, StringComparison.Ordinal))
            {
                // The database's CK_Couriers_TrackingUrlTemplate refuses this too. It is checked here so the
                // staff member gets a sentence naming the field rather than a 500 out of a constraint — and
                // it is checked *there* so no future write path can skip it (doc 08 §1.6).
                errors.Add(new ValidationError(
                    "trackingUrlTemplate",
                    $"The tracking address must contain {TemplateExample} where the tracking number goes. "
                    + "Leave it empty if this courier has no online tracking — the number is then shown "
                    + "as text."));
            }
            else if (!IsHttps(template))
            {
                errors.Add(new ValidationError(
                    "trackingUrlTemplate",
                    "The tracking address must start with https://. The link is sent to customers, and a "
                    + "plain http:// one would be blocked or warned about in their browser."));
            }
        }

        var phone = Clean(command.Phone);
        if (phone is { Length: > PhoneMaxLength })
        {
            errors.Add(new ValidationError(
                "phone",
                $"Keep the telephone number to {PhoneMaxLength} characters or fewer."));
        }
        else if (phone is not null && !IsStorableAscii(phone))
        {
            errors.Add(new ValidationError(
                "phone",
                "Use digits, spaces, brackets, + and - only. The column cannot store other characters and "
                + "would turn them into question marks."));
        }

        if (errors.Count > 0)
        {
            return new SaveCourierResult.Invalid(errors);
        }

        var draft = new CourierDraft(name!, template, phone, command.IsActive, command.SortOrder);
        var now = clock.UtcNow.UtcDateTime;

        var outcome = command.CourierId is { } courierId
            ? await store.UpdateAsync(courierId, draft, command.Actor, now, cancellationToken)
            : await store.CreateAsync(draft, command.Actor, now, cancellationToken);

        return outcome switch
        {
            CourierWriteResult.Applied applied => new SaveCourierResult.Applied(applied.CourierId),

            CourierWriteResult.NotFound => new SaveCourierResult.NotFound(),

            CourierWriteResult.Duplicate duplicate => new SaveCourierResult.Invalid(
            [
                new ValidationError(
                    "name",
                    $"There is already a courier called “{duplicate.Name}”. Edit that one, or "
                    + "give this one a different name."),
            ]),

            _ => throw new InvalidOperationException($"Unhandled courier outcome {outcome.GetType().Name}."),
        };
    }

    /// <summary>
    /// Whether <paramref name="template"/> is an absolute <c>https</c> URL.
    /// </summary>
    /// <remarks>
    /// <b>Parsed with the placeholder still in it, which <see cref="Uri"/> tolerates</b> — braces are not
    /// legal in a URI but <see cref="UriKind.Absolute"/> parsing accepts them in the query and the path, so
    /// there is no need to substitute a dummy number first. The check is on the scheme only: the host is
    /// the client's carrier and this code has no business having an opinion about which ones exist.
    /// </remarks>
    private static bool IsHttps(string template) =>
        Uri.TryCreate(template, UriKind.Absolute, out var uri)
        && string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal);

    /// <summary>
    /// Whether every character of <paramref name="value"/> survives a <c>varchar</c> column.
    /// </summary>
    /// <remarks>
    /// Printable ASCII only. It is a storability check and not a telephone-number format rule: Sri Lankan
    /// numbers are written several ways and refusing one of them would be a box a staff member could not
    /// get past, which is the argument <c>SettingKeys.Bank.AccountNumber</c> records about the same kind of
    /// field.
    /// </remarks>
    private static bool IsStorableAscii(string value) =>
        value.All(character => character is >= ' ' and <= '~');

    /// <summary>
    /// Trims, and treats whitespace as absent — <c>" "</c> in a box is a staff member who cleared it, not
    /// a courier called space.
    /// </summary>
    private static string? Clean(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>What the API should answer.</summary>
public abstract record SaveCourierResult
{
    private SaveCourierResult()
    {
    }

    /// <summary>Saved. 200 for an edit, 201 for a new one.</summary>
    public sealed record Applied(int CourierId) : SaveCourierResult;

    /// <summary>No such courier. 404.</summary>
    public sealed record NotFound : SaveCourierResult;

    /// <summary>400, naming the field. A duplicate name arrives here too, on <c>name</c>.</summary>
    public sealed record Invalid(IReadOnlyList<ValidationError> Errors) : SaveCourierResult;
}
