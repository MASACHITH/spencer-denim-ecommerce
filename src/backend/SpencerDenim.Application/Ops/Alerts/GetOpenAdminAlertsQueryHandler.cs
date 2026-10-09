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
            return new AdminAlertBannerDto(0, []);
        }

        var (totalOpen, rows) = await reader.GetOpenAsync(
            visibleTypes,
            GetOpenAdminAlertsQuery.MaxAlerts,
            cancellationToken);

        return new AdminAlertBannerDto(totalOpen, [.. rows.Select(ToDto)]);
    }

    private static AdminAlertDto ToDto(AdminAlertRow row)
    {
        var (orderPublicId, orderNumber) = ReadOrder(row.DetailsJson);

        return new AdminAlertDto(
            row.Id,
            row.AlertType,
            row.Severity,
            row.Title,
            row.CreatedAtUtc,
            row.RelatedEntityType,
            row.RelatedEntityId,
            orderPublicId,
            orderNumber);
    }

    /// <summary>
    /// Lifts the order link out of an alert's details, and answers nulls for anything it cannot read.
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
    private static (string? OrderPublicId, string? OrderNumber) ReadOrder(string? detailsJson)
    {
        if (string.IsNullOrWhiteSpace(detailsJson))
        {
            return (null, null);
        }

        try
        {
            using var document = JsonDocument.Parse(detailsJson);

            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return (null, null);
            }

            var publicId = ReadString(document.RootElement, OrderPublicIdKey);

            return Guid.TryParseExact(publicId, "D", out var parsed)
                ? (parsed.ToString("D", CultureInfo.InvariantCulture),
                    ReadString(document.RootElement, OrderNumberKey))
                : (null, null);
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }

    private static string? ReadString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
