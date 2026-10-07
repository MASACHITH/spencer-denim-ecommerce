using System.Globalization;
using SpencerDenim.Application.Abstractions.Auditing;
using SpencerDenim.Application.Content.Abstractions;
using SpencerDenim.Application.Content.Admin;
using SpencerDenim.Application.Content.Contact;
using SpencerDenim.Domain.Auditing;
using SpencerDenim.Domain.Content;
using SpencerDenim.UnitTests.Application.Content.Admin;

namespace SpencerDenim.UnitTests.Application.Content;

/// <summary>
/// The contact enquiry (GLB-10, doc 11 P15 part B): the public submission and the inbox's writes.
/// </summary>
public sealed class ContactEnquiryTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 7, 9, 30, 0, TimeSpan.Zero);

    private static readonly AdminActor Staff = new(7, "staff@example.com", "203.0.113.9", "agent", "corr");

    private readonly FakeContactEnquiryStore _store = new();
    private readonly CountingThrottle _throttle = new();
    private readonly RecordingAuditLogWriter _audit = new();

    private SubmitContactEnquiryCommandHandler Submit() =>
        new(_store, _throttle, new FixedClock(Now));

    private ContactEnquiryWriteHandlers Inbox() => new(_store, _audit, new FixedClock(Now));

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static SubmitContactEnquiryCommand Enquiry(
        string? name = "Nimal Perera",
        string? email = "nimal@example.com",
        string? phone = null,
        string? subject = "A question about sizing",
        string? message = "Is the linen shirt true to size?",
        string? orderNumber = null,
        string? honeypot = null,
        string? ip = "203.0.113.5") =>
        new(name, email, phone, subject, message, orderNumber, honeypot, ip);

    // ---- the submission --------------------------------------------------------------------------

    [Fact]
    public async Task A_valid_enquiry_is_stored_as_New_with_the_submitters_address()
    {
        var result = await Submit().HandleAsync(Enquiry(phone: "077 123 4567", orderNumber: "cw-2026-0042"), Token);

        Assert.Equal(ContactEnquiryOutcome.Saved, result.Outcome);

        var stored = Assert.Single(_store.Submissions);
        Assert.Equal("Nimal Perera", stored.Name);
        Assert.Equal("nimal@example.com", stored.Email);
        Assert.Equal("077 123 4567", stored.Phone);
        Assert.Equal("Is the linen shirt true to size?", stored.Message);
        Assert.Equal(ContactSubmissionStatus.New, stored.Status);
        Assert.Equal("203.0.113.5", stored.IpAddress);
        Assert.Equal(Now.UtcDateTime, stored.CreatedAtUtc);

        // Upper-cased, because that is how sales.Orders.OrderNumber is written and a staff member
        // searching for it should find it.
        Assert.Equal("CW-2026-0042", stored.OrderNumber);
    }

    /// <summary>
    /// An optional field left blank is stored as NULL, not as an empty string. Two spellings of "not
    /// given" in one column is how a report ends up with a blank row that is not blank.
    /// </summary>
    [Fact]
    public async Task An_optional_field_left_blank_is_stored_as_null()
    {
        await Submit().HandleAsync(Enquiry(phone: "   ", subject: "", orderNumber: "  "), Token);

        var stored = Assert.Single(_store.Submissions);
        Assert.Null(stored.Phone);
        Assert.Null(stored.Subject);
        Assert.Null(stored.OrderNumber);
    }

    /// <summary>
    /// Doc 11 §7 G-06's honeypot. **The answer is the same one a real submission gets**, because a 400
    /// naming the field, or a different sentence, or a different status would each tell a script exactly
    /// which field to leave alone — and the whole value of the technique is that it cannot tell.
    /// </summary>
    [Fact]
    public async Task A_filled_honeypot_is_accepted_and_not_stored()
    {
        var result = await Submit().HandleAsync(Enquiry(honeypot: "http://spam.example"), Token);

        Assert.Equal(ContactEnquiryOutcome.Discarded, result.Outcome);
        Assert.Empty(result.Errors);
        Assert.Empty(_store.Submissions);

        // And it did not spend a permit either: the throttle is asked after the honeypot, so a spam run
        // cannot exhaust the window a genuine visitor needs.
        Assert.Equal(0, _throttle.Attempts);
    }

    /// <summary>A honeypot of whitespace is a browser being tidy, not a bot. It is stored normally.</summary>
    [Fact]
    public async Task A_honeypot_of_whitespace_is_not_treated_as_spam()
    {
        var result = await Submit().HandleAsync(Enquiry(honeypot: "   "), Token);

        Assert.Equal(ContactEnquiryOutcome.Saved, result.Outcome);
        Assert.Single(_store.Submissions);
    }

    /// <summary>
    /// Validate, then honeypot, then take a permit, then write — and the order is the point. A mistyped
    /// email must cost nothing, because until W0-15 configures the proxy's forwarded headers every
    /// visitor may share one rate-limit partition, so those permits are the whole site's.
    /// </summary>
    [Fact]
    public async Task A_refused_enquiry_spends_no_permit()
    {
        var result = await Submit().HandleAsync(Enquiry(email: "not-an-address"), Token);

        Assert.Equal(ContactEnquiryOutcome.Invalid, result.Outcome);
        Assert.Equal(0, _throttle.Attempts);
        Assert.Empty(_store.Submissions);
    }

    [Fact]
    public async Task A_caller_out_of_permits_is_rate_limited_and_nothing_is_written()
    {
        _throttle.Allow = false;

        var result = await Submit().HandleAsync(Enquiry(), Token);

        Assert.Equal(ContactEnquiryOutcome.RateLimited, result.Outcome);
        Assert.Empty(_store.Submissions);
    }

    /// <summary>Every refused field at once, so the form can mark all of them in one round trip.</summary>
    [Fact]
    public async Task Every_refused_field_is_reported_together()
    {
        var result = await Submit().HandleAsync(
            Enquiry(name: "  ", email: "nope", phone: "abc", message: ""),
            Token);

        Assert.Equal(ContactEnquiryOutcome.Invalid, result.Outcome);

        var fields = result.Errors.Select(error => error.Field).Order(StringComparer.Ordinal).ToList();
        Assert.Equal(["email", "message", "name", "phone"], fields);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("a")]
    public async Task A_message_that_says_nothing_is_refused(string? message)
    {
        var result = await Submit().HandleAsync(Enquiry(message: message), Token);

        Assert.Equal(ContactEnquiryOutcome.Invalid, result.Outcome);
        Assert.Contains(result.Errors, error => error.Field == "message");
    }

    /// <summary>
    /// The length limit names the number, because a visitor who has written four thousand characters
    /// needs to know how much to cut rather than being told "too long".
    /// </summary>
    [Fact]
    public async Task An_over_long_message_is_refused_with_the_limit_in_the_message()
    {
        var result = await Submit().HandleAsync(
            Enquiry(message: new string('x', ContactSubmission.MessageMaxLength + 1)),
            Token);

        var error = Assert.Single(result.Errors, candidate => candidate.Field == "message");
        Assert.Contains(
            ContactSubmission.MessageMaxLength.ToString(CultureInfo.InvariantCulture),
            error.Message,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// A telephone number is optional, and the shapes people actually type are accepted — the shop ships
    /// island-wide and takes enquiries from abroad, so a Sri Lankan-only pattern would refuse real ones.
    /// </summary>
    [Theory]
    [InlineData("077 123 4567")]
    [InlineData("+94 77 123 4567")]
    [InlineData("(011) 234-5678")]
    [InlineData("0112345678")]
    [InlineData(null)]
    [InlineData("")]
    public async Task A_plausible_or_absent_telephone_number_is_accepted(string? phone)
    {
        var result = await Submit().HandleAsync(Enquiry(phone: phone), Token);

        Assert.Equal(ContactEnquiryOutcome.Saved, result.Outcome);
    }

    [Theory]
    [InlineData("not a number")]
    [InlineData("12345")]
    [InlineData("0771234567890123456789")]
    [InlineData("077-123-4567; DROP TABLE")]
    public async Task An_implausible_telephone_number_is_refused(string phone)
    {
        var result = await Submit().HandleAsync(Enquiry(phone: phone), Token);

        Assert.Equal(ContactEnquiryOutcome.Invalid, result.Outcome);
        Assert.Contains(result.Errors, error => error.Field == "phone");
    }

    /// <summary>
    /// **No error message echoes anything the caller sent.** Doc 11 P8 records that the message may hold
    /// bank account details, and an error body that quoted the offending value would put them in a
    /// response, a browser cache and very likely a screenshot.
    /// </summary>
    [Fact]
    public async Task No_error_message_echoes_what_the_visitor_sent()
    {
        const string Secret = "my account is 1234567890 at Sampath";

        var result = await Submit().HandleAsync(
            Enquiry(email: "nope", phone: "nonsense", message: Secret),
            Token);

        foreach (var error in result.Errors)
        {
            Assert.DoesNotContain("1234567890", error.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("Sampath", error.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("nonsense", error.Message, StringComparison.Ordinal);
        }
    }

    // ---- the inbox -------------------------------------------------------------------------------

    /// <summary>Doc 11 P15 step 7: reading the enquiry is what moves it to Read.</summary>
    [Fact]
    public async Task Opening_a_new_enquiry_marks_it_read_and_records_who()
    {
        var submission = _store.Seed(1, ContactSubmissionStatus.New);

        var outcome = await Inbox().HandleAsync(new OpenContactSubmissionCommand(1, Staff), Token);

        Assert.Equal(ContactSubmissionWriteOutcome.Done, outcome);
        Assert.Equal(ContactSubmissionStatus.Read, submission.Status);
        Assert.Equal(7, submission.HandledByUserId);
        Assert.Equal(Now.UtcDateTime, submission.HandledAtUtc);
    }

    /// <summary>
    /// Opening one that is already Replied or Spam changes nothing.
    /// <para>
    /// Opening a Spam enquiry to check it really was spam must not quietly reclassify it, and opening a
    /// Replied one must not take the reply off it — which is what a blanket "set Read on open" would do.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(ContactSubmissionStatus.Read)]
    [InlineData(ContactSubmissionStatus.Replied)]
    [InlineData(ContactSubmissionStatus.Spam)]
    public async Task Opening_an_enquiry_that_is_not_new_changes_nothing(ContactSubmissionStatus status)
    {
        var submission = _store.Seed(1, status);

        var outcome = await Inbox().HandleAsync(new OpenContactSubmissionCommand(1, Staff), Token);

        Assert.Equal(ContactSubmissionWriteOutcome.Unchanged, outcome);
        Assert.Equal(status, submission.Status);
        Assert.Empty(_audit.Entries);
    }

    [Fact]
    public async Task Marking_replied_records_who_and_when_and_writes_a_status_audit_row()
    {
        var submission = _store.Seed(1, ContactSubmissionStatus.Read);

        var outcome = await Inbox().HandleAsync(
            new SetContactSubmissionStatusCommand(1, ContactSubmissionStatus.Replied, Staff),
            Token);

        Assert.Equal(ContactSubmissionWriteOutcome.Done, outcome);
        Assert.Equal(ContactSubmissionStatus.Replied, submission.Status);
        Assert.Equal(7, submission.HandledByUserId);

        var entry = Assert.Single(_audit.Entries);
        Assert.Equal(AuditActions.StatusChange, entry.Action);
        Assert.Equal(ContentAuditTrail.ContactSubmissionEntityType, entry.EntityType);
        Assert.Contains("Replied", entry.AfterJson, StringComparison.Ordinal);
    }

    /// <summary>
    /// Idempotent: two staff marking the same enquiry Replied is a no-op rather than the second one's
    /// name replacing the first one's.
    /// </summary>
    [Fact]
    public async Task Marking_a_status_it_already_has_changes_nothing()
    {
        var submission = _store.Seed(1, ContactSubmissionStatus.Replied);
        submission.HandledByUserId = 99;

        var outcome = await Inbox().HandleAsync(
            new SetContactSubmissionStatusCommand(1, ContactSubmissionStatus.Replied, Staff),
            Token);

        Assert.Equal(ContactSubmissionWriteOutcome.Unchanged, outcome);
        Assert.Equal(99, submission.HandledByUserId);
        Assert.Empty(_audit.Entries);
    }

    /// <summary>
    /// <b>The audit row for a deletion carries no copy of the message body</b> — doc 08 §3.7 in as many
    /// words, and the reason the deletion exists at all is that the body held something that must stop
    /// existing. The name, the address and the telephone number are left out on the same reasoning.
    /// </summary>
    [Fact]
    public async Task Deleting_an_enquiry_audits_the_reason_and_no_part_of_the_message()
    {
        _store.Seed(
            1,
            ContactSubmissionStatus.Read,
            name: "Nimal Perera",
            email: "nimal@example.com",
            message: "My card number is 4111111111111111 and my bank is Sampath");

        var outcome = await Inbox().HandleAsync(
            new DeleteContactSubmissionCommand(1, "Contains card details", Staff),
            Token);

        Assert.Equal(ContactSubmissionWriteOutcome.Done, outcome);
        Assert.Empty(_store.Submissions);

        var entry = Assert.Single(_audit.Entries);
        Assert.Equal(AuditActions.Delete, entry.Action);
        Assert.Equal(ContentAuditTrail.ContactSubmissionEntityType, entry.EntityType);
        Assert.Equal("1", entry.EntityId);
        Assert.Contains("Contains card details", entry.AfterJson, StringComparison.Ordinal);

        var trail = (entry.BeforeJson ?? "") + (entry.AfterJson ?? "");
        Assert.DoesNotContain("4111111111111111", trail, StringComparison.Ordinal);
        Assert.DoesNotContain("Sampath", trail, StringComparison.Ordinal);
        Assert.DoesNotContain("Nimal", trail, StringComparison.Ordinal);
        Assert.DoesNotContain("nimal@example.com", trail, StringComparison.Ordinal);
    }

    /// <summary>
    /// A permanent deletion of customer data with no stated reason is not a trail, so the reason is
    /// required — and nothing is deleted while it is missing.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("x")]
    public async Task A_deletion_without_a_reason_is_refused(string? reason)
    {
        _store.Seed(1, ContactSubmissionStatus.Read);

        var outcome = await Inbox().HandleAsync(
            new DeleteContactSubmissionCommand(1, reason, Staff),
            Token);

        Assert.Equal(ContactSubmissionWriteOutcome.ReasonRequired, outcome);
        Assert.Single(_store.Submissions);
        Assert.Empty(_audit.Entries);
    }

    [Fact]
    public async Task An_enquiry_that_does_not_exist_is_not_found()
    {
        Assert.Equal(
            ContactSubmissionWriteOutcome.NotFound,
            await Inbox().HandleAsync(new OpenContactSubmissionCommand(404, Staff), Token));

        Assert.Equal(
            ContactSubmissionWriteOutcome.NotFound,
            await Inbox().HandleAsync(
                new SetContactSubmissionStatusCommand(404, ContactSubmissionStatus.Spam, Staff),
                Token));

        Assert.Equal(
            ContactSubmissionWriteOutcome.NotFound,
            await Inbox().HandleAsync(
                new DeleteContactSubmissionCommand(404, "Contains card details", Staff),
                Token));
    }

    /// <summary>
    /// An in-memory <see cref="IContactEnquiryStore"/>. The hard delete is a real removal, as
    /// <c>ExecuteDeleteAsync</c> is: there is no soft-delete flag for a test to be fooled by.
    /// </summary>
    private sealed class FakeContactEnquiryStore : IContactEnquiryStore
    {
        private int _nextId = 1;

        public List<ContactSubmission> Submissions { get; } = [];

        public Task AddAsync(ContactSubmission submission, CancellationToken cancellationToken)
        {
            submission.Id = _nextId++;
            Submissions.Add(submission);
            return Task.CompletedTask;
        }

        public Task<ContactSubmission?> FindAsync(int submissionId, CancellationToken cancellationToken) =>
            Task.FromResult(Submissions.FirstOrDefault(submission => submission.Id == submissionId));

        public Task SaveAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<bool> DeleteAsync(int submissionId, CancellationToken cancellationToken)
        {
            var found = Submissions.FirstOrDefault(submission => submission.Id == submissionId);
            if (found is null)
            {
                return Task.FromResult(false);
            }

            Submissions.Remove(found);
            return Task.FromResult(true);
        }

        public ContactSubmission Seed(
            int id,
            ContactSubmissionStatus status,
            string name = "A visitor",
            string email = "visitor@example.com",
            string message = "A question.")
        {
            var submission = new ContactSubmission
            {
                Id = id,
                Name = name,
                Email = email,
                Message = message,
                Status = status,
                CreatedAtUtc = Now.UtcDateTime,
            };

            Submissions.Add(submission);
            _nextId = Math.Max(_nextId, id + 1);

            return submission;
        }
    }

    /// <summary>A throttle that counts how often it was asked, which is half of what these tests check.</summary>
    private sealed class CountingThrottle : IContactEnquiryThrottle
    {
        public bool Allow { get; set; } = true;

        public int Attempts { get; private set; }

        public TimeSpan Window => TimeSpan.FromMinutes(10);

        public bool TryAcquire(string? partitionKey)
        {
            Attempts++;
            return Allow;
        }
    }
}
