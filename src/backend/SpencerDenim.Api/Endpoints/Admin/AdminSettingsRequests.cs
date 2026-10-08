namespace SpencerDenim.Api.Endpoints.Admin;

/*
 * These two records are the declared contract for the bodies these routes accept: they are what the OpenAPI
 * document shows a client (`.Accepts<T>` on each route) and what `AdminSettingsShapeTests` pins against the
 * handlers' own key lists.
 *
 * **They are deliberately not what the endpoint binds.** Since round 1 the endpoint reads a `JsonObject`,
 * because telling an *absent* field from a `null` one is the difference between "leave this key alone" and
 * "clear it" — and collapsing the two is W1-12's Blocker shape. Binding a record cannot express that: a missing
 * property and an explicit null both arrive as null. Reading the JSON by hand also turns a field of the wrong
 * type into a named field error instead of "malformed JSON".
 */

/// <summary>
/// Set or remove the shop's logo (GLB-02, ADM-22).
/// </summary>
/// <param name="LogoAssetPublicId">
/// The media asset's public token, or <see langword="null"/> to remove the logo.
/// <para>
/// <c>Guid?</c> rather than <c>Guid</c>, because <see langword="null"/> is a <i>meaningful</i> value here —
/// it is the Remove action the owner asked for — and because a non-nullable <c>Guid</c> would bind a missing
/// field to <c>Guid.Empty</c>, which is indistinguishable from a client that sent the wrong thing. A value
/// that is not a GUID is a <c>JsonException</c> and comes back as one 400 naming the body, which is
/// <c>AdminWriteRequest.ReadJsonAsync</c>'s job rather than a 500 (issue #47).
/// </para>
/// </param>
/// <param name="RowVersion">
/// The <c>Store</c> group's token from the read model. May travel in <c>If-Match</c> instead; see
/// <c>AdminWriteRequest.RowVersion</c>.
/// </param>
public sealed record SetStoreLogoRequest(Guid? LogoAssetPublicId, string? RowVersion);

/// <summary>
/// The contact number and the social profile addresses (ADM-22, GLB-07, GLB-08).
/// <para>
/// <b>Every field is nullable and every field is sent on every save.</b> PUT replaces, so a field the body
/// omits is a field the API is told to clear — which is exactly how W1-12's blocker worked, where a request
/// model that had never listed three image columns wiped them on every save while 1,571 tests round-tripped
/// null to null. <c>AdminSettingsShapeTests</c> pins this record against the handler's own list of keys
/// so a sixth field cannot be added to one and forgotten on the other.
/// </para>
/// <para>
/// <b>Issue #47 is why these are <c>string?</c> and not <c>string</c>.</b> A non-nullable reference type
/// binds a missing property to <see langword="null"/> regardless, and the resulting
/// <c>NullReferenceException</c> deeper in is the 500 on the admin login that #47 records. Nullable is the
/// truth about what JSON can send, and the use case decides what a missing field means.
/// </para>
/// </summary>
/// <param name="WhatsApp">Digits only, international form. Blank or absent clears it.</param>
/// <param name="Phone">
/// The telephone number as shoppers should read it, e.g. <c>+94 11 234 5678</c> (GLB-10, W2-11). Shown on
/// the Contact page and in the footer; the browser strips the punctuation for the <c>tel:</c> link.
/// </param>
/// <param name="Email">The address shoppers write to. Nothing is ever sent from it by the shop.</param>
/// <param name="Address">The postal address, one line per line. Plain text, never HTML.</param>
/// <param name="OpeningHours">When the shop is open, one line per line. Plain text, never HTML.</param>
/// <param name="MapEmbedUrl">
/// The Contact page's map. <b>Only an https address on a Google Maps or OpenStreetMap embed host is
/// accepted</b>, because the storefront puts it in an <c>&lt;iframe src&gt;</c> inside the shop's own page
/// — see <c>SettingFieldRules.MapEmbedHosts</c>.
/// </param>
/// <param name="FacebookUrl">A full http(s) address. Blank or absent hides the icon.</param>
/// <param name="InstagramUrl"><inheritdoc cref="FacebookUrl" path="/node()"/></param>
/// <param name="TikTokUrl"><inheritdoc cref="FacebookUrl" path="/node()"/></param>
/// <param name="RowVersion">The group's token from the read model.</param>
public sealed record UpdateContactAndSocialRequest(
    string? WhatsApp,
    string? Phone,
    string? Email,
    string? Address,
    string? OpeningHours,
    string? MapEmbedUrl,
    string? FacebookUrl,
    string? InstagramUrl,
    string? TikTokUrl,
    string? RowVersion);

/// <summary>
/// The bank account customers transfer to (PAY-01, doc 11 P7's preconditions; <c>Settings.Payments</c>).
/// <para>
/// <b>This record is the OpenAPI shape only; the endpoint reads the body as a <c>JsonObject</c>.</b> That
/// is the ADM-22 pattern and it is load-bearing here: absent and <see langword="null"/> have to mean
/// different things. A screen whose caller cannot read the <c>Bank</c> group never receives these fields
/// and therefore never sends them, and a record binding would turn that silence into "clear the shop's
/// account number" — W1-12's blocker, on the one group where it costs real money.
/// </para>
/// </summary>
/// <param name="BankName">
/// Required before a shopper is shown anything. Absent leaves it alone; null clears it.
/// </param>
/// <param name="Branch">Optional: a deposit slip asks for it, online banking does not.</param>
/// <param name="AccountName">Required before a shopper is shown anything.</param>
/// <param name="AccountNumber">
/// Required before a shopper is shown anything. Digits, spaces and dashes — the grouping the client types
/// is kept, because a shopper copies it into online banking.
/// </param>
/// <param name="Instructions">Optional plain text under the details. Line breaks are kept; markup is not.</param>
/// <param name="RowVersion">The group's token from the read model.</param>
public sealed record UpdateBankDetailsRequest(
    string? BankName,
    string? Branch,
    string? AccountName,
    string? AccountNumber,
    string? Instructions,
    string? RowVersion);

/// <summary>
/// The company's details as they appear on an invoice, and the auto-deliver window (ADM-09 / ADM-22,
/// doc 08 §3.8 groups <c>Store</c> and <c>Orders</c>).
/// </summary>
/// <remarks>
/// <b>For the OpenAPI document only.</b> The body is read as a <c>JsonObject</c> so that an absent field
/// can be told from a null one, which this group needs as much as the bank group does: a save that did not
/// mention a field has silently cleared it four times in this project.
/// </remarks>
/// <param name="LegalName">
/// The registered company name. <b>Not the shop's name</b> — a shopper reads <c>StoreBrand.Name</c>
/// (docs/02 §I2); an invoice names the company that issues it.
/// </param>
/// <param name="RegisteredAddress">
/// The registered address, one line per line. <b>Named <c>registeredAddress</c> rather than <c>address</c>,
/// because <c>Contact.Address</c> already owns that name on the same screen</b> — two boxes called
/// "address" on one page collided on their DOM id and their error field, which the settings spec caught.
/// </param>
/// <param name="BrNumber">The business registration number.</param>
/// <param name="VatNumber">The VAT registration number. No VAT line is calculated from it (doc 08 D2).</param>
/// <param name="AutoDeliverAfterDays">
/// Whole days, as digits, between 1 and 90. Empty stops the system closing orders by itself.
/// </param>
/// <param name="RowVersion">The save unit's concurrency token, as the screen read it.</param>
public sealed record UpdateInvoiceAndFulfilmentSettingsRequest(
    string? LegalName,
    string? RegisteredAddress,
    string? BrNumber,
    string? VatNumber,
    string? AutoDeliverAfterDays,
    string? RowVersion);
