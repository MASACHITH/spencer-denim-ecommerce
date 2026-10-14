namespace SpencerDenim.Domain.Identity;

/// <summary>
/// The one extra rule a customer password has to satisfy beyond ASP.NET Identity's own validators
/// (ACC-01).
/// </summary>
/// <remarks>
/// <b>Neither doc 08 nor doc 11 states a password rule, so this is a decision and is named as one.</b>
/// Identity's defaults are what the repository already runs on — <c>IdentityRegistration</c> configures
/// <c>IdentityOptions.Lockout</c>, <c>User</c> and <c>SignIn</c> and leaves <c>Password</c> alone — which
/// means six characters with a digit, a lower-case letter, an upper-case letter and a symbol.
/// <para>
/// <b>Six is too short for a shop's customer account in 2026, and the composition rules are not what
/// fixes that.</b> <c>Pa$$w1</c> satisfies all four classes. Length is the property that actually costs an
/// attacker work, so this adds a minimum of <see cref="MinimumLength"/> and adds nothing else.
/// </para>
/// <para>
/// <b>Why not change <c>IdentityOptions.Password</c> instead.</b> Those options are global and are
/// therefore the <i>staff</i> rules as well, in a startup path Tharanga has three open PRs against
/// (#142, #144, #146). Raising a shared minimum under somebody else's review is a change for them to
/// make with their own tests; raising the customer side is this task's to make. <b>The consequence is
/// that staff passwords are still six characters</b>, which is the weaker of the two and the one that
/// matters more. That is in the task report for the owner rather than fixed here.
/// </para>
/// <para>
/// <b>It lives in Domain so that one number serves three readers</b>: the validator, the API's own
/// description of the field, and the storefront's hint. A form that promises eight and an API that
/// refuses nine is the shape of defect #126 shipped (a <c>maxlength</c> of 120 against a 100-character
/// column), so the number is not written twice on the server; the front end reads it from the
/// registration endpoint's problem details rather than holding a second copy.
/// </para>
/// </remarks>
public static class CustomerPasswordPolicy
{
    /// <summary>
    /// The fewest characters a customer password may have.
    /// </summary>
    /// <remarks>
    /// Ten. Long enough to be worth more than the four composition classes Identity already requires,
    /// short enough to type on a telephone, and a round number a shopper can be told in one clause.
    /// </remarks>
    public const int MinimumLength = 10;

    /// <summary>
    /// The sentence a shopper is shown, in one place, so the form's hint and the server's refusal cannot
    /// disagree about the rule.
    /// </summary>
    /// <remarks>
    /// It names every rule that is actually enforced — the four classes are Identity's and are enforced
    /// whether or not they are mentioned, so leaving them out of the hint would mean a shopper meeting
    /// the stated rule and being refused.
    /// </remarks>
    public const string Requirement =
        "Use at least 10 characters, including an upper-case letter, a lower-case letter, a number and a "
        + "symbol.";

    /// <summary>
    /// Whether <paramref name="password"/> is long enough. Identity's validators decide everything else.
    /// </summary>
    public static bool IsLongEnough(string? password) =>
        password is not null && password.Length >= MinimumLength;
}
