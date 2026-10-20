using SpencerDenim.Application.Abstractions.Auditing;
using SpencerDenim.Application.Content.Admin.Abstractions;
using SpencerDenim.Domain.Catalog;
using SpencerDenim.Domain.Content;
using SpencerDenim.Domain.Media;

namespace SpencerDenim.UnitTests.Application.Content.Admin;

/// <summary>The media library, as the content writes see it: a few assets of each kind and state.</summary>
internal static class TestMedia
{
    public static readonly Guid Photo = Guid.Parse("11111111-1111-1111-1111-111111111111");
    public static readonly Guid OtherPhoto = Guid.Parse("22222222-2222-2222-2222-222222222222");
    public static readonly Guid Film = Guid.Parse("33333333-3333-3333-3333-333333333333");
    public static readonly Guid NoAlt = Guid.Parse("44444444-4444-4444-4444-444444444444");
    public static readonly Guid Processing = Guid.Parse("55555555-5555-5555-5555-555555555555");
    public static readonly Guid PrivateSlip = Guid.Parse("66666666-6666-6666-6666-666666666666");
    public static readonly Guid Unknown = Guid.Parse("77777777-7777-7777-7777-777777777777");

    public static readonly Dictionary<Guid, ContentMediaAsset> Library = new()
    {
        [Photo] = new(101, MediaAssetType.Image, MediaAssetStatus.Ready, MediaVisibility.Public, "A linen shirt"),
        [OtherPhoto] = new(102, MediaAssetType.Image, MediaAssetStatus.Ready, MediaVisibility.Public, "Denim"),
        [Film] = new(103, MediaAssetType.Video, MediaAssetStatus.Ready, MediaVisibility.Public, null),
        [NoAlt] = new(104, MediaAssetType.Image, MediaAssetStatus.Ready, MediaVisibility.Public, "  "),
        [Processing] = new(105, MediaAssetType.Image, MediaAssetStatus.Processing, MediaVisibility.Public, "x"),
        [PrivateSlip] = new(106, MediaAssetType.Image, MediaAssetStatus.Ready, MediaVisibility.Private, "slip"),
    };
}

/// <summary>An in-memory homepage store: ten blocks, nothing else, and a save that can be told to conflict.</summary>
internal sealed class FakeHomeContentAdminStore : IHomeContentAdminStore
{
    private int _nextItemId = 500;

    public FakeHomeContentAdminStore()
    {
        foreach (var type in Enum.GetValues<HomeSectionType>())
        {
            Sections.Add(new HomeSection
            {
                Id = (int)type,
                SectionType = type,
                SortOrder = (short)type,
                RowVersion = [0, 0, 0, 0, 0, 0, 0, (byte)type],
            });
        }
    }

    public List<HomeSection> Sections { get; } = [];

    public HashSet<int> Departments { get; } = [1, 2, 3];

    public Dictionary<string, int> Products { get; } = new(StringComparer.OrdinalIgnoreCase) { ["linen-shirt"] = 41 };

    public List<HomeSection> Changed { get; } = [];

    public List<HomeSectionItem> Removed { get; } = [];

    public int Saves { get; private set; }

    public ContentMediaUsageChange? LastUsage { get; private set; }

    public int? LastUsageOwnerId { get; private set; }

    public ContentSaveOutcome NextOutcome { get; set; } = ContentSaveOutcome.Saved;

    public HomeSection Section(HomeSectionType type) => Sections.Single(section => section.SectionType == type);

    public string RowVersionOf(HomeSectionType type) => Convert.ToBase64String(Section(type).RowVersion!);

    public HomeSectionItem AddExisting(HomeSectionType type, Action<HomeSectionItem>? configure = null)
    {
        var section = Section(type);
        var item = new HomeSectionItem { Id = _nextItemId++, HomeSectionId = section.Id, HomeSection = section, IsEnabled = true };
        configure?.Invoke(item);
        section.Items.Add(item);
        return item;
    }

    public Task<HomeSection?> FindSectionAsync(int sectionId, CancellationToken cancellationToken) =>
        Task.FromResult(Sections.FirstOrDefault(section => section.Id == sectionId));

    public Task<HomeSection?> FindSectionOfItemAsync(int itemId, CancellationToken cancellationToken) =>
        Task.FromResult(Sections.FirstOrDefault(section => section.Items.Any(item => item.Id == itemId)));

    public Task<bool> DepartmentExistsAsync(int departmentId, CancellationToken cancellationToken) =>
        Task.FromResult(Departments.Contains(departmentId));

    public Task<int?> FindProductIdAsync(string slug, CancellationToken cancellationToken) =>
        Task.FromResult(Products.TryGetValue(slug, out var id) ? id : (int?)null);

    public Task<ContentMediaAsset?> FindMediaAsync(Guid publicId, CancellationToken cancellationToken) =>
        Task.FromResult(TestMedia.Library.GetValueOrDefault(publicId));

    public void AddItem(HomeSectionItem item)
    {
        var section = Sections.Single(candidate => candidate.Id == item.HomeSectionId);
        section.Items.Add(item);
    }

    public void RemoveItem(HomeSectionItem item)
    {
        Removed.Add(item);
        Sections.Single(candidate => candidate.Id == item.HomeSectionId).Items.Remove(item);
    }

    public void MarkChanged(HomeSection section) => Changed.Add(section);

    public Task<ContentSaveOutcome> SaveAsync(int? actingUserId, ContentMediaUsageChange? usage, CancellationToken cancellationToken)
    {
        Saves++;

        if (NextOutcome != ContentSaveOutcome.Saved)
        {
            return Task.FromResult(NextOutcome);
        }

        foreach (var section in Sections)
        {
            foreach (var item in section.Items.Where(item => item.Id == 0))
            {
                item.Id = _nextItemId++;
            }
        }

        foreach (var section in Changed.Distinct())
        {
            section.RowVersion = [9, 9, 9, 9, 9, 9, 9, (byte)section.Id];
        }

        LastUsage = usage;
        LastUsageOwnerId = usage?.OwnerId();

        return Task.FromResult(ContentSaveOutcome.Saved);
    }
}

/// <summary>An in-memory announcement store. The interceptor's stamping is played by the save.</summary>
internal sealed class FakeAnnouncementAdminStore(DateTime utcNow) : IAnnouncementAdminStore
{
    private int _nextId = 1;

    public List<AnnouncementMessage> Messages { get; } = [];

    public int Saves { get; private set; }

    public AnnouncementMessage AddExisting(string message, DateTime createdAtUtc)
    {
        var row = new AnnouncementMessage { Id = _nextId++, Message = message, IsActive = true, CreatedAtUtc = createdAtUtc };
        Messages.Add(row);
        return row;
    }

    public Task<AnnouncementMessage?> FindAsync(int messageId, CancellationToken cancellationToken) =>
        Task.FromResult(Messages.FirstOrDefault(message => message.Id == messageId));

    public Task<int> CountAsync(CancellationToken cancellationToken) => Task.FromResult(Messages.Count);

    public void Add(AnnouncementMessage message) => Messages.Add(message);

    public void Remove(AnnouncementMessage message) => Messages.Remove(message);

    public Task SaveAsync(int? actingUserId, CancellationToken cancellationToken)
    {
        Saves++;

        foreach (var message in Messages)
        {
            if (message.Id == 0)
            {
                message.Id = _nextId++;
                message.CreatedAtUtc = utcNow;
            }
            else
            {
                message.UpdatedAtUtc = utcNow;
            }
        }

        return Task.CompletedTask;
    }
}

/// <summary>An in-memory size-guide store.</summary>
internal sealed class FakeSizeGuideAdminStore : ISizeGuideAdminStore
{
    private int _nextId = 1;

    public List<SizeGuide> Guides { get; } = [];

    public Dictionary<int, (int Categories, int Products)> Usage { get; } = [];

    public int Saves { get; private set; }

    public ContentMediaUsageChange? LastUsage { get; private set; }

    public int? LastUsageOwnerId { get; private set; }

    public ContentSaveOutcome NextOutcome { get; set; } = ContentSaveOutcome.Saved;

    public SizeGuide AddExisting(string name)
    {
        var guide = new SizeGuide
        {
            Id = _nextId++,
            Name = name,
            TableJson = """{"columns":["Size"],"rows":[["S"]]}""",
            RowVersion = [0, 0, 0, 0, 0, 0, 0, 1],
        };
        Guides.Add(guide);
        return guide;
    }

    public Task<SizeGuide?> FindAsync(int sizeGuideId, CancellationToken cancellationToken) =>
        Task.FromResult(Guides.FirstOrDefault(guide => guide.Id == sizeGuideId && !guide.IsDeleted));

    public Task<(int Categories, int Products)> UsageAsync(int sizeGuideId, CancellationToken cancellationToken) =>
        Task.FromResult(Usage.GetValueOrDefault(sizeGuideId));

    public Task<ContentMediaAsset?> FindMediaAsync(Guid publicId, CancellationToken cancellationToken) =>
        Task.FromResult(TestMedia.Library.GetValueOrDefault(publicId));

    public void Add(SizeGuide sizeGuide) => Guides.Add(sizeGuide);

    public Task<ContentSaveOutcome> SaveAsync(int? actingUserId, ContentMediaUsageChange? usage, CancellationToken cancellationToken)
    {
        Saves++;

        if (NextOutcome != ContentSaveOutcome.Saved)
        {
            return Task.FromResult(NextOutcome);
        }

        foreach (var guide in Guides.Where(guide => guide.Id == 0))
        {
            guide.Id = _nextId++;
        }

        foreach (var guide in Guides)
        {
            guide.RowVersion = [9, 9, 9, 9, 9, 9, 9, (byte)guide.Id];
        }

        LastUsage = usage;
        LastUsageOwnerId = usage?.OwnerId();

        return Task.FromResult(ContentSaveOutcome.Saved);
    }
}

/// <summary>Who the tests act as.</summary>
internal static class TestActors
{
    public static readonly AdminActor Editor = new(7, "editor@example.com", "203.0.113.9", "agent", "corr");
}
