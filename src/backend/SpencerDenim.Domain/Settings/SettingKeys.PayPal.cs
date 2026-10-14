namespace SpencerDenim.Domain.Settings;

// The PayPal keys (W4-01, PAY-02), in a file of their own so the payment providers that follow add theirs
// beside these and not into one list that every provider's branch edits.
public static partial class SettingKeys
{
    /// <summary>
    /// Group <c>Payments.PayPal</c> (doc 08 §3.8), plus the one key doc 08 files under <c>Orders</c> that is
    /// PayPal's alone. <c>Settings.Payments</c> on every <c>Payments.PayPal.*</c> row (DB2).
    /// <para>
    /// <b>Everything PayPal needs is here, and nothing is read from the environment or from a committed
    /// file</b> (issue #54: "client id, secret, environment and the webhook id all come from
    /// configuration, per doc 03 §7 … sandbox and live must be switchable without a rebuild"). The secret is
    /// stored as Data Protection ciphertext (<see cref="SettingValueType.Secret"/>) and never leaves the
    /// server: the admin read model returns only that a value is set.
    /// </para>
    /// <para>
    /// <b>The credentials and the rate are null until the client fills them in, and PayPal is simply not
    /// offered until they are.</b> That is the same shape the <c>Bank</c> group has — a figure only the client
    /// knows is never given a plausible default — so the numbers that <i>are</i> seeded are exactly the ones
    /// doc 08 §3.8 states: the buffer (3.00), the rate's maximum age (7 days), the capture lock (3 h), the
    /// recovery window (24 h), the pending-capture hold (5 days) and the payment window (60 minutes).
    /// </para>
    /// </summary>
    public static class PayPal
    {
        public const string Group = "Payments.PayPal";

        /// <summary>
        /// <c>sandbox</c> or <c>live</c>. It chooses the PayPal host and so which account the credentials
        /// belong to; switching it needs no rebuild and no restart, because every call reads it.
        /// </summary>
        public const string Mode = "Payments.PayPal.Mode";

        /// <summary>The REST app's client id. Not a secret on its own (PayPal's own sample pages print it).</summary>
        public const string ClientId = "Payments.PayPal.ClientId";

        /// <summary>The REST app's client secret. The only secret in this build's catalogue.</summary>
        public const string ClientSecret = "Payments.PayPal.ClientSecret";

        /// <summary>The webhook's id, from the PayPal developer dashboard. Needed to verify a signature.</summary>
        public const string WebhookId = "Payments.PayPal.WebhookId";

        /// <summary>LKR per 1 USD, set by a person (doc 08 D1). The rate is frozen on each payment at initiation.</summary>
        public const string LkrPerUsd = "Payments.PayPal.LkrPerUsd";

        /// <summary>The percentage added to the converted amount to cover exchange differences (doc 08 D1).</summary>
        public const string FxBufferPercent = "Payments.PayPal.FxBufferPercent";

        /// <summary>How old <see cref="LkrPerUsd"/> may be before PayPal is hidden at checkout (doc 11 P8).</summary>
        public const string RateMaxAgeDays = "Payments.PayPal.RateMaxAgeDays";

        /// <summary>
        /// When the rate was last set or confirmed, stamped by the server and never typed (an ISO-8601
        /// instant in UTC). The screen shows it; no request can write it.
        /// </summary>
        public const string RateUpdatedAtUtc = "Payments.PayPal.RateUpdatedAtUtc";

        /// <summary>How long an order is locked against expiry while a capture is in flight (doc 08 §4.2 step 1).</summary>
        public const string CaptureLockHours = "Payments.PayPal.CaptureLockHours";

        /// <summary>How long the recovery job keeps asking PayPal about a stuck capture (doc 08 §4.2 step 5).</summary>
        public const string CaptureRecoveryMaxHours = "Payments.PayPal.CaptureRecoveryMaxHours";

        /// <summary>
        /// How long, from placement, an order is held while PayPal reports a capture as PENDING
        /// (doc 08 §4.2 step 4, N16 — the client's choice).
        /// </summary>
        public const string PendingCaptureMaxDays = "Payments.PayPal.PendingCaptureMaxDays";

        /// <summary>
        /// How long a customer has to pay by PayPal before the order may expire. Doc 08 §3.8 files this
        /// under the <c>Orders</c> group (<c>Orders.PayPalExpiryMinutes</c>, <c>Settings.Edit</c>); it is
        /// here because it is PayPal's own window and is edited on PayPal's own panel. <b>It keeps doc 08's
        /// name, group and permission</b>: the <c>Orders</c> group shares a save unit with <c>Store</c>
        /// (<see cref="SettingCatalog.SaveUnits"/>), and a unit may not span two permissions, so giving this
        /// one row <c>Settings.Payments</c> would publish a concurrency token over it to callers who may not
        /// write it. A Super Admin holds both permissions, so the panel saves it like any other field.
        /// </summary>
        public const string ExpiryMinutes = "Orders.PayPalExpiryMinutes";

        public const string ModeSandbox = "sandbox";

        public const string ModeLive = "live";

        public const decimal FxBufferPercentDefault = 3.00m;

        /// <summary>Above this the buffer is a typo, not a hedge.</summary>
        public const decimal FxBufferPercentMax = 20m;

        public const int RateMaxAgeDaysDefault = 7;

        public const int RateMaxAgeDaysMin = 1;

        public const int RateMaxAgeDaysMax = 30;

        public const int CaptureLockHoursDefault = 3;

        public const int CaptureLockHoursMin = 1;

        public const int CaptureLockHoursMax = 24;

        public const int CaptureRecoveryMaxHoursDefault = 24;

        public const int CaptureRecoveryMaxHoursMin = 1;

        public const int CaptureRecoveryMaxHoursMax = 168;

        public const int PendingCaptureMaxDaysDefault = 5;

        public const int PendingCaptureMaxDaysMin = 1;

        public const int PendingCaptureMaxDaysMax = 14;

        public const int ExpiryMinutesDefault = 60;

        /// <summary>
        /// Ten minutes. Shorter than that and an order can expire while its buyer is still on PayPal's page.
        /// </summary>
        public const int ExpiryMinutesMin = 10;

        /// <summary>
        /// Three hours: a PayPal order that nobody approves lapses on PayPal's side after about that long, so a
        /// longer window would leave a payment open on our side that PayPal can no longer capture.
        /// </summary>
        public const int ExpiryMinutesMax = 180;

        /// <summary>
        /// The most LKR per USD worth accepting. Sri Lanka's rate is in the hundreds; a figure past this is a
        /// misplaced decimal point, and it would charge a customer a few cents for a whole order.
        /// </summary>
        public const decimal LkrPerUsdMax = 10_000m;

        /// <summary><c>decimal(18,6)</c> on <c>Payments.ExchangeRate</c>: more places than this would be rounded away.</summary>
        public const int LkrPerUsdScale = 6;
    }
}
