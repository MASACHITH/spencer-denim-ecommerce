namespace SpencerDenim.Application.Admin.Users.Abstractions;

/// <summary>
/// Staff accounts that the shop's own staff cannot switch off, named in server configuration and never in code.
/// <para>
/// <b>Protected</b> (<c>AdminSecurity__ProtectedStaffEmails</c>): the technical owner's account. It cannot be
/// deactivated by anybody, a Super Admin included, and its email address cannot be changed: the protection is
/// keyed on the address, so without the second rule a Super Admin could rename the account and deactivate it
/// under its new name.
/// </para>
/// <para>
/// <b>Guarded</b> (<c>AdminSecurity__GuardedStaffEmails</c>): an account handed to someone else, such as the
/// client's own Super Admin. Only a <i>protected</i> account may deactivate it, reactivate it or change its email
/// address, so the people who use it, other Super Admins included, cannot lock it out or hand it over.
/// </para>
/// The rules are enforced by the command handlers, not by the screen; the screen only reads these answers to stop
/// offering a button that would be refused.
/// </summary>
public interface IProtectedStaffAccounts
{
    /// <summary>Whether this address is a protected account. Case-insensitive; a blank address is never protected.</summary>
    bool IsProtected(string? email);

    /// <summary>
    /// Whether this address is a guarded account: one that only a protected account may deactivate, reactivate or
    /// rename. Case-insensitive; a blank address is never guarded.
    /// </summary>
    bool IsGuarded(string? email);
}
