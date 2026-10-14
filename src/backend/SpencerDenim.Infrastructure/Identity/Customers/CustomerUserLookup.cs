using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using SpencerDenim.Domain.Identity;
using SpencerDenim.Infrastructure.Persistence;

namespace SpencerDenim.Infrastructure.Identity.Customers;

/// <summary>
/// Finding a user for a customer flow, and paying the same price when there is nobody to find.
/// </summary>
/// <remarks>
/// <b>Doc 08 §3.1 DB10's first two bullets, in one place.</b> "Customer flows (register, login,
/// forgot/reset password, confirm email, change email) find users through use-case queries filtered
/// <c>UserType = Customer</c> <i>before</i> calling <c>UserManager</c>. Staff accounts are invisible to
/// them." Every customer flow goes through <see cref="FindCustomerByEmailAsync"/> or
/// <see cref="ClassifyAsync"/>, so there is no path on which a customer endpoint can reach a staff row.
/// <para>
/// <b>And the timing half, which is the part a reviewer cannot see in the control flow.</b> #101 is open
/// on the staff routes for the same reason: a sign-in form is a timing oracle by default, because the
/// early-return paths — unknown address, wrong user type, deactivated account — skip the password hash
/// that dominates a real wrong-password attempt. <see cref="SpendPasswordHashCost"/> runs the configured
/// hasher against a fixed dummy hash on exactly those paths, so the cheap answers cost what the expensive
/// one costs. <c>AdminAuthenticator</c> does the same thing and this is deliberately a second
/// implementation rather than a shared one: the admin version is a private method on a class in the middle
/// of #142/#144/#146, and the dummy subject has to be hashed by <i>this</i> flow's hasher.
/// </para>
/// </remarks>
internal sealed class CustomerUserLookup(
    AppDbContext dbContext,
    UserManager<AppUser> userManager,
    IPasswordHasher<AppUser> passwordHasher)
{
    /// <summary>
    /// The subject and hash the dummy verification runs against.
    /// </summary>
    /// <remarks>
    /// <b>Computed once, in a static initialiser.</b> Hashing at module load rather than per request is
    /// what stops the equalisation adding variance of its own — a PBKDF2 hash takes long enough that
    /// computing it inside the request would make the "no such user" path <i>slower</i> than a real
    /// attempt, which is the same oracle with its sign flipped. <c>AdminAuthenticator</c> makes the same
    /// note.
    /// </remarks>
    private static readonly AppUser DummyHashSubject = new() { UserName = "no-such-customer@invalid" };

    private static readonly string DummyPasswordHash =
        new PasswordHasher<AppUser>().HashPassword(DummyHashSubject, "Dummy-Password-Never-Used-1!");

    /// <summary>Which of doc 08 §3.1 DB10's three cases an address is.</summary>
    public enum EmailOwner
    {
        /// <summary>No account at all. Registration may create one; every other flow does nothing.</summary>
        Nobody = 0,

        /// <summary>An active customer. The only case that receives an email.</summary>
        Customer = 1,

        /// <summary>
        /// A staff account (doc 08 D7: "a staff member who shops uses a different email"). Nothing is
        /// sent, nothing is created, and the customer endpoints cannot reach the row.
        /// </summary>
        Staff = 2,
    }

    /// <summary>
    /// The <b>active customer</b> with this address, or <see langword="null"/>. A staff row, a
    /// deactivated row, an anonymised row and no row at all are all <see langword="null"/>.
    /// </summary>
    /// <remarks>
    /// <b><c>IsActive</c> is part of the filter, not a check the caller makes afterwards.</b> A
    /// deactivated or anonymised account must be as invisible to a customer flow as a staff one: doc 08
    /// §3.1's anonymisation sets <c>IsActive = 0</c>, blanks the password hash and rewrites the address
    /// to <c>anon-{id}@anonymised.invalid</c>, and a reset email to a reactivated anonymised row would be
    /// a message about somebody who asked to be forgotten.
    /// <para>
    /// <b>Tracked, not <c>AsNoTracking</c>.</b> Every caller either changes the row or hands it to
    /// <c>UserManager</c>, which needs the tracked instance to save through.
    /// </para>
    /// </remarks>
    public async Task<AppUser?> FindCustomerByEmailAsync(string email, CancellationToken cancellationToken)
    {
        var normalised = Normalise(email);

        return normalised is null
            ? null
            : await dbContext.Users
                .Where(user =>
                    user.NormalizedEmail == normalised
                    && user.UserType == UserType.Customer
                    && user.IsActive)
                .FirstOrDefaultAsync(cancellationToken);
    }

    /// <summary>
    /// The <b>active customer</b> with this id, or <see langword="null"/> — the same filter as
    /// <see cref="FindCustomerByEmailAsync"/>, applied to a user id that came off a session cookie.
    /// </summary>
    /// <remarks>
    /// <b>The id on a cookie is not a licence to skip the type filter.</b> The cookie is signed, so the
    /// id is ours — but the account behind it may have been deactivated or anonymised since, and doc 08
    /// §3.1 DB10 asks the scheme to reject a staff principal "both at sign-in and on security-stamp
    /// revalidation", which is this read.
    /// </remarks>
    public async Task<AppUser?> FindCustomerByIdAsync(int userId, CancellationToken cancellationToken) =>
        await dbContext.Users
            .Where(user => user.Id == userId && user.UserType == UserType.Customer && user.IsActive)
            .FirstOrDefaultAsync(cancellationToken);

    /// <summary>
    /// Which of the three cases this address is, for registration — the one flow that has to tell a
    /// staff address from an unknown one, because the two send different things (one message, nothing).
    /// </summary>
    /// <remarks>
    /// <b>It looks at every user type on purpose, and it is the only method here that does.</b> Doc 08
    /// D7 makes an address unique across the whole store, so registration must refuse a staff address —
    /// but it must refuse it the same way it refuses a taken customer address, which is by answering the
    /// caller with one outcome enum and never with a different HTTP status.
    /// <para>
    /// <b>A deactivated or anonymised customer row counts as <see cref="EmailOwner.Customer"/>.</b> The
    /// address is taken: a unique index says so, and registration cannot create a second row for it. What
    /// it must not do is send an <c>AccountExists</c> message to it, so the caller checks
    /// <see cref="FindCustomerByEmailAsync"/> for the <i>active</i> row before queueing one.
    /// </remarks>
    public async Task<EmailOwner> ClassifyAsync(string email, CancellationToken cancellationToken)
    {
        var normalised = Normalise(email);
        if (normalised is null)
        {
            return EmailOwner.Nobody;
        }

        var type = await dbContext.Users
            .Where(user => user.NormalizedEmail == normalised)
            .Select(user => (UserType?)user.UserType)
            .FirstOrDefaultAsync(cancellationToken);

        return type switch
        {
            UserType.Customer => EmailOwner.Customer,
            UserType.Staff => EmailOwner.Staff,
            _ => EmailOwner.Nobody,
        };
    }

    /// <summary>
    /// Spends what a real password verification costs, and discards the answer.
    /// </summary>
    /// <remarks>
    /// Called on every path that answers "invalid credentials" without having verified a password:
    /// unknown address, staff address, deactivated account, and an account with no password hash at all
    /// (an anonymised row). <see cref="DummyPasswordHash"/> matches nothing, so the return value is
    /// meaningless by construction — which is why it is not returned.
    /// </remarks>
    public void SpendPasswordHashCost(string password) =>
        passwordHasher.VerifyHashedPassword(DummyHashSubject, DummyPasswordHash, password);

    /// <summary>
    /// The address in the form <c>identity.Users.NormalizedEmail</c> holds, using Identity's own
    /// normaliser so the comparison cannot disagree with what <c>UserManager</c> wrote.
    /// </summary>
    /// <remarks>
    /// Null for a blank or whitespace-only address, so a request with an empty field becomes "nobody"
    /// rather than a query for <c>NormalizedEmail = ''</c> — which could in principle match a row written
    /// by something else.
    /// </remarks>
    public string? Normalise(string? email) =>
        string.IsNullOrWhiteSpace(email) ? null : userManager.NormalizeEmail(email.Trim());
}
