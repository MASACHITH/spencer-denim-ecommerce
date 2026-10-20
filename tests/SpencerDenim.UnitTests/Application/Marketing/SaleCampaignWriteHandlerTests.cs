using SpencerDenim.Application.Abstractions.Auditing;
using SpencerDenim.Application.Abstractions.Time;
using SpencerDenim.Application.Content.Abstractions;
using SpencerDenim.Application.Content.Contracts;
using SpencerDenim.Application.Marketing.Promotions;
using SpencerDenim.Application.Marketing.Promotions.Abstractions;
using SpencerDenim.Application.Marketing.Promotions.Contracts;
using SpencerDenim.Domain.Auditing;
using SpencerDenim.Domain.Marketing;

namespace SpencerDenim.UnitTests.Application.Marketing;

/// <summary>
/// The ADM-14 write rules #191 added or leaned on: individual product targets, the banner chosen by its
/// <c>publicId</c>, and the homepage eviction every save owes doc 08 §3.7 (#192).
/// </summary>
/// <remarks>
/// <b>No database.</b> The store, the reader and the refresher are fakes that record what they were asked,
/// in one shared journal, so a test can assert the <i>order</i> of the recompute and the eviction as well
/// as that both happened. The SQL side of each rule is pinned by <c>AdminSaleCampaignEndpointTests</c> and
/// <c>CampaignProductTargetTests</c>.
/// </remarks>
public sealed class SaleCampaignWriteHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 20, 6, 0, 0, TimeSpan.Zero);

    private static readonly AdminActor Actor = new(5, "manager@example.com", "203.0.113.4", "agent", "corr");

    private static readonly Guid Banner = Guid.Parse("0000f00d-0000-4000-8000-000000000001");

    private readonly List<string> _journal = [];
    private readonly FakeStore _store;
    private readonly FakeRefresher _refresher;
    private readonly FakeHomePageCache _homePage;
    private readonly SaleCampaignWriteHandlers _handlers;

    public SaleCampaignWriteHandlerTests()
    {
        _store = new FakeStore(_journal);
        _refresher = new FakeRefresher(_journal);
        _homePage = new FakeHomePageCache(_journal);

        _store.Departments.Add(1);
        _store.Categories.Add(10);
        _store.Products.UnionWith([301, 302, 303]);
        _store.Banners[Banner] = new SaleCampaignBannerCandidate(77, "Linen Week, 20% off");

        _handlers = new SaleCampaignWriteHandlers(
            _store,
            new FakeReader(_store),
            _refresher,
            new NullAuditLogWriter(),
            new FixedClock(Now),
            _homePage);
    }

    // ---- product targets ------------------------------------------------------------------------

    [Fact]
    public async Task A_product_target_is_saved_naming_only_the_product_and_covering_no_subtree_flag()
    {
        var result = await _handlers.HandleAsync(
            new CreateSaleCampaignCommand(Input(targets: [Product(301, includeSubcategories: false)]), Actor),
            Token);

        Assert.IsType<SaleCampaignWriteResult.Succeeded>(result);

        var target = Assert.Single(Assert.Single(_store.Added).Targets);

        Assert.Equal(SaleCampaignTargetType.Product, target.TargetType);
        Assert.Equal(301, target.ProductId);
        Assert.Null(target.DepartmentId);
        Assert.Null(target.CategoryId);

        // Forced on: a product has no subtree, and storing `false` would be a column nothing can read.
        Assert.True(target.IncludeSubcategories);
    }

    [Fact]
    public async Task Products_departments_and_categories_can_share_one_campaign()
    {
        var result = await _handlers.HandleAsync(
            new CreateSaleCampaignCommand(
                Input(targets: [Department(1), Category(10), Product(301), Product(302)]),
                Actor),
            Token);

        Assert.IsType<SaleCampaignWriteResult.Succeeded>(result);
        Assert.Equal(
            [SaleCampaignTargetType.Department, SaleCampaignTargetType.Category, SaleCampaignTargetType.Product, SaleCampaignTargetType.Product],
            Assert.Single(_store.Added).Targets.Select(target => target.TargetType));
    }

    [Fact]
    public async Task The_same_product_twice_is_refused_against_the_second_row()
    {
        var result = await _handlers.HandleAsync(
            new CreateSaleCampaignCommand(Input(targets: [Product(301), Product(302), Product(301)]), Actor),
            Token);

        var invalid = Assert.IsType<SaleCampaignWriteResult.Invalid>(result);
        var error = Assert.Single(invalid.Errors);

        Assert.Equal("targets[2]", error.Field);
        Assert.Contains("already on the campaign", error.Message, StringComparison.Ordinal);
        Assert.Empty(_store.Added);
    }

    /// <summary>
    /// A product and a category with the same number are different targets. The duplicate check keys on
    /// the type as well as the id, or a campaign on category 301 could not also name product 301.
    /// </summary>
    [Fact]
    public async Task A_product_and_a_category_sharing_a_number_are_not_duplicates()
    {
        _store.Categories.Add(301);

        var result = await _handlers.HandleAsync(
            new CreateSaleCampaignCommand(Input(targets: [Category(301), Product(301)]), Actor),
            Token);

        Assert.IsType<SaleCampaignWriteResult.Succeeded>(result);
    }

    [Fact]
    public async Task An_unknown_or_removed_product_is_refused_against_its_row()
    {
        var result = await _handlers.HandleAsync(
            new CreateSaleCampaignCommand(Input(targets: [Product(301), Product(999)]), Actor),
            Token);

        var invalid = Assert.IsType<SaleCampaignWriteResult.Invalid>(result);
        var error = Assert.Single(invalid.Errors);

        Assert.Equal("targets[1]", error.Field);
        Assert.Contains("no longer exists", error.Message, StringComparison.Ordinal);

        // Asked once, with both ids, rather than once per target.
        Assert.Equal([301, 999], Assert.Single(_store.ProductLookups));
    }

    [Fact]
    public async Task A_product_target_without_a_product_id_is_refused()
    {
        var result = await _handlers.HandleAsync(
            new CreateSaleCampaignCommand(
                Input(targets: [new SaleCampaignTargetInput(SaleCampaignTargetType.Product, null, null, null, true)]),
                Actor),
            Token);

        var invalid = Assert.IsType<SaleCampaignWriteResult.Invalid>(result);
        Assert.Equal("targets[0]", Assert.Single(invalid.Errors).Field);
    }

    [Fact]
    public async Task A_product_target_that_also_names_a_category_is_refused()
    {
        var result = await _handlers.HandleAsync(
            new CreateSaleCampaignCommand(
                Input(targets: [new SaleCampaignTargetInput(SaleCampaignTargetType.Product, null, 10, 301, true)]),
                Actor),
            Token);

        var invalid = Assert.IsType<SaleCampaignWriteResult.Invalid>(result);
        Assert.Contains("not more than one", Assert.Single(invalid.Errors).Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(SaleCampaignWriteHandlers.MaxTargets, true)]
    [InlineData(SaleCampaignWriteHandlers.MaxTargets + 1, false)]
    public async Task The_target_cap_counts_product_targets(int count, bool accepted)
    {
        var ids = Enumerable.Range(1_000, count).ToList();
        _store.Products.UnionWith(ids);

        var result = await _handlers.HandleAsync(
            new CreateSaleCampaignCommand(Input(targets: ids.Select(id => Product(id)).ToList()), Actor),
            Token);

        if (accepted)
        {
            Assert.IsType<SaleCampaignWriteResult.Succeeded>(result);
        }
        else
        {
            var invalid = Assert.IsType<SaleCampaignWriteResult.Invalid>(result);
            Assert.Equal("targets", Assert.Single(invalid.Errors).Field);
        }
    }

    // ---- the banner -----------------------------------------------------------------------------

    [Fact]
    public async Task A_banner_named_by_its_public_id_is_stored_as_the_assets_internal_id()
    {
        var result = await _handlers.HandleAsync(
            new CreateSaleCampaignCommand(Input(banner: Banner), Actor),
            Token);

        var saved = Assert.IsType<SaleCampaignWriteResult.Succeeded>(result);

        Assert.Equal(77, Assert.Single(_store.Added).BannerAssetId);
        Assert.Equal([Banner], _store.BannerLookups);
        Assert.DoesNotContain(saved.Saved.Warnings, warning => warning.Contains("banner", StringComparison.Ordinal));
    }

    [Fact]
    public async Task No_banner_stores_none_and_never_asks_the_media_library()
    {
        var result = await _handlers.HandleAsync(new CreateSaleCampaignCommand(Input(), Actor), Token);

        Assert.IsType<SaleCampaignWriteResult.Succeeded>(result);
        Assert.Null(Assert.Single(_store.Added).BannerAssetId);
        Assert.Empty(_store.BannerLookups);
    }

    /// <summary>
    /// Not a ready public image: a video, a private bank slip, an asset still processing, a deleted one.
    /// The store's one query answers null for each (proved against SQL Server by the endpoint suite).
    /// </summary>
    [Fact]
    public async Task A_banner_the_media_library_does_not_offer_is_refused_by_name()
    {
        var result = await _handlers.HandleAsync(
            new CreateSaleCampaignCommand(Input(banner: Guid.Parse("0000f00d-0000-4000-8000-000000000099")), Actor),
            Token);

        var invalid = Assert.IsType<SaleCampaignWriteResult.Invalid>(result);
        var error = Assert.Single(invalid.Errors);

        Assert.Equal("bannerPublicId", error.Field);
        Assert.Contains("ready public image", error.Message, StringComparison.Ordinal);
        Assert.Empty(_store.Added);
    }

    [Fact]
    public async Task The_empty_identifier_is_refused_without_a_lookup()
    {
        var result = await _handlers.HandleAsync(
            new CreateSaleCampaignCommand(Input(banner: Guid.Empty), Actor),
            Token);

        var invalid = Assert.IsType<SaleCampaignWriteResult.Invalid>(result);
        Assert.Equal("bannerPublicId", Assert.Single(invalid.Errors).Field);
        Assert.Empty(_store.BannerLookups);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_banner_without_a_description_saves_with_a_warning(string? altText)
    {
        _store.Banners[Banner] = new SaleCampaignBannerCandidate(78, altText);

        var result = await _handlers.HandleAsync(
            new CreateSaleCampaignCommand(Input(banner: Banner), Actor),
            Token);

        var saved = Assert.IsType<SaleCampaignWriteResult.Succeeded>(result);

        Assert.Equal(78, Assert.Single(_store.Added).BannerAssetId);
        Assert.Contains(saved.Saved.Warnings, warning => warning.StartsWith("The banner image has no description", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Clearing_the_banner_on_an_edit_clears_the_column()
    {
        var campaign = _store.Seed(id: 9, bannerAssetId: 77);

        var result = await _handlers.HandleAsync(
            new UpdateSaleCampaignCommand(9, Input(banner: null), Convert.ToBase64String(campaign.RowVersion!), Actor),
            Token);

        Assert.IsType<SaleCampaignWriteResult.Succeeded>(result);
        Assert.Null(campaign.BannerAssetId);
    }

    // ---- the homepage eviction (#192) -----------------------------------------------------------

    [Fact]
    public async Task A_create_evicts_the_homepage_after_the_recompute()
    {
        await _handlers.HandleAsync(new CreateSaleCampaignCommand(Input(), Actor), Token);

        Assert.Equal(["save", "refresh", "evict"], _journal);
    }

    [Fact]
    public async Task An_edit_evicts_the_homepage_after_the_recompute()
    {
        var campaign = _store.Seed(id: 9);

        await _handlers.HandleAsync(
            new UpdateSaleCampaignCommand(9, Input(), Convert.ToBase64String(campaign.RowVersion!), Actor),
            Token);

        Assert.Equal(["save", "refresh", "evict"], _journal);
    }

    [Fact]
    public async Task A_delete_evicts_the_homepage_after_the_recompute()
    {
        var campaign = _store.Seed(id: 9);

        await _handlers.HandleAsync(
            new DeleteSaleCampaignCommand(9, Convert.ToBase64String(campaign.RowVersion!), Actor),
            Token);

        Assert.Equal(["save", "refresh", "evict"], _journal);
        Assert.True(campaign.IsDeleted);
    }

    /// <summary>
    /// The campaign is committed before the recompute runs, so a failing recompute must still drop a page
    /// assembled before the save. The exception itself still reaches the caller.
    /// </summary>
    [Fact]
    public async Task A_failing_recompute_still_evicts_the_homepage()
    {
        _refresher.Fail = true;

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _handlers.HandleAsync(new CreateSaleCampaignCommand(Input(), Actor), Token));

        Assert.Equal(["save", "refresh", "evict"], _journal);
    }

    [Fact]
    public async Task A_refused_save_evicts_nothing()
    {
        await _handlers.HandleAsync(
            new CreateSaleCampaignCommand(Input(targets: [Product(999)]), Actor),
            Token);

        Assert.Equal(0, _homePage.Evictions);
        Assert.Empty(_journal);
    }

    [Fact]
    public async Task A_stale_save_evicts_nothing()
    {
        var campaign = _store.Seed(id: 9);
        _store.NextOutcome = SaleCampaignSaveOutcome.ConcurrencyConflict;

        var result = await _handlers.HandleAsync(
            new UpdateSaleCampaignCommand(9, Input(), Convert.ToBase64String(campaign.RowVersion!), Actor),
            Token);

        Assert.IsType<SaleCampaignWriteResult.Conflict>(result);
        Assert.Equal(0, _homePage.Evictions);
    }

    [Fact]
    public async Task A_stale_delete_evicts_nothing()
    {
        var campaign = _store.Seed(id: 9);
        _store.NextOutcome = SaleCampaignSaveOutcome.ConcurrencyConflict;

        var result = await _handlers.HandleAsync(
            new DeleteSaleCampaignCommand(9, Convert.ToBase64String(campaign.RowVersion!), Actor),
            Token);

        Assert.IsType<SaleCampaignWriteResult.Conflict>(result);
        Assert.Equal(0, _homePage.Evictions);
    }

    // ---- helpers --------------------------------------------------------------------------------

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static SaleCampaignInput Input(
        IReadOnlyList<SaleCampaignTargetInput>? targets = null,
        Guid? banner = null) =>
        new(
            "Linen Week",
            null,
            SaleDiscountType.Percent,
            20m,
            new DateTime(2026, 10, 20, 0, 0, 0, DateTimeKind.Unspecified),
            new DateTime(2026, 10, 27, 0, 0, 0, DateTimeKind.Unspecified),
            true,
            banner,
            targets ?? [Category(10)]);

    private static SaleCampaignTargetInput Department(int id) =>
        new(SaleCampaignTargetType.Department, id, null, null, true);

    private static SaleCampaignTargetInput Category(int id) =>
        new(SaleCampaignTargetType.Category, null, id, null, true);

    private static SaleCampaignTargetInput Product(int id, bool includeSubcategories = true) =>
        new(SaleCampaignTargetType.Product, null, null, id, includeSubcategories);

    private sealed class FakeStore(List<string> journal) : ISaleCampaignAdminStore
    {
        private readonly Dictionary<int, SaleCampaign> _campaigns = [];
        private int _nextId = 100;

        public HashSet<int> Departments { get; } = [];

        public HashSet<int> Categories { get; } = [];

        public HashSet<int> Products { get; } = [];

        public Dictionary<Guid, SaleCampaignBannerCandidate> Banners { get; } = [];

        public List<SaleCampaign> Added { get; } = [];

        public List<List<int>> ProductLookups { get; } = [];

        public List<Guid> BannerLookups { get; } = [];

        public SaleCampaignSaveOutcome NextOutcome { get; set; } = SaleCampaignSaveOutcome.Saved;

        public SaleCampaign? Get(int id) => _campaigns.GetValueOrDefault(id);

        public SaleCampaign Seed(int id, int? bannerAssetId = null)
        {
            var campaign = new SaleCampaign
            {
                Id = id,
                Name = "Seeded",
                DiscountType = SaleDiscountType.Percent,
                DiscountValue = 10m,
                StartsAtUtc = new DateTime(2026, 10, 19, 18, 30, 0, DateTimeKind.Utc),
                EndsAtUtc = new DateTime(2026, 10, 26, 18, 30, 0, DateTimeKind.Utc),
                IsActive = true,
                BannerAssetId = bannerAssetId,
                RowVersion = [0, 0, 0, 0, 0, 0, 0, 1],
            };

            campaign.Targets.Add(new SaleCampaignTarget
            {
                TargetType = SaleCampaignTargetType.Category,
                CategoryId = 10,
                IncludeSubcategories = true,
            });

            _campaigns[id] = campaign;
            return campaign;
        }

        public Task<SaleCampaign?> FindAsync(int campaignId, CancellationToken cancellationToken) =>
            Task.FromResult(_campaigns.TryGetValue(campaignId, out var campaign) && !campaign.IsDeleted
                ? campaign
                : null);

        public void Add(SaleCampaign campaign) => Added.Add(campaign);

        public Task<SaleCampaignTargetExistence> WhichTargetsExistAsync(
            IReadOnlyCollection<int> departmentIds,
            IReadOnlyCollection<int> categoryIds,
            IReadOnlyCollection<int> productIds,
            CancellationToken cancellationToken)
        {
            ProductLookups.Add([.. productIds]);

            return Task.FromResult(new SaleCampaignTargetExistence(
                departmentIds.Where(Departments.Contains).ToHashSet(),
                categoryIds.Where(Categories.Contains).ToHashSet(),
                productIds.Where(Products.Contains).ToHashSet()));
        }

        public Task<SaleCampaignBannerCandidate?> FindBannerAsync(Guid publicId, CancellationToken cancellationToken)
        {
            BannerLookups.Add(publicId);
            return Task.FromResult(Banners.GetValueOrDefault(publicId));
        }

        public Task<SaleCampaignSaveOutcome> SaveAsync(
            SaleCampaign campaign,
            int? actingUserId,
            CancellationToken cancellationToken)
        {
            journal.Add("save");

            if (NextOutcome == SaleCampaignSaveOutcome.Saved && campaign.Id == 0)
            {
                campaign.Id = _nextId++;
                campaign.RowVersion = [0, 0, 0, 0, 0, 0, 0, 2];
                _campaigns[campaign.Id] = campaign;
            }

            return Task.FromResult(NextOutcome);
        }
    }

    private sealed class FakeReader(FakeStore store) : ISaleCampaignAdminReader
    {
        public Task<IReadOnlyList<AdminSaleCampaignDto>> ListAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<AdminSaleCampaignDto?> FindAsync(int campaignId, CancellationToken cancellationToken)
        {
            var campaign = store.Get(campaignId);

            return Task.FromResult(campaign is null
                ? null
                : new AdminSaleCampaignDto(
                    campaign.Id,
                    campaign.Name,
                    campaign.Description,
                    campaign.DiscountType,
                    campaign.DiscountValue,
                    campaign.StartsAtUtc,
                    campaign.EndsAtUtc,
                    campaign.IsActive,
                    null,
                    false,
                    [],
                    PromoCodeStatus.Live,
                    "AAAAAAAAAAI="));
        }
    }

    private sealed class FakeRefresher(List<string> journal) : ICampaignPricingRefresher
    {
        public bool Fail { get; set; }

        public Task<CampaignPricingRefreshPass> RefreshProductsAsync(
            IReadOnlyCollection<int> productIds,
            CancellationToken cancellationToken)
        {
            journal.Add("refresh");

            return Fail
                ? throw new InvalidOperationException("The recompute failed.")
                : Task.FromResult(new CampaignPricingRefreshPass(productIds.Count, 1, productIds.Count));
        }

        public Task<IReadOnlyList<int>> ProductsTargetedByAsync(
            IReadOnlyCollection<int> campaignIds,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<int>>([301]);

        public Task<IReadOnlyList<int>> ProductsLinkedToCategoriesAsync(
            IReadOnlyCollection<int> categoryIds,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<CampaignPricingRefreshPass> RefreshBoundariesAsync(
            DateTime sinceUtc,
            DateTime untilUtc,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class FakeHomePageCache(List<string> journal) : IHomePageCache
    {
        public int Evictions { get; private set; }

        public long Generation => Evictions;

        public HomePageDto? GetPage() => null;

        public void SetPage(HomePageDto page, DateTime? validUntilUtc, long generation)
        {
        }

        public void Evict()
        {
            Evictions++;
            journal.Add("evict");
        }
    }

    private sealed class NullAuditLogWriter : IAuditLogWriter
    {
        public void Enlist(AuditLog entry)
        {
        }

        public Task WriteAsync(AuditLog entry, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow => now;
    }
}
