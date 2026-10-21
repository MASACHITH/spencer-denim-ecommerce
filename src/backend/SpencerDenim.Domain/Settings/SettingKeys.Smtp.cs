namespace SpencerDenim.Domain.Settings;

public static partial class SettingKeys
{
    /// <summary>
    /// Group <c>Email.Smtp</c> (doc 08 §3.8): the mail account the shop sends every email through (ADM-22,
    /// owner's instruction of 2026-10-21: "admin must be able to add and change the email account at any time").
    /// <para>
    /// <b><c>Settings.Security</c> on every key</b>, which only the GrantsAll role holds (doc 08 §3.1, N2;
    /// <c>CK_RoleClaims_NoSecurity</c>), so only a Super Admin reads or writes them. Every change is audited per
    /// key, raises the <c>SensitiveSettingsChanged</c> banner, and emails every Super Admin <b>twice</b> — through
    /// the new account and through the previous one (N2's dual send), so a hijacked change cannot silence its own
    /// warning.
    /// </para>
    /// <para>
    /// <b>The rows win over <c>Email__Smtp__*</c> only when they make a complete, valid account</b>
    /// (<c>SmtpSettingsResolver</c>); otherwise configuration is used, exactly as before this group existed. So a
    /// deployment that configured mail through the environment keeps working untouched until a Super Admin fills
    /// this form in, and clearing the server name hands it back to the environment.
    /// </para>
    /// <para>
    /// <b>No reply-to key</b>: doc 08 §3.8 lists seven keys and none of them is a reply-to, so none is invented.
    /// <b>Encryption is <see cref="UseStartTls"/> (on/off), not a three-way none/STARTTLS/SSL choice</b>: the
    /// transport is <c>System.Net.Mail.SmtpClient</c>, which can upgrade a connection with STARTTLS but cannot
    /// speak implicit TLS (port 465) at all, so offering "SSL" would be a choice the shop cannot honour.
    /// </para>
    /// </summary>
    public static class Smtp
    {
        public const string Group = "Email.Smtp";

        /// <summary>The mail server's host name or IP address. Empty means "use the server's configuration".</summary>
        public const string Host = "Email.Smtp.Host";

        /// <summary>The TCP port, 1–65535; not seeded (doc 08 states no default), so the form asks for it.</summary>
        public const string Port = "Email.Smtp.Port";

        /// <summary>The account's user name, or empty for a relay that does not authenticate.</summary>
        public const string Username = "Email.Smtp.Username";

        /// <summary>
        /// The account's password: a <see cref="SettingValueType.Secret"/>, Data Protection ciphertext at rest,
        /// never returned by any read, logged in the audit trail as <c>[secret changed]</c>.
        /// </summary>
        public const string Password = "Email.Smtp.Password";

        /// <summary>The address every email is sent from.</summary>
        public const string FromAddress = "Email.Smtp.FromAddress";

        /// <summary>The display name beside <see cref="FromAddress"/>, or empty to send the address alone.</summary>
        public const string FromName = "Email.Smtp.FromName";

        /// <summary>Whether the connection is upgraded with STARTTLS. Not seeded; an unsaved value counts as on.</summary>
        public const string UseStartTls = "Email.Smtp.UseStartTls";

        /// <summary>
        /// What an unsaved (null) switch means: on. Mail and its password never cross the network in the clear
        /// because a value was left out — <c>SmtpOptions.UseStartTls</c>' own default.
        /// </summary>
        public const bool UseStartTlsDefault = true;

        /// <summary>RFC 1035's limit for a host name.</summary>
        public const int HostMaxLength = 253;

        /// <summary>Enough for any provider's user name, usually an email address.</summary>
        public const int UsernameMaxLength = 256;

        /// <summary>Enough for a generated app password; refuses a pasted paragraph.</summary>
        public const int PasswordMaxLength = 256;

        /// <summary><c>ops.EmailOutbox.ToEmail</c>'s width, so any address we could send to we could send from.</summary>
        public const int FromAddressMaxLength = 256;

        /// <summary><c>ops.EmailOutbox.ToName</c>'s width.</summary>
        public const int FromNameMaxLength = 120;
    }
}
