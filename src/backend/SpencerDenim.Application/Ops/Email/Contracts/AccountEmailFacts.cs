namespace SpencerDenim.Application.Ops.Email.Contracts;

/// <summary>
/// Who an account email is for (doc 11 P11's notification list).
/// </summary>
/// <remarks>
/// <b>Separate from <c>OrderEmailAddressee</c> rather than shared with it.</b> That type is built from an
/// order's own name snapshot and is part of a contract six order emails pass around; this one is built
/// from <c>identity.Users</c>. Sharing it would make every change to one of them a change to the other,
/// and the two have different lifetimes — an order's name is frozen, a customer's is not.
/// </remarks>
/// <param name="Email">
/// The address the message goes to. For <see cref="AccountEmailFacts.EmailConfirmation"/> on an email
/// change this is the <b>new</b> address (doc 11 P11 step 10); nothing is sent to the old one, because
/// doc 08 §3.1 R10's old-address notice is a <i>staff</i> rule and a customer-side equivalent is not in
/// doc 08, doc 11 or the scope baseline. It is in the task report as a gap worth the owner's decision.
/// </param>
public sealed record AccountEmailAddressee(string Email, string FirstName, string? LastName)
{
    /// <summary>
    /// The display name for the <c>To</c> header, or <see langword="null"/> when there is no name —
    /// never a placeholder like "Customer", which would be a name we invented.
    /// </summary>
    public string? DisplayName
    {
        get
        {
            var name = string.Join(' ', new[] { FirstName, LastName }
                .Where(part => !string.IsNullOrWhiteSpace(part))
                .Select(part => part!.Trim()));

            return name.Length == 0 ? null : name;
        }
    }

    /// <summary>
    /// How the body opens: "Hello Nimal," or, with no first name, "Hello," — the greeting degrades
    /// rather than guessing.
    /// </summary>
    public string Greeting =>
        string.IsNullOrWhiteSpace(FirstName) ? "Hello," : $"Hello {FirstName.Trim()},";
}

/// <summary>
/// What an account email is composed from: the recipient, the one-time link where there is one, and the
/// queueing transaction's own instant.
/// </summary>
/// <remarks>
/// <b><see cref="Link"/> is nullable, and that is not a defect.</b> Every link in every email is built
/// from <c>Email:StorefrontBaseUrl</c>, which the shop may legitimately not have configured
/// (<see cref="EmailOptions.StorefrontBaseUrl"/>: "an unset value costs the links and nothing else"). For
/// an order email that costs a convenience. For a confirmation or a reset it costs the <i>whole
/// message</i> — a reset email with no reset link is not a reset email — so the two token-bearing
/// composers refuse to compose at all without one, and their callers skip queueing rather than send a
/// message a customer cannot act on. See <c>AccountEmailOutbox</c>, where the refusal lives, and the task
/// report, which names the deployment consequence.
/// </remarks>
/// <param name="NowUtc">
/// The transaction's own "now", passed in rather than read from a clock inside the queue, so the outbox
/// row's <c>CreatedAtUtc</c> and <c>NextAttemptAtUtc</c> are the same instant as the user row it commits
/// beside.
/// </param>
public sealed record AccountEmailFacts(AccountEmailAddressee To, string? Link, DateTime NowUtc)
{
    /// <summary>How long the link in the body is good for, in whole hours, for the body to say so.</summary>
    /// <remarks>
    /// <b>The body states the window because an expired link with no explanation reads as a broken
    /// site.</b> The number is the one the token was actually minted with, passed through rather than
    /// written into the copy, so the sentence cannot drift from
    /// <c>CustomerSecurityOptions.PasswordResetTokenHours</c>.
    /// </remarks>
    public int LinkValidHours { get; init; }
}
