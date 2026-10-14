using System.Globalization;
using SpencerDenim.Domain.Identity;

namespace SpencerDenim.Domain.Settings;

/// <summary>
/// The PayPal rows of <see cref="SettingCatalog"/> (W4-01, PAY-02; doc 08 §3.8 <c>Payments.PayPal</c> and
/// <c>Orders.PayPalExpiryMinutes</c>), kept in their own file so a second payment provider adds its rows
/// beside these instead of editing one list.
/// <para>
/// <b>Every <c>Payments.PayPal.*</c> key needs <c>Settings.Payments</c></b>, which no default role holds (a
/// Super Admin's <c>GrantsAll</c> does); <c>Orders.PayPalExpiryMinutes</c> keeps the <c>Settings.Edit</c> doc 08
/// gives the <c>Orders</c> group, for a reason <see cref="SettingCatalog.SaveUnits"/> enforces. The numbers that are seeded are the ones doc 08 §3.8 states; what only the client
/// knows — the credentials, the webhook id, the rate — is left null, and PayPal is not offered at checkout
/// until it is filled in.
/// </para>
/// </summary>
internal static class PayPalSettingDefinitions
{
    public static IReadOnlyList<SettingDefinition> All { get; } =
    [
        new(
            SettingKeys.PayPal.Mode,
            SettingKeys.PayPal.Group,
            SettingValueType.String,
            Permissions.Settings.Payments,
            "PayPal environment",
            "sandbox (PayPal's test system, no real money) or live. Switching takes effect at once and needs no "
            + "restart. Use the credentials that belong to the environment you choose.",
            SeedValue: SettingKeys.PayPal.ModeSandbox),

        new(
            SettingKeys.PayPal.ClientId,
            SettingKeys.PayPal.Group,
            SettingValueType.String,
            Permissions.Settings.Payments,
            "PayPal client ID",
            "The Client ID of the shop's PayPal REST app (PayPal developer dashboard, Apps and Credentials). "
            + "PayPal is not offered at checkout until the client ID, the secret and the webhook ID are all set."),

        new(
            SettingKeys.PayPal.ClientSecret,
            SettingKeys.PayPal.Group,
            SettingValueType.Secret,
            Permissions.Settings.Payments,
            "PayPal client secret",
            "The Secret of the same REST app. It is stored encrypted and is never shown again: leave the box "
            + "empty to keep the secret that is saved, or type a new one to replace it."),

        new(
            SettingKeys.PayPal.WebhookId,
            SettingKeys.PayPal.Group,
            SettingValueType.String,
            Permissions.Settings.Payments,
            "PayPal webhook ID",
            "The ID of the webhook registered for this shop's /api/payments/paypal/webhook address, from the "
            + "PayPal dashboard. Without it no notification from PayPal can be trusted, so PayPal is not offered."),

        new(
            SettingKeys.PayPal.LkrPerUsd,
            SettingKeys.PayPal.Group,
            SettingValueType.Decimal,
            Permissions.Settings.Payments,
            "Rupees per US dollar",
            "How many rupees one US dollar costs today. PayPal cannot charge in rupees, so each order is "
            + "converted at this rate plus the buffer below. Update it at least as often as the age limit "
            + "below, or PayPal is hidden at checkout."),

        new(
            SettingKeys.PayPal.FxBufferPercent,
            SettingKeys.PayPal.Group,
            SettingValueType.Decimal,
            Permissions.Settings.Payments,
            "Currency buffer (%)",
            "Added to the converted amount to cover exchange-rate movement between the rate above and PayPal's "
            + "own conversion. Customers see the final dollar amount before they pay. Between 0 and 20.",
            SeedValue: SettingKeys.PayPal.FxBufferPercentDefault.ToString("0.00", CultureInfo.InvariantCulture)),

        new(
            SettingKeys.PayPal.RateMaxAgeDays,
            SettingKeys.PayPal.Group,
            SettingValueType.Int,
            Permissions.Settings.Payments,
            "Rate valid for (days)",
            "PayPal is hidden at checkout when the rate was last set or confirmed longer ago than this. "
            + "Between 1 and 30 days.",
            SeedValue: SettingKeys.PayPal.RateMaxAgeDaysDefault.ToString(CultureInfo.InvariantCulture)),

        new(
            SettingKeys.PayPal.RateUpdatedAtUtc,
            SettingKeys.PayPal.Group,
            SettingValueType.String,
            Permissions.Settings.Payments,
            "Rate last updated",
            "When the rate was last set or confirmed. Filled in by the system; it cannot be typed."),

        new(
            SettingKeys.PayPal.CaptureLockHours,
            SettingKeys.PayPal.Group,
            SettingValueType.Int,
            Permissions.Settings.Payments,
            "Capture lock (hours)",
            "While a payment is being collected from PayPal the order cannot expire for this long. PayPal keeps "
            + "an approved order for about three hours, so leave this at 3 unless PayPal changes it. "
            + "Between 1 and 24.",
            SeedValue: SettingKeys.PayPal.CaptureLockHoursDefault.ToString(CultureInfo.InvariantCulture)),

        new(
            SettingKeys.PayPal.CaptureRecoveryMaxHours,
            SettingKeys.PayPal.Group,
            SettingValueType.Int,
            Permissions.Settings.Payments,
            "Capture recovery window (hours)",
            "How long the system keeps asking PayPal about a payment that did not finish, before it gives up "
            + "and tells staff to check PayPal by hand. Between 1 and 168 hours.",
            SeedValue: SettingKeys.PayPal.CaptureRecoveryMaxHoursDefault.ToString(CultureInfo.InvariantCulture)),

        new(
            SettingKeys.PayPal.PendingCaptureMaxDays,
            SettingKeys.PayPal.Group,
            SettingValueType.Int,
            Permissions.Settings.Payments,
            "Hold for pending payments (days)",
            "When PayPal reports a payment as pending (for example an eCheck), the order's items are held "
            + "for up to this many days from the day it was placed. Between 1 and 14.",
            SeedValue: SettingKeys.PayPal.PendingCaptureMaxDaysDefault.ToString(CultureInfo.InvariantCulture)),

        // Group Orders, not Payments.PayPal: doc 08 §3.8 gives the key that name and the catalogue's own rule
        // is that a key begins with its group. **And Settings.Edit, not Settings.Payments**, which is what
        // doc 08 gives the Orders group and what the save-unit rule forces: Orders shares a save unit with
        // Store (one concurrency token over both), and a unit that spanned two permissions would publish a
        // token over a row a Settings.Edit holder may not write. See SettingKeys.PayPal.ExpiryMinutes.
        new(
            SettingKeys.PayPal.ExpiryMinutes,
            SettingKeys.Orders.Group,
            SettingValueType.Int,
            Permissions.Settings.Edit,
            "Time to pay by PayPal (minutes)",
            "How long a customer has to finish paying by PayPal before the order expires and its items go back "
            + "on sale. Between 10 and 180 minutes.",
            SeedValue: SettingKeys.PayPal.ExpiryMinutesDefault.ToString(CultureInfo.InvariantCulture)),
    ];
}
