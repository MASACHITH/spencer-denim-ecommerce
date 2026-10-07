namespace SpencerDenim.Domain.Content;

/// <summary>
/// <c>content.ContactSubmissions.Status</c> (doc 08 §3.7). Doc 11 P15's status line: "New → Read →
/// Replied, or → Spam". A CK pins the range in the database.
/// </summary>
public enum ContactSubmissionStatus : byte
{
    /// <summary>Just arrived; nobody has opened it.</summary>
    New = 0,

    /// <summary>A staff member has opened it. Set by reading it, not by a button (doc 11 P15 step 7).</summary>
    Read = 1,

    /// <summary>
    /// Answered. The reply itself happens outside the system — by email or by telephone — so this records
    /// only that somebody dealt with it, and who.
    /// </summary>
    Replied = 2,

    /// <summary>Junk. Still stored, so a mistake can be undone and so the retention clock is the same.</summary>
    Spam = 3,
}

/// <summary>
/// <c>content.ContactSubmissions</c> (doc 08 §3.7, GLB-10). One enquiry from the Contact form.
/// <para>
/// <b>This row is personal data and parts of it are the most sensitive free text the shop collects.</b>
/// Doc 11 P15 records the case that decides how it is handled: a customer may paste <b>card or bank
/// account details</b> into <see cref="Message"/>. So:
/// </para>
/// <list type="bullet">
/// <item><description>
/// <see cref="Message"/>, <see cref="Email"/>, <see cref="Phone"/> and <see cref="Name"/> never reach a
/// log line, an audit row or an error message. Doc 08 §3.7: a delete is audited "with the reason, and the
/// audit row holds <b>no copy of the message body</b>".
/// </description></item>
/// <item><description>
/// Reading one needs <c>Customers.View</c>, changing its status <c>Customers.Edit</c>, deleting it
/// <c>Customers.Delete</c> — the <b>Customers</b> module, not <c>Content.*</c>, because this is customer
/// data that happens to arrive through a content page (doc 08 §3.7 G-06).
/// </description></item>
/// <item><description>
/// It is a <b>hard</b> delete, not a soft one. "It must be removed rather than merely hidden" (doc 08
/// §3.7) — a soft-deleted row still holds the bank account number.
/// </description></item>
/// </list>
/// <para>
/// <b>Retention (doc 08 §8.4), and what is built for it.</b> An enquiry is kept two years and its IP
/// address ninety days. Neither purge job is built — the nightly job host is not this task's — but the
/// row is shaped so both are a single set-based statement: <see cref="CreatedAtUtc"/> leads
/// <c>IX_ContactSubmissions_CreatedAtUtc</c> so <c>DELETE TOP (4000) … WHERE CreatedAtUtc &lt; @cutoff</c>
/// seeks rather than scans, and <see cref="IpAddress"/> is <b>nullable</b> so the ninety-day step is an
/// <c>UPDATE … SET IpAddress = NULL</c> that leaves the enquiry itself intact and readable. A
/// <c>NOT NULL</c> column would have forced a sentinel value that every reader then has to know about.
/// </para>
/// </summary>
public sealed class ContactSubmission
{
    /// <summary><c>content.ContactSubmissions.Name</c> is <c>nvarchar(120)</c>.</summary>
    public const int NameMaxLength = 120;

    /// <summary><c>content.ContactSubmissions.Email</c> is <c>nvarchar(256)</c>.</summary>
    public const int EmailMaxLength = 256;

    /// <summary><c>content.ContactSubmissions.Phone</c> is <c>varchar(20)</c>.</summary>
    public const int PhoneMaxLength = 20;

    /// <summary><c>content.ContactSubmissions.Subject</c> is <c>nvarchar(150)</c>.</summary>
    public const int SubjectMaxLength = 150;

    /// <summary><c>content.ContactSubmissions.Message</c> is <c>nvarchar(4000)</c>.</summary>
    public const int MessageMaxLength = 4000;

    /// <summary><c>content.ContactSubmissions.OrderNumber</c> is <c>varchar(20)</c>.</summary>
    public const int OrderNumberMaxLength = 20;

    /// <summary><c>content.ContactSubmissions.IpAddress</c> is <c>varchar(45)</c> — an IPv6 literal fits.</summary>
    public const int IpAddressMaxLength = 45;

    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string Email { get; set; } = string.Empty;

    public string? Phone { get; set; }

    public string? Subject { get; set; }

    /// <summary>
    /// The enquiry itself, as <b>plain text</b>. It is never HTML and is never rendered as HTML: the admin
    /// screen interpolates it, so there is nothing here to sanitise and nothing a visitor can inject.
    /// </summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>
    /// An order number the enquiry is about, as the visitor typed it. Stored as given rather than resolved
    /// to an order id: a visitor may mistype it, and doc 11 P15's reason for the field is that it "lets
    /// staff find it quickly", not that it is a foreign key.
    /// </summary>
    public string? OrderNumber { get; set; }

    public ContactSubmissionStatus Status { get; set; } = ContactSubmissionStatus.New;

    /// <summary>Who last changed the status (doc 11 P15 step 7: "records who and when").</summary>
    public int? HandledByUserId { get; set; }

    public DateTime? HandledAtUtc { get; set; }

    /// <summary>
    /// The submitter's address, for the spam record doc 11 P15 step 6 asks for. Nulled by the ninety-day
    /// retention step (doc 08 §8.4), which is why it is nullable.
    /// </summary>
    public string? IpAddress { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    /// <summary>
    /// Moves the status, stamping who and when. Returns false when it was already there, so a second
    /// reader of the same enquiry does not keep re-stamping it — and so "a staff member replied" does not
    /// quietly become "the last person to open the list".
    /// </summary>
    public bool SetStatus(ContactSubmissionStatus status, int? userId, DateTime utcNow)
    {
        if (Status == status)
        {
            return false;
        }

        Status = status;
        HandledByUserId = userId;
        HandledAtUtc = utcNow;

        return true;
    }
}
