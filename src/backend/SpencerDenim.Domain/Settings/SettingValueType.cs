namespace SpencerDenim.Domain.Settings;

/// <summary>
/// <c>ops.Settings.ValueType</c> (doc 08 §3.8), and its <c>CK_Settings_ValueType</c>.
/// <para>
/// <b>Every member is declared, including the ones nothing writes yet.</b> The numbers are the
/// document's, so the CHECK constraint, the seeded rows and a future group all agree without anybody
/// re-deriving them: <see cref="Decimal"/> arrives with PayPal's FX buffer and <see cref="Secret"/>
/// with its client secret (both #53 / W3), and a type added later would have to come with a migration
/// that widens the constraint. Declaring the whole set now costs one enum and removes that migration.
/// </para>
/// </summary>
// CA1720 ("identifier contains type name") fires on String, Int and Decimal. Suppressed for this one
// declaration rather than relaxed in .editorconfig, because here the rule is wrong for a specific reason:
// these are not names this codebase chose. They are doc 08 §3.8's own words for the eight values of
// ops.Settings.ValueType, and the numbers beside them are written into CK_Settings_ValueType and into every
// seeded row. The names also travel on the wire — JsonStringEnumConverter is registered globally — so
// "TextValue" or "Integer" would put a vocabulary in the API that the binding design document does not use,
// and the next person comparing the table to the document would have to translate. The rule exists to stop
// parameters like `string String`; an enum member meaning "this setting holds a string" is the case it was
// not written for.
#pragma warning disable CA1720
public enum SettingValueType : byte
{
    /// <summary>A single line of text, e.g. a bank account name.</summary>
    String = 1,

    /// <summary>
    /// Text or HTML. Doc 08 §3.8: <b>sanitised on save with the allow-list HTML sanitiser used for
    /// content pages</b> (N10). Nothing in this slice writes one, and the sanitiser belongs to the task
    /// that does — a value of this type cannot be set through any endpoint that exists today.
    /// </summary>
    TextHtml = 2,

    Int = 3,

    Decimal = 4,

    Bool = 5,

    Json = 6,

    /// <summary>
    /// Data Protection ciphertext, purpose <c>SpencerDenim.Settings.v1</c>. <c>IsSecret</c> is 1 if and
    /// only if the type is this one (<c>CK_Settings_IsSecret</c>).
    /// </summary>
    Secret = 7,

    /// <summary>
    /// A <c>media.MediaAssets.Id</c>, as invariant digits, tracked in <c>media.MediaUsages</c> with
    /// <c>OwnerType = Setting</c> and the setting's key as <c>OwnerId</c> (doc 08 §3.3, §3.8). The usage
    /// row is what makes the media library refuse to delete an asset a setting still points at.
    /// </summary>
    MediaAssetRef = 8,
}
#pragma warning restore CA1720
