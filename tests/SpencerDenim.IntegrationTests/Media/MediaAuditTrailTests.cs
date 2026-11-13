using System.Data.Common;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using SpencerDenim.Application.Abstractions.Auditing;
using SpencerDenim.Application.Media.Abstractions;
using SpencerDenim.Application.Media.Contracts;
using SpencerDenim.Domain.Auditing;
using SpencerDenim.Domain.Media;
using SpencerDenim.Infrastructure.Persistence;
using SpencerDenim.IntegrationTests.Infrastructure;
using SpencerDenim.Tests.MediaFixtures;

namespace SpencerDenim.IntegrationTests.Media;

/// <summary>
/// #59 (cases found by the independent tester): the media library's audit rows under the paths the endpoint
/// tests do not reach — a lost de-dup race at the unique index, a publish that fails after its row was enlisted, a video
/// and its poster, a private asset, and a delete retried by the execution strategy (both a lost acknowledgement and a commit that really failed).
/// </summary>
public sealed class MediaAuditTrailTests(SqlServerDatabaseFixture sqlServer)
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private readonly string _corr = "probe-" + Guid.NewGuid().ToString("N")[..20];

    private AdminActor Actor => new(null, "probe@example.com", "203.0.113.9", "ProbeAgent/1.0", _corr);

    private AdminActor OtherActor { get; } = new(null, "probe@example.com", "203.0.113.9", "ProbeAgent/1.0", "probe-other-" + Guid.NewGuid().ToString("N")[..12]);

    [Fact]
    public async Task Losing_the_de_dup_race_at_the_unique_index_leaves_no_audit_row_for_the_loser()
    {
        // With the cheap lookup switched off, the second upload of the same bytes reaches PublishAsync and is refused by
        // UX_MediaAssets_Checksum_Ready — exactly the path a concurrent loser takes.
        await using var host = await StartAsync(new Dictionary<string, string?> { ["Media:DeduplicateByChecksum"] = "false" });
        var bytes = TestImages.UniqueJpeg(500, 400);
        var since = DateTime.UtcNow.AddSeconds(-1);

        var first = Assert.IsType<MediaUploadOutcome.Created>(await UploadImageAsync(host, bytes, "first.jpg", MediaVisibility.Public, Actor));
        var second = Assert.IsType<MediaUploadOutcome.Duplicate>(await UploadImageAsync(host, bytes, "second.jpg", MediaVisibility.Public, Actor));
        Assert.Equal(first.Asset.PublicId, second.Asset.PublicId);

        var rows = await MediaRowsSinceAsync(host, since);
        var row = Assert.Single(rows);
        Assert.Equal(AuditActions.Create, row.Action);
        Assert.Contains("first.jpg", row.AfterJson, StringComparison.Ordinal);
        Assert.Equal("probe@example.com", row.UserEmail);
        Assert.Equal("203.0.113.9", row.IpAddress);
        Assert.Equal("ProbeAgent/1.0", row.UserAgent);
        Assert.Equal(_corr, row.CorrelationId);
        Assert.Empty(await OrphanRowsAsync(host, since));
    }

    [Fact]
    public async Task Simultaneous_uploads_of_the_same_bytes_write_exactly_one_create_row_and_no_orphans()
    {
        await using var host = await StartAsync();
        var bytes = TestImages.UniqueJpeg(900, 1200);
        var since = DateTime.UtcNow.AddSeconds(-1);

        var outcomes = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(index => UploadImageAsync(host, bytes, $"race-{index}.jpg", MediaVisibility.Public, Actor)));

        Assert.Single(outcomes.OfType<MediaUploadOutcome.Created>());
        Assert.Single(await MediaRowsSinceAsync(host, since));
        Assert.Empty(await OrphanRowsAsync(host, since));
    }

    [Fact]
    public async Task A_publish_that_fails_after_its_row_was_enlisted_writes_no_row()
    {
        var failer = new FailMediaAuditSaveOnce();
        await using var host = await StartAsync(configureServices: services => services.AddSingleton<IInterceptor>(failer));
        var since = DateTime.UtcNow.AddSeconds(-1);

        await Assert.ThrowsAnyAsync<Exception>(() =>
            UploadImageAsync(host, TestImages.UniqueJpeg(400, 300), "doomed.jpg", MediaVisibility.Public, Actor));

        Assert.Equal(1, failer.Fired);
        Assert.Empty(await MediaRowsSinceAsync(host, since));
    }

    [Fact]
    public async Task A_video_upload_audits_the_video_and_its_new_poster_and_a_duplicate_video_audits_nothing()
    {
        await using var host = await StartAsync();
        var video = TestVideos.UniqueMp4(1280, 720, 6.0);
        var since = DateTime.UtcNow.AddSeconds(-1);

        var created = Assert.IsType<MediaUploadOutcome.Created>(
            await UploadVideoAsync(host, video, TestImages.UniqueJpeg(1280, 720), Actor));

        var rows = await MediaRowsSinceAsync(host, since);
        Assert.Equal(2, rows.Count);
        Assert.All(rows, row => Assert.Equal(AuditActions.Create, row.Action));
        Assert.Single(rows, row => row.AfterJson!.Contains("\"mediaType\":\"Video\"", StringComparison.Ordinal));
        var posterRow = Assert.Single(rows, row => row.AfterJson!.Contains("posterOf", StringComparison.Ordinal));
        Assert.Contains(created.Asset.PublicId.ToString(), posterRow.AfterJson, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(await OrphanRowsAsync(host, since));

        // The same video bytes with a different, new poster: Duplicate, the new poster rolled back, nothing audited.
        var since2 = DateTime.UtcNow.AddSeconds(-1);
        Assert.IsType<MediaUploadOutcome.Duplicate>(await UploadVideoAsync(host, video, TestImages.UniqueJpeg(1280, 720), Actor));
        var after2 = await MediaRowsSinceAsync(host, since2);
        Assert.Equal(after2.Count, after2.Count(row => rows.Any(old => old.Id == row.Id)));
    }

    [Fact]
    public async Task A_video_whose_poster_already_exists_audits_only_the_video()
    {
        await using var host = await StartAsync();
        var poster = TestImages.UniqueJpeg(1280, 720);
        Assert.IsType<MediaUploadOutcome.Created>(await UploadImageAsync(host, poster, "p.jpg", MediaVisibility.Public, OtherActor));
        var since = DateTime.UtcNow.AddSeconds(-1);

        Assert.IsType<MediaUploadOutcome.Created>(await UploadVideoAsync(host, TestVideos.UniqueMp4(1280, 720, 6.0), poster, Actor));

        var row = Assert.Single(await MediaRowsSinceAsync(host, since));
        Assert.Contains("\"mediaType\":\"Video\"", row.AfterJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_private_upload_is_not_audited_as_a_library_upload()
    {
        await using var host = await StartAsync();
        var since = DateTime.UtcNow.AddSeconds(-1);

        Assert.IsType<MediaUploadOutcome.Created>(
            await UploadImageAsync(host, TestImages.UniqueJpeg(400, 300), "slip.jpg", MediaVisibility.Private, Actor));

        Assert.Empty(await MediaRowsSinceAsync(host, since));
    }

    [Fact]
    public async Task A_delete_retried_after_a_lost_commit_acknowledgement_writes_one_row()
    {
        var injector = new CommitFaultOnce(afterCommit: true);
        await using var host = await StartAsync(configureServices: services => services.AddSingleton<IInterceptor>(injector));
        var asset = Assert.IsType<MediaUploadOutcome.Created>(
            await UploadImageAsync(host, TestImages.UniqueJpeg(400, 300), "ack.jpg", MediaVisibility.Public, OtherActor)).Asset;
        var since = DateTime.UtcNow.AddSeconds(-1);

        await using (var scope = host.Factory.Services.CreateAsyncScope())
        {
            injector.Arm();
            Assert.IsType<MediaDeleteOutcome.Deleted>(await scope.ServiceProvider.GetRequiredService<IMediaService>()
                .DeleteAsync(asset.PublicId, null, Token, Actor));
        }

        Assert.Equal(1, injector.Fired);
        var row = Assert.Single(await MediaRowsSinceAsync(host, since), row => row.Action == AuditActions.SoftDelete);
        Assert.Equal(_corr, row.CorrelationId);
    }

    [Fact]
    public async Task A_delete_retried_after_a_commit_that_really_failed_writes_one_row()
    {
        var injector = new CommitFaultOnce(afterCommit: false);
        await using var host = await StartAsync(configureServices: services => services.AddSingleton<IInterceptor>(injector));
        var asset = Assert.IsType<MediaUploadOutcome.Created>(
            await UploadImageAsync(host, TestImages.UniqueJpeg(400, 300), "rolled.jpg", MediaVisibility.Public, OtherActor)).Asset;
        var since = DateTime.UtcNow.AddSeconds(-1);

        await using (var scope = host.Factory.Services.CreateAsyncScope())
        {
            injector.Arm();
            Assert.IsType<MediaDeleteOutcome.Deleted>(await scope.ServiceProvider.GetRequiredService<IMediaService>()
                .DeleteAsync(asset.PublicId, null, Token, Actor));
        }

        Assert.Equal(1, injector.Fired);
        Assert.Single(await MediaRowsSinceAsync(host, since), row => row.Action == AuditActions.SoftDelete);

        await using var check = host.Factory.Services.CreateAsyncScope();
        Assert.True(await check.ServiceProvider.GetRequiredService<AppDbContext>().MediaAssets.IgnoreQueryFilters()
            .Where(candidate => candidate.PublicId == asset.PublicId).Select(candidate => candidate.IsDeleted).SingleAsync(Token));
    }

    [Fact]
    public async Task A_second_delete_of_the_same_asset_is_404_and_writes_no_second_row()
    {
        await using var host = await StartAsync();
        var asset = Assert.IsType<MediaUploadOutcome.Created>(
            await UploadImageAsync(host, TestImages.UniqueJpeg(400, 300), "twice.jpg", MediaVisibility.Public, OtherActor)).Asset;
        var since = DateTime.UtcNow.AddSeconds(-1);

        var outcomes = await Task.WhenAll(Enumerable.Range(0, 4).Select(async _ =>
        {
            await using var scope = host.Factory.Services.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<IMediaService>().DeleteAsync(asset.PublicId, null, Token, Actor);
        }));

        Assert.Single(outcomes.OfType<MediaDeleteOutcome.Deleted>());
        Assert.Single(await MediaRowsSinceAsync(host, since), row => row.Action == AuditActions.SoftDelete);
    }

    // ------------------------------------------------------------------ helpers

    private async Task<MediaTestHost> StartAsync(
        IReadOnlyDictionary<string, string?>? configure = null,
        Action<IServiceCollection>? configureServices = null)
    {
        sqlServer.SkipIfUnavailable();
        await TestDatabaseMigrator.MigrateAsync(sqlServer.ConnectionString, Token);
        return MediaTestHost.Create(sqlServer.ConnectionString, configure, configureServices: configureServices);
    }

    private static async Task<MediaUploadOutcome> UploadImageAsync(
        MediaTestHost host, byte[] bytes, string fileName, MediaVisibility visibility, AdminActor actor)
    {
        await using var scope = host.Factory.Services.CreateAsyncScope();
        using var content = new MemoryStream(bytes);
        return await scope.ServiceProvider.GetRequiredService<IMediaService>().UploadImageAsync(
            new UploadImageRequest(new MediaUploadFile(content, fileName), visibility, null, null, null, actor),
            Token);
    }

    private static async Task<MediaUploadOutcome> UploadVideoAsync(MediaTestHost host, byte[] video, byte[] poster, AdminActor actor)
    {
        await using var scope = host.Factory.Services.CreateAsyncScope();
        using var videoContent = new MemoryStream(video);
        using var posterContent = new MemoryStream(poster);
        return await scope.ServiceProvider.GetRequiredService<IMediaService>().UploadVideoAsync(
            new UploadVideoRequest(
                new MediaUploadFile(videoContent, "video.mp4"),
                new MediaUploadFile(posterContent, "poster.jpg"),
                null, null, null, actor),
            Token);
    }

    /// <summary>Media rows this test wrote (by its correlation id), since a moment.</summary>
    private async Task<List<AuditLog>> MediaRowsSinceAsync(MediaTestHost host, DateTime since)
    {
        var corr = _corr;
        await using var scope = host.Factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().AuditLogs.AsNoTracking()
            .Where(row => row.EntityType == "MediaAsset" && row.CorrelationId == corr && row.OccurredAtUtc >= since)
            .ToListAsync(Token);
    }

    /// <summary>Media rows naming an asset that does not exist, or one that is not Ready.</summary>
    private async Task<List<AuditLog>> OrphanRowsAsync(MediaTestHost host, DateTime since)
    {
        var corr = _corr;
        await using var scope = host.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var rows = await db.AuditLogs.AsNoTracking()
            .Where(row => row.EntityType == "MediaAsset" && row.CorrelationId == corr && row.OccurredAtUtc >= since)
            .ToListAsync(Token);
        var ready = (await db.MediaAssets.IgnoreQueryFilters().AsNoTracking()
                .Where(asset => asset.Status == MediaAssetStatus.Ready)
                .Select(asset => asset.Id)
                .ToListAsync(Token))
            .Select(id => id.ToString(CultureInfo.InvariantCulture))
            .ToHashSet(StringComparer.Ordinal);
        return rows.Where(row => !ready.Contains(row.EntityId!)).ToList();
    }

    /// <summary>Throws once from SavingChanges when a media audit row is about to be inserted.</summary>
    private sealed class FailMediaAuditSaveOnce : SaveChangesInterceptor
    {
        private int _fired;

        public int Fired => Volatile.Read(ref _fired);

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (eventData.Context is { } context
                && context.ChangeTracker.Entries<AuditLog>().Any(entry => entry.State == EntityState.Added && entry.Entity.EntityType == "MediaAsset")
                && Interlocked.CompareExchange(ref _fired, 1, 0) == 0)
            {
                throw new InvalidOperationException("Injected: the publish save failed.");
            }

            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }

    /// <summary>Throws a transient error once, either just before a commit (it never happens) or just after (ack lost).</summary>
    private sealed class CommitFaultOnce(bool afterCommit) : DbTransactionInterceptor
    {
        private int _armed;
        private int _fired;

        public int Fired => Volatile.Read(ref _fired);

        public void Arm() => Volatile.Write(ref _armed, 1);

        public override ValueTask<InterceptionResult> TransactionCommittingAsync(
            DbTransaction transaction, TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
        {
            if (!afterCommit)
            {
                FailOnce();
            }

            return base.TransactionCommittingAsync(transaction, eventData, result, cancellationToken);
        }

        public override Task TransactionCommittedAsync(
            DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
        {
            if (afterCommit)
            {
                FailOnce();
            }

            return base.TransactionCommittedAsync(transaction, eventData, cancellationToken);
        }

        private void FailOnce()
        {
            if (Volatile.Read(ref _armed) == 1 && Interlocked.CompareExchange(ref _fired, 1, 0) == 0)
            {
                throw new TimeoutException("Injected transient commit fault.");
            }
        }
    }
}
