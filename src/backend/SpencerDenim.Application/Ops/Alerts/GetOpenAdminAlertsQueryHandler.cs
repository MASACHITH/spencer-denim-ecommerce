using System.Globalization;
using System.Text.Json;
using SpencerDenim.Application.Abstractions.Messaging;
using SpencerDenim.Application.Admin.CurrentUser;
using SpencerDenim.Application.Ops.Alerts.Abstractions;
using SpencerDenim.Application.Ops.Alerts.Contracts;
using SpencerDenim.Domain.Ops;

namespace SpencerDenim.Application.Ops.Alerts;

/// <summary>
/// Reads the banner (doc 08 §3.8 N2, R10).
/// <para>
/// <b>Two steps, and the order is the security.</b> The user's <i>effective</i> permissions are read from
/// their roles — never from claims on the cookie (doc 08 §3.1 R4), so a role changed five minutes ago
/// decides what they see now — and <c>AdminAlertVisibility</c> turns them into the list of types R10
/// allows. The reader is then told exactly which types to return. A user R10 gives no path to gets an
/// empty list from the query, not an empty list by accident of there being no rows.
/// </para>
/// </summary>
internal sealed class GetOpenAdminAlertsQueryHandler(
    IEffectivePermissionsProvider permissionsProvider,
    IAdminAlertReader reader)
    : IQueryHandler<GetOpenAdminAlertsQuery, AdminAlertBannerDto>
{
    /// <summary>
    /// <c>DetailsJson</c> key naming the order a staff member should open. Written by
    /// <c>DeadEmailAlert</c>; any later alert type that names an order the same way gets the banner's link
    /// for free.
    /// </summary>
    private const string OrderPublicIdKey = "orderPublicId";

    /// <summary><c>DetailsJson</c> key naming that order's number, so the link can say where it goes.</summary>
    private const string OrderNumberKey = "orderNumber";

    /// <summary>
    /// <c>DetailsJson</c> key naming the outbox template that died. Written by <c>DeadEmailAlert</c>; the
    /// screen's advice branches on it, because doc 11 §4 gives a dead <c>StaffInvite</c> a different
    /// remedy from a dead <c>PasswordReset</c>.
    /// </summary>
    private const string TemplateCodeKey = "templateCode";

    public async Task<AdminAlertBannerDto> HandleAsync(
        GetOpenAdminAlertsQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var permissions = await permissionsProvider.GetEffectivePermissionsAsync(query.UserId, cancellationToken);

        var visibleTypes = AdminAlertVisibility.VisibleTypes(
            permissions.GrantsAllPermissions,
            permissions.Permissions);

        if (visibleTypes.Count == 0)
        {
            // Most staff. Answered without touching the database, because there is no set of rows that
            // could be the right answer for somebody R10 gives no path to.
            return new AdminAlertBannerDto(false, []);
        }

        var (moreThanShown, rows) = await reader.GetOpenAsync(
            visibleTypes,
            GetOpenAdminAlertsQuery.MaxAlerts,
            cancellationToken);

        return new AdminAlertBannerDto(moreThanShown, [.. rows.Select(ToDto)]);
    }

    private static AdminAlertDto ToDto(AdminAlertRow row)
    {
        var details = ReadDetails(row.DetailsJson);

        return new AdminAlertDto(
            row.Id,
            row.AlertType,
            row.Severity,
            row.Title,
            row.CreatedAtUtc,
            row.RelatedEntityType,
            row.RelatedEntityId,
            details.OrderPublicId,
            details.OrderNumber,
            details.TemplateCode);
    }

    /// <summary>The three things the banner lifts out of an alert's details.</summary>
    private sealed record AlertDetails(string? OrderPublicId, string? OrderNumber, string? TemplateCode);

    /// <summary>
    /// Lifts the order link and the template code out of an alert's details, and answers nulls for
    /// anything it cannot read.
    /// </summary>
    /// <remarks>
    /// <b>Nothing here throws on a details payload it does not understand.</b> The column is
    /// <c>nvarchar(max)</c> with an <c>ISJSON</c> check and nothing else (doc 08 §3.8), six of the seven
    /// alert types are written by tasks that do not exist yet, and a banner that 500s because one row's
    /// details have a shape this method did not expect would hide every other alert in the shop — which
    /// is the opposite of what the table is for.
    /// <para>
    /// <b>The public id is validated as a GUID before it is returned.</b> It becomes a link target in the
    /// admin app (<c>/orders/{id}</c>), and a value from a column no screen wrote is not something to
    /// interpolate into a route unchecked.
    /// </para>
    /// </remarks>
    private static AlertDetails ReadDetails(string? detailsJson)
    {
        if (string.IsNullOrWhiteSpace(detailsJson))
        {
            return new AlertDetails(null, null, null);
        }

        try
        {
            using var document = JsonDocument.Parse(detailsJson);

            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return new AlertDetails(null, null, null);
            }

            var templateCode = ReadString(document.RootElement, TemplateCodeKey);
            var publicId = ReadString(document.RootElement, OrderPublicIdKey);

            // **An unparseable public id costs the order and not the template code.** They are
            // independent facts, and a details payload with a broken id still tells a Super Admin which
            // message died.
            return Guid.TryParseExact(publicId, "D", out var parsed)
                ? new AlertDetails(
                    parsed.ToString("D", CultureInfo.InvariantCulture),
                    ReadString(document.RootElement, OrderNumberKey),
                    templateCode)
                : new AlertDetails(null, null, templateCode);
        }
        catch (JsonException)
        {
            return new AlertDetails(null, null, null);
        }
    }

    private static string? ReadString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
