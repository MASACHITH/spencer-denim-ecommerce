using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SpencerDenim.Application.Accounts.Abstractions;
using SpencerDenim.Application.Accounts.Contracts;
using SpencerDenim.Application.Catalog;
using SpencerDenim.Application.Sales.Orders.Contracts;
using SpencerDenim.Domain.Sales;
using SpencerDenim.Infrastructure.Catalog;
using SpencerDenim.Infrastructure.Persistence;

namespace SpencerDenim.Infrastructure.Sales.Orders;

/// <summary>
/// A customer's own order history, with its tracking (ACC-02, doc 11 P11 step 12).
/// </summary>
/// <remarks>
/// <b>This is the one shopper-facing gap W3-01 closes.</b> Before it, a customer placed an order and had
/// no way to look it up again: the tracking number lived in <c>sales.Orders</c> and was read only by the
/// admin screens and the <c>OrderShipped</c> email. Nothing on the storefront showed it.
/// <para>
/// <b>Every query filters <c>CustomerUserId == customerUserId</c> in its own <c>WHERE</c> clause.</b> Not
/// "read the order, then compare the owner": that shape has a window and invites a caller to skip the
/// comparison. <c>IX_Orders_Customer_Placed</c> is the index doc 08 §3.1 names for exactly this read.
/// </para>
/// <para>
/// <b>The ordering is applied before the projection.</b> That is the thing #126's 500 got wrong — a
/// positional-record projection with the ordering applied <i>after</i> the <c>Select</c>, which EF cannot
/// translate, and the whole promo-code admin API answered 500 for a day and a half with no backend test
/// to catch it. <c>CustomerOrderEndpointTests</c> is this route's backend test.
/// </para>
/// </remarks>
internal sealed class CustomerOrderReader(
    AppDbContext dbContext,
    IOptions<CatalogOptions> options) : ICustomerOrderReader
{
    private readonly CatalogOptions _options = options.Value;

    public async Task<CustomerOrderPageDto> ListAsync(
        int customerUserId,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        var safePage = page < 1 ? 1 : page;
        var safePageSize = pageSize is < 1 or > 50 ? 10 : pageSize;

        var mine = dbContext.Orders
            .AsNoTracking()
            .Where(order => order.CustomerUserId == customerUserId);

        var total = await mine.CountAsync(cancellationToken);

        if (total == 0)
        {
            return new CustomerOrderPageDto([], safePage, safePageSize, 0);
        }

        var rows = await mine
            // Newest first (doc 11 P11 step 12), with the key as a tie-break so two orders placed in the
            // same millisecond have a stable order between pages rather than being able to swap and
            // either repeat or hide a row.
            .OrderByDescending(order => order.PlacedAtUtc)
            .ThenByDescending(order => order.Id)
            .Skip((safePage - 1) * safePageSize)
            .Take(safePageSize)
            .Select(order => new OrderRow(
                order.OrderNumber,
                order.PublicId,
                order.Status,
                order.Delivery.DeliveryMethodCode,
                order.PlacedAtUtc,
                order.GrandTotal,
                order.Currency,
                order.Lines.Sum(line => (int)line.Quantity),

                // The first line's thumbnail, by line number so it is the same picture the order page
                // shows at the top. The stored key is a snapshot (doc 08 §3.5): an order keeps showing
                // the file it was placed against, so a withdrawn photograph leaves a gap rather than
                // quietly becoming a different picture.
                order.Lines
                    .OrderBy(line => line.LineNumber)
                    .Select(line => line.ThumbnailStorageKey)
                    .FirstOrDefault(),

                order.CourierName,
                order.TrackingNumber,
                order.ShippedAtUtc,

                // The courier's *current* template, joined by the id the order kept. Doc 08 §3.5: the
                // order keeps the name and the number and never a built URL, so editing a template
                // changes the link on older orders too — the documented behaviour, not a side effect.
                // Null when the courier has since been deleted, in which case the number is shown as
                // text and the shopper can quote it.
                dbContext.Couriers
                    .Where(courier => courier.Id == order.CourierId)
                    .Select(courier => courier.TrackingUrlTemplate)
                    .FirstOrDefault()))
            .ToListAsync(cancellationToken);

        return new CustomerOrderPageDto(
            rows.ConvertAll(row => ToSummary(row, _options.MediaBaseUrl)),
            safePage,
            safePageSize,
            total);
    }

    public async Task<bool> OwnsAsync(int customerUserId, Guid token, CancellationToken cancellationToken) =>
        token != Guid.Empty
        && await dbContext.Orders
            .AsNoTracking()
            .AnyAsync(
                order => order.PublicId == token && order.CustomerUserId == customerUserId,
                cancellationToken);

    /// <summary>
    /// Turns a row into the payload, including the one decision this projection cannot make in SQL.
    /// </summary>
    /// <remarks>
    /// <b>The tracking block is present only when there is something to track with.</b> Doc 08 §3.5
    /// makes <c>CourierName</c>, <c>TrackingNumber</c> and <c>ShippedAtUtc</c> all nullable, and they are
    /// written together by doc 11 P9's dispatch — but a null block and a block of nulls are different
    /// things to a client, and the second one makes every consumer write the same three-way check. So the
    /// block exists or it does not.
    /// <para>
    /// <b>A pickup order has no courier and gets no block</b>, which is right: there is nothing to
    /// follow, and <see cref="CustomerOrderSummaryDto.StatusLabel"/> already reads "Ready for
    /// collection".
    /// </para>
    /// </remarks>
    private static CustomerOrderSummaryDto ToSummary(OrderRow row, string? mediaBaseUrl)
    {
        var isPickup = string.Equals(
            row.DeliveryMethodCode,
            DeliveryMethod.PickupCode,
            StringComparison.Ordinal);

        var tracking = row is { CourierName: { } courierName, TrackingNumber: { } trackingNumber, ShippedAtUtc: { } shippedAtUtc }
            ? new CustomerOrderTrackingDto(
                courierName,
                trackingNumber,
                OrderTrackingLink.Build(row.TrackingUrlTemplate, trackingNumber),
                shippedAtUtc)
            : null;

        return new CustomerOrderSummaryDto(
            row.OrderNumber,
            row.PublicId,
            row.Status.ToString(),
            OrderStatusLabels.For(row.Status, isPickup),
            row.PlacedAtUtc,
            row.GrandTotal,
            row.Currency,
            row.ItemCount,
            MediaUrlBuilder.ToPublicUrl(mediaBaseUrl, row.ThumbnailStorageKey),
            tracking);
    }

    /// <summary>
    /// The columns the list projection reads, in the shape SQL returns them.
    /// </summary>
    /// <remarks>
    /// A record between the query and the payload for <c>CheckoutReader</c>'s reason: the payload's
    /// <c>StatusLabel</c>, its media URL and its tracking link are all computed from more than one column
    /// and cannot be expressed in the projection — and a projection that tried would be exactly the
    /// untranslatable shape #126 shipped.
    /// </remarks>
    private sealed record OrderRow(
        string OrderNumber,
        Guid PublicId,
        OrderStatus Status,
        string DeliveryMethodCode,
        DateTime PlacedAtUtc,
        decimal GrandTotal,
        string Currency,
        int ItemCount,
        string? ThumbnailStorageKey,
        string? CourierName,
        string? TrackingNumber,
        DateTime? ShippedAtUtc,
        string? TrackingUrlTemplate);
}
