using SpencerDenim.Application.Abstractions.Auditing;
using SpencerDenim.Application.Abstractions.Time;
using SpencerDenim.Application.Inventory;
using SpencerDenim.Application.Inventory.Abstractions;
using SpencerDenim.Application.Inventory.Contracts;
using SpencerDenim.Domain.Inventory;

namespace SpencerDenim.UnitTests.Application.Inventory;

/// <summary>
/// The inventory use cases' own decisions (ADM-12): validation is all or nothing and names every bad field,
/// a well-formed request reaches the store once with a trimmed reason, and a batch id is minted for a
/// request of more than one row only.
/// </summary>
public sealed class InventoryWriteHandlersTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 19, 6, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task A_well_formed_single_change_reaches_the_store_with_a_trimmed_reason_and_no_batch()
    {
        var store = new RecordingStore();
        var handlers = Handlers(store);

        var result = await handlers.HandleAsync(
            new ApplyStockChangesCommand([new StockChangeInput(7, StockChangeKind.Adjust, -2, null)], "  Damaged  ", AdminActor.Unknown),
            CancellationToken.None);

        Assert.IsType<InventoryWriteResult.Applied>(result);
        var batch = Assert.Single(store.Batches);
        Assert.Equal("Damaged", batch.Reason);
        Assert.Null(batch.BatchId);
        Assert.Equal(Now.UtcDateTime, batch.NowUtc);
        Assert.Equal(new StockChangeRequest(7, StockChangeKind.Adjust, -2, null), Assert.Single(batch.Rows));
    }

    [Fact]
    public async Task A_request_of_several_rows_shares_one_new_batch_id_and_keeps_its_order()
    {
        var store = new RecordingStore();

        await Handlers(store).HandleAsync(
            new ApplyStockChangesCommand(
                [
                    new StockChangeInput(9, StockChangeKind.Receive, 4, null),
                    new StockChangeInput(3, StockChangeKind.SetCount, 0, 2),
                ],
                "Delivery",
                AdminActor.Unknown),
            CancellationToken.None);

        var batch = Assert.Single(store.Batches);
        Assert.NotNull(batch.BatchId);
        Assert.NotEqual(Guid.Empty, batch.BatchId);
        Assert.Equal([9, 3], batch.Rows.Select(row => row.VariantId));
    }

    /// <summary>One bad row among good ones refuses the request, every bad field is named, and nothing is applied.</summary>
    [Fact]
    public async Task Validation_collects_every_refusal_and_nothing_reaches_the_store()
    {
        var store = new RecordingStore();

        var result = await Handlers(store).HandleAsync(
            new ApplyStockChangesCommand(
                [
                    new StockChangeInput(1, StockChangeKind.Receive, 2, null),
                    new StockChangeInput(null, StockChangeKind.Adjust, 1, null),
                    new StockChangeInput(1, StockChangeKind.Adjust, 1, null),
                    new StockChangeInput(4, null, 1, null),
                    new StockChangeInput(5, StockChangeKind.Adjust, null, null),
                    new StockChangeInput(6, StockChangeKind.SetCount, 3, null),
                ],
                " ",
                AdminActor.Unknown),
            CancellationToken.None);

        var invalid = Assert.IsType<InventoryWriteResult.Invalid>(result);
        Assert.Equal(
            ["reason", "rows[1].variantId", "rows[2].variantId", "rows[3].kind", "rows[4].quantity", "rows[5].expectedOnHand"],
            invalid.Errors.Select(error => error.Field).Order(StringComparer.Ordinal));
        Assert.Empty(store.Batches);
    }

    [Fact]
    public async Task No_rows_and_too_many_rows_are_refused()
    {
        var store = new RecordingStore();
        var handlers = Handlers(store);

        var none = await handlers.HandleAsync(new ApplyStockChangesCommand([], "x", AdminActor.Unknown), CancellationToken.None);
        var missing = await handlers.HandleAsync(new ApplyStockChangesCommand(null, "x", AdminActor.Unknown), CancellationToken.None);
        var tooMany = await handlers.HandleAsync(
            new ApplyStockChangesCommand(
                Enumerable.Range(1, StockChangeRules.MaxBulkRows + 1).Select(id => new StockChangeInput(id, StockChangeKind.Adjust, 1, null)).ToList(),
                "x",
                AdminActor.Unknown),
            CancellationToken.None);
        var exactly = await handlers.HandleAsync(
            new ApplyStockChangesCommand(
                Enumerable.Range(1, StockChangeRules.MaxBulkRows).Select(id => new StockChangeInput(id, StockChangeKind.Adjust, 1, null)).ToList(),
                "x",
                AdminActor.Unknown),
            CancellationToken.None);

        Assert.Equal("rows", Assert.Single(Assert.IsType<InventoryWriteResult.Invalid>(none).Errors).Field);
        Assert.Equal("rows", Assert.Single(Assert.IsType<InventoryWriteResult.Invalid>(missing).Errors).Field);
        Assert.Equal("rows", Assert.Single(Assert.IsType<InventoryWriteResult.Invalid>(tooMany).Errors).Field);
        Assert.IsType<InventoryWriteResult.Applied>(exactly);
        Assert.Single(store.Batches);
    }

    [Fact]
    public async Task A_threshold_out_of_range_never_reaches_the_store()
    {
        var store = new RecordingStore();

        var result = await Handlers(store).HandleAsync(new SetLowStockThresholdCommand(3, -1, AdminActor.Unknown), CancellationToken.None);

        Assert.Equal("lowStockThreshold", Assert.Single(Assert.IsType<InventoryWriteResult.Invalid>(result).Errors).Field);
        Assert.Empty(store.Thresholds);
    }

    [Fact]
    public async Task A_threshold_for_a_variant_with_no_stock_record_is_not_found()
    {
        var store = new RecordingStore { ThresholdOutcome = ThresholdChangeOutcome.NotFound };

        var result = await Handlers(store).HandleAsync(new SetLowStockThresholdCommand(3, null, AdminActor.Unknown), CancellationToken.None);

        Assert.IsType<InventoryWriteResult.NotFound>(result);
        Assert.Equal((3, (int?)null), Assert.Single(store.Thresholds));
    }

    [Theory]
    [InlineData(null, null, 1, InventoryListCriteria.DefaultPageSize)]
    [InlineData(0, 0, 1, 1)]
    [InlineData(-5, 100_000, 1, InventoryListCriteria.MaxPageSize)]
    [InlineData(int.MaxValue, 10, InventoryListCriteria.MaxPage, 10)]
    public void Paging_is_clamped_never_refused(int? page, int? size, int expectedPage, int expectedSize) =>
        Assert.Equal((expectedPage, expectedSize), InventoryQueryHandlers.Clamp(page, size));

    private static InventoryWriteHandlers Handlers(RecordingStore store) => new(store, new NoReader(), new FixedClock());

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => Now;
    }

    private sealed class RecordingStore : IInventoryStore
    {
        public List<StockChangeBatch> Batches { get; } = [];

        public List<(int VariantId, int? Threshold)> Thresholds { get; } = [];

        public ThresholdChangeOutcome ThresholdOutcome { get; init; } = ThresholdChangeOutcome.Applied;

        public Task<StockChangeResultDto> ApplyAsync(StockChangeBatch batch, CancellationToken cancellationToken)
        {
            Batches.Add(batch);
            return Task.FromResult(new StockChangeResultDto(batch.BatchId, 0, 0, []));
        }

        public Task<ThresholdChangeOutcome> SetThresholdAsync(
            int variantId,
            int? threshold,
            AdminActor actor,
            DateTime nowUtc,
            CancellationToken cancellationToken)
        {
            Thresholds.Add((variantId, threshold));
            return Task.FromResult(ThresholdOutcome);
        }
    }

    private sealed class NoReader : IInventoryReader
    {
        public Task<InventoryListPage> ListAsync(InventoryListCriteria criteria, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<InventoryFilterOptions> FilterOptionsAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<InventoryRow?> FindAsync(int variantId, CancellationToken cancellationToken) =>
            Task.FromResult<InventoryRow?>(null);

        public Task<InventoryHistoryPage?> HistoryAsync(int variantId, int page, int pageSize, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
