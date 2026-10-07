using SpencerDenim.Application.Abstractions.Messaging;
using SpencerDenim.Application.Abstractions.Results;
using SpencerDenim.Application.Abstractions.Time;
using SpencerDenim.Application.Content.Abstractions;
using SpencerDenim.Domain.Content;

namespace SpencerDenim.Application.Content.Contact;

/// <summary>
/// A visitor's enquiry from the Contact form (GLB-10, doc 11 P15 step 5).
/// </summary>
/// <param name="Name">As typed.</param>
/// <param name="Email">As typed. Nothing is ever sent to it (doc 11 P15: notifications are none in v1).</param>
/// <param name="Phone">Optional.</param>
/// <param name="Subject">Optional in the column (doc 08 §3.7 allows NULL); the form asks for it.</param>
/// <param name="Message">The enquiry. Plain text, stored and shown as plain text.</param>
/// <param name="OrderNumber">Optional, so staff can find the order quickly.</param>
/// <param name="Honeypot">
/// A field no person fills in. Doc 11 §7 G-06 asks for "rate limit + honeypot" as the spam protection, and
/// this is the second half: a filled value means an automated submission, which is <b>accepted with the
/// same answer and not stored</b>.
/// <para>
/// Accepted rather than refused, deliberately. A 400 naming the honeypot teaches a script exactly which
/// field to leave alone, and the whole value of the technique is that the bot cannot tell. So the visitor
/// — real or not — gets the confirmation a real submission gets, and nothing is written.
/// </para>
/// </param>
/// <param name="IpAddress">
/// The real socket peer. Recorded with the enquiry (doc 11 P15 step 6) and used as the rate-limit
/// partition. <c>X-Forwarded-For</c> processing is deliberately off (W1-01 round 3), so a caller cannot
/// write somebody else's address into the record.
/// </param>
public sealed record SubmitContactEnquiryCommand(
    string? Name,
    string? Email,
    string? Phone,
    string? Subject,
    string? Message,
    string? OrderNumber,
    string? Honeypot,
    string? IpAddress);

/// <summary>What happened to an enquiry, as the endpoint has to answer it.</summary>
public enum ContactEnquiryOutcome
{
    /// <summary>Written as <see cref="ContactSubmissionStatus.New"/>.</summary>
    Saved = 0,

    /// <summary>One or more fields were refused; <see cref="ContactEnquiryResult.Errors"/> says which.</summary>
    Invalid = 1,

    /// <summary>Too many submissions from this caller in the window. Nothing was written.</summary>
    RateLimited = 2,

    /// <summary>
    /// The honeypot was filled in. Nothing was written, and the endpoint answers exactly as it does for
    /// <see cref="Saved"/> — see <see cref="SubmitContactEnquiryCommand.Honeypot"/>.
    /// </summary>
    Discarded = 3,
}

/// <summary>
/// What the handler decided. The errors are only ever read for <see cref="ContactEnquiryOutcome.Invalid"/>.
/// </summary>
/// <param name="Outcome">What happened.</param>
/// <param name="Errors">
/// One entry per refused field, so the form can attach each message to its own control (GLB-12: an error
/// a screen reader cannot associate with a field is an error nobody can act on).
/// </param>
public sealed record ContactEnquiryResult(ContactEnquiryOutcome Outcome, IReadOnlyList<ValidationError> Errors)
{
    public static ContactEnquiryResult Saved { get; } = new(ContactEnquiryOutcome.Saved, []);

    public static ContactEnquiryResult Discarded { get; } = new(ContactEnquiryOutcome.Discarded, []);

    public static ContactEnquiryResult RateLimited { get; } = new(ContactEnquiryOutcome.RateLimited, []);

    public static ContactEnquiryResult Invalid(IReadOnlyList<ValidationError> errors) =>
        new(ContactEnquiryOutcome.Invalid, errors);
}

/// <summary>
/// Writes a contact enquiry (GLB-10, doc 11 P15 steps 5–6).
/// <para>
/// <b>Nothing is emailed, and nothing anywhere says otherwise.</b> Doc 11 P15's own line is
/// "Notifications. None in v1 (no subscription confirmation, no enquiry acknowledgement, no staff alert)",
/// and <c>ops.EmailOutbox</c> does not exist in this codebase at all. Checkout shipped a promise of an
/// email that nothing could send and it was a Major finding; the confirmation this produces says the
/// enquiry has been received and that the shop will be in touch, which is true of a staff member reading
/// the inbox.
/// </para>
/// <para>
/// <b>Validate, then honeypot, then take a permit, then write</b> — the order
/// <see cref="IContactEnquiryThrottle"/> explains. A typo must not spend anybody's budget, and in the
/// shared-partition state the deployment is in until W0-15, "anybody" is the whole site.
/// </para>
/// <para>
/// <b>No part of the enquiry is logged.</b> Doc 11 P8 records that a customer may paste bank account
/// details into the message, and §3.7 says even the audit row for a <i>deletion</i> carries no copy of the
/// body. This handler therefore has no logger: there is nothing it could usefully say that would not be a
/// copy of personal data, and a handler that holds an <c>ILogger</c> is a handler somebody adds a
/// diagnostic line to.
/// </para>
/// </summary>
public sealed class SubmitContactEnquiryCommandHandler(
    IContactEnquiryStore store,
    IContactEnquiryThrottle throttle,
    IClock clock)
    : ICommandHandler<SubmitContactEnquiryCommand, ContactEnquiryResult>
{
    public async Task<ContactEnquiryResult> HandleAsync(
        SubmitContactEnquiryCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var errors = Validate(command);
        if (errors.Count > 0)
        {
            return ContactEnquiryResult.Invalid(errors);
        }

        // After validation, so a bot that fills every field including the honeypot still costs nothing,
        // and before the throttle, so it does not spend a permit either.
        if (!string.IsNullOrWhiteSpace(command.Honeypot))
        {
            return ContactEnquiryResult.Discarded;
        }

        if (!throttle.TryAcquire(command.IpAddress))
        {
            return ContactEnquiryResult.RateLimited;
        }

        var submission = new ContactSubmission
        {
            Name = command.Name!.Trim(),
            Email = command.Email!.Trim(),
            Phone = ContactEnquiryRules.NormalisePhone(command.Phone),
            Subject = ContactEnquiryRules.NormaliseOptional(command.Subject, ContactSubmission.SubjectMaxLength),
            Message = command.Message!.Trim(),
            OrderNumber = ContactEnquiryRules.NormaliseOrderNumber(command.OrderNumber),
            Status = ContactSubmissionStatus.New,
            IpAddress = ContactEnquiryRules.NormaliseOptional(command.IpAddress, ContactSubmission.IpAddressMaxLength),
            CreatedAtUtc = clock.UtcNow.UtcDateTime,
        };

        await store.AddAsync(submission, cancellationToken);

        return ContactEnquiryResult.Saved;
    }

    /// <summary>
    /// Every refused field at once, rather than the first one. A form that reports one error per round
    /// trip is a form a visitor abandons, and the shape also means the screen can mark all of them.
    /// </summary>
    private static List<ValidationError> Validate(SubmitContactEnquiryCommand command)
    {
        var errors = new List<ValidationError>();

        var name = command.Name?.Trim();
        if (string.IsNullOrEmpty(name) || name.Length < ContactEnquiryRules.NameMinLength)
        {
            errors.Add(new ValidationError("name", "Tell us your name."));
        }
        else if (name.Length > ContactSubmission.NameMaxLength)
        {
            errors.Add(new ValidationError(
                "name",
                $"Please keep the name to {ContactSubmission.NameMaxLength} characters or fewer."));
        }

        if (!ContactEnquiryRules.IsPlausibleEmail(command.Email))
        {
            errors.Add(new ValidationError(
                "email",
                "Enter an email address we can reply to, for example name@example.com."));
        }

        if (!ContactEnquiryRules.IsPlausiblePhone(command.Phone))
        {
            errors.Add(new ValidationError(
                "phone",
                "That does not look like a telephone number. Leave it empty if you would rather not give one."));
        }

        if (command.Subject is { } subject && subject.Trim().Length > ContactSubmission.SubjectMaxLength)
        {
            errors.Add(new ValidationError(
                "subject",
                $"Please keep the subject to {ContactSubmission.SubjectMaxLength} characters or fewer."));
        }

        var message = command.Message?.Trim();
        if (string.IsNullOrEmpty(message) || message.Length < ContactEnquiryRules.MessageMinLength)
        {
            errors.Add(new ValidationError("message", "Tell us how we can help."));
        }
        else if (message.Length > ContactSubmission.MessageMaxLength)
        {
            // The number, not "too long": a visitor who has written four thousand characters needs to
            // know how much to cut, and the form's own counter shows the same figure.
            errors.Add(new ValidationError(
                "message",
                $"Please keep the message to {ContactSubmission.MessageMaxLength} characters or fewer."));
        }

        if (command.OrderNumber is { } orderNumber
            && orderNumber.Trim().Length > ContactSubmission.OrderNumberMaxLength)
        {
            errors.Add(new ValidationError(
                "orderNumber",
                $"An order number is at most {ContactSubmission.OrderNumberMaxLength} characters."));
        }

        return errors;
    }
}
