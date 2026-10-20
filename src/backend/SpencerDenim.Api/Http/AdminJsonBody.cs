using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http.HttpResults;
using SpencerDenim.Application.Abstractions.Results;

namespace SpencerDenim.Api.Http;

/// <summary>
/// Reading one field out of an admin write's <c>JsonObject</c> body, refusing a body that leaves it out.
/// </summary>
/// <remarks>
/// <b>Presence, not just value.</b> "A field this body does not carry is left as it is" and "a field it
/// carries as null is cleared" are different instructions, and collapsing them is how W1-12 cleared every
/// category image and how W1-03's mapper cleared four of five collections. Every admin form in this API
/// sends the whole form, so a missing key is a 400 naming it. That is also why the endpoints bind a
/// <c>JsonObject</c> rather than a typed record: a record cannot tell a missing property from a null one.
/// <para>
/// <b>Extracted from <c>AdminPromotionEndpoints</c> by W3-05b</b>, which needed the same eight readers for
/// the sale-campaign form (ADM-14). Both files now use this one copy; the alternative was a second set of
/// readers free to drift from the first, which is the shape of defect the paragraph above is about.
/// </para>
/// </remarks>
internal static class AdminJsonBody
{
    /// <summary>A string field. Present and null means "clear it".</summary>
    public static ValidationProblem? ReadString(JsonObject body, string field, out string? value)
    {
        ArgumentNullException.ThrowIfNull(body);

        value = null;

        if (!body.TryGetPropertyValue(field, out var node))
        {
            return Missing(field);
        }

        if (node is null)
        {
            return null;
        }

        if (node is not JsonValue json || !json.TryGetValue<string>(out var text))
        {
            return WrongType(field, "text");
        }

        value = text;
        return null;
    }

    /// <summary>
    /// An enum field, serialised by name because <c>Program.cs</c> registers a global
    /// <c>JsonStringEnumConverter</c> — so a name is what comes out of this API and a name is what goes
    /// in.
    /// </summary>
    /// <param name="expected">
    /// How to describe the legal values in the error, e.g. "one of Percent or FixedAmount". The caller
    /// supplies it rather than this method listing <c>Enum.GetNames</c>, because the sentence a staff
    /// member sees should read like a sentence.
    /// </param>
    public static ValidationProblem? ReadEnum<TEnum>(
        JsonObject body,
        string field,
        string expected,
        out TEnum? value)
        where TEnum : struct, Enum
    {
        ArgumentNullException.ThrowIfNull(body);

        value = null;

        if (!body.TryGetPropertyValue(field, out var node))
        {
            return Missing(field);
        }

        if (node is null)
        {
            // Left to the validator, whose message names the control rather than complaining about types.
            return null;
        }

        if (node is not JsonValue json
            || !json.TryGetValue<string>(out var text)
            || !Enum.TryParse(text, ignoreCase: true, out TEnum parsed)
            || !Enum.IsDefined(parsed))
        {
            return WrongType(field, expected);
        }

        value = parsed;
        return null;
    }

    /// <summary>A money or percentage field.</summary>
    public static ValidationProblem? ReadDecimal(JsonObject body, string field, out decimal? value)
    {
        ArgumentNullException.ThrowIfNull(body);

        value = null;

        if (!body.TryGetPropertyValue(field, out var node))
        {
            return Missing(field);
        }

        if (node is null)
        {
            return null;
        }

        if (node is not JsonValue json || !json.TryGetValue<decimal>(out var number))
        {
            return WrongType(field, "a number");
        }

        value = number;
        return null;
    }

    /// <summary>An id or a count.</summary>
    public static ValidationProblem? ReadInt(JsonObject body, string field, out int? value)
    {
        ArgumentNullException.ThrowIfNull(body);

        value = null;

        if (!body.TryGetPropertyValue(field, out var node))
        {
            return Missing(field);
        }

        if (node is null)
        {
            return null;
        }

        if (node is not JsonValue json || !json.TryGetValue<int>(out var number))
        {
            return WrongType(field, "a whole number");
        }

        value = number;
        return null;
    }

    /// <summary>
    /// A media-library <c>publicId</c>, or any other identifier a client is given as a GUID.
    /// </summary>
    /// <remarks>
    /// Text in the canonical <c>D</c> form only (<c>0000f00d-0000-4000-8000-000000000001</c>), which is how
    /// this API writes one. <c>Guid.TryParse</c> would also take braces, parentheses and 32 bare digits;
    /// accepting spellings the API never sends would only widen what a client can get away with.
    /// </remarks>
    public static ValidationProblem? ReadGuid(JsonObject body, string field, out Guid? value)
    {
        ArgumentNullException.ThrowIfNull(body);

        value = null;

        if (!body.TryGetPropertyValue(field, out var node))
        {
            return Missing(field);
        }

        if (node is null)
        {
            return null;
        }

        if (node is not JsonValue json
            || !json.TryGetValue<string>(out var text)
            || !Guid.TryParseExact(text, "D", out var id))
        {
            return WrongType(field, "an identifier like 0000f00d-0000-4000-8000-000000000001");
        }

        value = id;
        return null;
    }

    /// <summary>A flag.</summary>
    public static ValidationProblem? ReadBool(JsonObject body, string field, out bool? value)
    {
        ArgumentNullException.ThrowIfNull(body);

        value = null;

        if (!body.TryGetPropertyValue(field, out var node))
        {
            return Missing(field);
        }

        if (node is null)
        {
            return null;
        }

        if (node is not JsonValue json || !json.TryGetValue<bool>(out var flag))
        {
            return WrongType(field, "true or false");
        }

        value = flag;
        return null;
    }

    /// <summary>An array of objects, for a form that edits a child collection.</summary>
    public static ValidationProblem? ReadObjectArray(
        JsonObject body,
        string field,
        out IReadOnlyList<JsonObject> value)
    {
        ArgumentNullException.ThrowIfNull(body);

        value = [];

        if (!body.TryGetPropertyValue(field, out var node))
        {
            return Missing(field);
        }

        if (node is null)
        {
            // An omitted array is refused above; an explicit null means "none", and the validator decides
            // whether none is allowed. For sale campaigns it is not — a campaign with no target prices
            // nothing — and that message belongs next to the control, not here.
            return null;
        }

        if (node is not JsonArray array)
        {
            return WrongType(field, "a list");
        }

        var items = new List<JsonObject>(array.Count);

        for (var index = 0; index < array.Count; index++)
        {
            if (array[index] is not JsonObject item)
            {
                return WrongType(
                    string.Create(CultureInfo.InvariantCulture, $"{field}[{index}]"),
                    "an object");
            }

            items.Add(item);
        }

        value = items;
        return null;
    }

    /// <summary>
    /// A date and time with <b>no offset</b>, which is what the admin form's <c>datetime-local</c> input
    /// produces and what this API means by a marketing window.
    /// </summary>
    /// <remarks>
    /// <b>An offset or a trailing <c>Z</c> is refused rather than honoured, and that is the Colombo defect
    /// being closed rather than rediscovered.</b> <c>PromoCodeSchedule.ToUtc</c> defines its input to be
    /// Colombo wall-clock; if this accepted <c>2026-10-12T23:59:00Z</c> and passed the same
    /// <see cref="DateTime"/> through, the window would silently move by five and a half hours. Refusing
    /// makes the contract visible to whoever sends the wrong thing instead of making it a wrong figure in
    /// the database.
    /// </remarks>
    public static ValidationProblem? ReadLocalDateTime(JsonObject body, string field, out DateTime? value)
    {
        ArgumentNullException.ThrowIfNull(body);

        value = null;

        if (!body.TryGetPropertyValue(field, out var node))
        {
            return Missing(field);
        }

        if (node is null)
        {
            return null;
        }

        if (node is not JsonValue json || !json.TryGetValue<string>(out var text)
            || string.IsNullOrWhiteSpace(text))
        {
            return WrongType(field, "a date and time like 2026-10-12T23:59");
        }

        var trimmed = text.Trim();

        if (trimmed.EndsWith('Z') || trimmed.EndsWith('z')
            || HasOffset(trimmed)
            || !DateTime.TryParse(trimmed, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
        {
            return WrongType(
                field,
                "a date and time in the shop's own time with no offset, like 2026-10-12T23:59");
        }

        value = parsed;
        return null;
    }

    /// <summary>The <c>rowVersion</c> a form may send in the body instead of <c>If-Match</c>.</summary>
    public static string? RowVersionIn(JsonObject body)
    {
        ArgumentNullException.ThrowIfNull(body);

        return body["rowVersion"] is JsonValue value && value.TryGetValue<string>(out var text)
            ? text
            : null;
    }

    public static ValidationProblem Missing(string field) =>
        Problem([new ValidationError(field, $"Send {field}.")]);

    public static ValidationProblem WrongType(string field, string expected) =>
        Problem([new ValidationError(field, $"Send {field} as {expected}.")]);

    /// <summary>One problem document per field, so a screen can attach each message to its control.</summary>
    public static ValidationProblem Problem(IReadOnlyList<ValidationError> errors)
    {
        ArgumentNullException.ThrowIfNull(errors);

        return TypedResults.ValidationProblem(
            errors
                .GroupBy(error => error.Field, StringComparer.Ordinal)
                .ToDictionary(
                    group => group.Key,
                    group => group.Select(error => error.Message).ToArray(),
                    StringComparer.Ordinal),
            title: "One or more fields need correcting.");
    }

    /// <summary>
    /// Whether an ISO-8601 date-time carries a <c>+hh:mm</c> or <c>-hh:mm</c> offset.
    /// </summary>
    /// <remarks>
    /// Looked for after the <c>T</c> only, because the date half of every such string contains hyphens. A
    /// value with no <c>T</c> cannot carry an offset in the first place.
    /// </remarks>
    private static bool HasOffset(string text)
    {
        var timeStart = text.IndexOf('T', StringComparison.Ordinal);

        if (timeStart < 0)
        {
            return false;
        }

        var time = text.AsSpan(timeStart);

        return time.Contains('+') || time.Contains('-');
    }
}
