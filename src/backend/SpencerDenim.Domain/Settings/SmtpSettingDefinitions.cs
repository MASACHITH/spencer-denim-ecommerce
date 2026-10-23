using SpencerDenim.Domain.Identity;

namespace SpencerDenim.Domain.Settings;

/// <summary>
/// The <c>Email.Smtp</c> rows of <see cref="SettingCatalog"/> (doc 08 §3.8; ADM-22, 2026-10-21), in their own file
/// as <see cref="PayPalSettingDefinitions"/> are.
/// <para>
/// <b>Every key needs <c>Settings.Security</c></b> (GrantsAll only). <b>Nothing is seeded</b>: doc 08 §3.8 states no
/// default for this group, and <c>SettingKeySpecificationTests</c> allows a seed value only where it does. The server,
/// the account, the password and the sender are the client's anyway; an unsaved switch counts as on
/// (<see cref="SettingKeys.Smtp.UseStartTlsDefault"/>). Empty rows keep the shop on its <c>Email__Smtp__*</c>
/// configuration until a Super Admin fills them in.
/// </para>
/// </summary>
internal static class SmtpSettingDefinitions
{
    public static IReadOnlyList<SettingDefinition> All { get; } =
    [
        new(
            SettingKeys.Smtp.Host,
            SettingKeys.Smtp.Group,
            SettingValueType.String,
            Permissions.Settings.Security,
            "Mail server",
            "The mail server's name from your email provider, without https:// or a port. Leave it empty and the "
            + "shop keeps using the mail account set up on the server."),

        new(
            SettingKeys.Smtp.Port,
            SettingKeys.Smtp.Group,
            SettingValueType.Int,
            Permissions.Settings.Security,
            "Port",
            "The mail server's port from your email provider, usually 587. Between 1 and 65535."),

        new(
            SettingKeys.Smtp.Username,
            SettingKeys.Smtp.Group,
            SettingValueType.String,
            Permissions.Settings.Security,
            "User name",
            "The account's user name, usually the full email address. Leave it empty for a server that does not "
            + "ask for one."),

        new(
            SettingKeys.Smtp.Password,
            SettingKeys.Smtp.Group,
            SettingValueType.Secret,
            Permissions.Settings.Security,
            "Password",
            "The account's password or app password. It is stored encrypted and is never shown again: leave the "
            + "box empty to keep the saved one, or type a new one to replace it."),

        new(
            SettingKeys.Smtp.FromAddress,
            SettingKeys.Smtp.Group,
            SettingValueType.String,
            Permissions.Settings.Security,
            "Send from address",
            "The address customers see every email come from. Your provider must allow this account to send "
            + "from it."),

        new(
            SettingKeys.Smtp.FromName,
            SettingKeys.Smtp.Group,
            SettingValueType.String,
            Permissions.Settings.Security,
            "Send from name",
            "The name shown beside the address in a customer's inbox. Leave it empty to show the address alone."),

        new(
            SettingKeys.Smtp.UseStartTls,
            SettingKeys.Smtp.Group,
            SettingValueType.Bool,
            Permissions.Settings.Security,
            "Encrypt the connection (STARTTLS)",
            "Keep this on unless your provider says otherwise. With it off nothing is encrypted, so a user name "
            + "and password are refused. Until it is saved it counts as on."),
    ];
}
