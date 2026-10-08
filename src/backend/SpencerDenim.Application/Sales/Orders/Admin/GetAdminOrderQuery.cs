using SpencerDenim.Application.Abstractions.Messaging;
using SpencerDenim.Application.Abstractions.Time;
using SpencerDenim.Application.Sales.Orders.Abstractions;
using SpencerDenim.Application.Sales.Orders.Contracts;

namespace SpencerDenim.Application.Sales.Orders.Admin;

/// <summary>
/// One order in full (ADM-09, doc 11 P9; <c>Orders.View</c>).
/// </summary>
/// <param name="OrderPublicId">
/// The order's public token, which is what the list, every link and the shopper's own order link all
/// carry (GLB-11).
/// </param>
public sealed record GetAdminOrderQuery(Guid OrderPublicId);

/// <summary>Reads one order, or answers <see langword="null"/> for a 404.</summary>
public sealed class GetAdminOrderQueryHandler(IAdminOrderReader reader, IClock clock)
    : IQueryHandler<GetAdminOrderQuery, AdminOrderDetailDto?>
{
    public async Task<AdminOrderDetailDto?> HandleAsync(
        GetAdminOrderQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        return await reader.ReadDetailAsync(
            query.OrderPublicId,
            clock.UtcNow.UtcDateTime,
            cancellationToken);
    }
}

/// <summary>
/// One order's invoice and packing copy (ADM-09, doc 11 P9 step 3; <c>Orders.View</c>).
/// </summary>
/// <param name="OrderPublicId">The order's public token.</param>
public sealed record GetOrderInvoiceQuery(Guid OrderPublicId);

/// <summary>
/// Reads an invoice and attaches the seller's legal details from <c>ops.Settings</c>.
/// </summary>
/// <remarks>
/// <b>The seller's details are joined on here rather than in the reader, because they are not the
/// order's.</b> The reader's job is the order's own snapshot columns; this is the one value on the
/// document that comes from live settings, and keeping the seam visible is what makes it obvious that an
/// unset key prints as nothing (see <see cref="InvoiceSellerDetails"/>) rather than as a default.
/// <para>
/// <b>Two reads rather than a join</b>, deliberately: the orders table and the settings table share
/// nothing, and an order with no invoice number must not pay for a settings read at all — which is why
/// the null check comes first.
/// </para>
/// </remarks>
public sealed class GetOrderInvoiceQueryHandler(IAdminOrderReader reader, IOrderSettingsReader settings)
    : IQueryHandler<GetOrderInvoiceQuery, OrderInvoiceDto?>
{
    public async Task<OrderInvoiceDto?> HandleAsync(
        GetOrderInvoiceQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (await reader.ReadInvoiceAsync(query.OrderPublicId, cancellationToken) is not { } invoice)
        {
            return null;
        }

        return invoice with { Seller = await settings.ReadInvoiceSellerAsync(cancellationToken) };
    }
}

/// <summary>Every courier, for the dispatch picker and the couriers screen (<c>Orders.View</c>).</summary>
public sealed record ListCouriersQuery;

/// <summary>Reads the courier list.</summary>
public sealed class ListCouriersQueryHandler(IAdminOrderReader reader)
    : IQueryHandler<ListCouriersQuery, IReadOnlyList<AdminCourierDto>>
{
    public async Task<IReadOnlyList<AdminCourierDto>> HandleAsync(
        ListCouriersQuery query,
        CancellationToken cancellationToken) =>
        await reader.ReadCouriersAsync(cancellationToken);
}
