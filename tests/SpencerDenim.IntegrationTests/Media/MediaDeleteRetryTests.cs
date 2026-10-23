using System.Data.Common;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using SpencerDenim.Application.Media.Abstractions;
using SpencerDenim.Application.Media.Contracts;
using SpencerDenim.Domain.Media;
using SpencerDenim.Infrastructure.Persistence;
using SpencerDenim.IntegrationTests.Infrastructure;

namespace SpencerDenim.IntegrationTests.Media;

/// <summary>
/// #223 edge case 2: <b>a media delete whose commit lands but whose acknowledgement is lost must still answer
/// success.</b> <c>EnableRetryOnFailure</c> re-runs the whole attempt on a transient error, including one thrown
/// after SQL Server has committed. Before #223 the retry of a video's delete re-sent the change tracker's DELETE
/// for the poster usage row the committed attempt had already removed, got 0 rows, and answered 500 with a
/// concurrency exception. The retry now removes those rows set-based and, finding the asset deleted under its
/// lock, recognises its own stamp and answers <see cref="MediaDeleteOutcome.Deleted"/>.
/// </summary>
/// <remarks>
/// The lost acknowledgement is simulated by <see cref="AckLostInterceptor"/>, which throws a
/// <see cref="TimeoutException"/> (which the SQL Server execution strategy classifies as transient) from
/// <c>TransactionCommitted</c> — after the commit has really happened.
/// </remarks>
public sealed class MediaDeleteRetryTests(SqlServerDatabaseFixture sqlServer)
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_video_delete_retried_after_a_lost_commit_acknowledgement_answers_deleted_not_500()
    {
        sqlServer.SkipIfUnavailable();
        await TestDatabaseMigrator.MigrateAsync(sqlServer.ConnectionString, Token);

        var ackLost = new AckLostInterceptor();
        await using var factory = new ApiFactory(
            sqlServer.ConnectionString,
            configureServices: services => services.AddSingleton<IInterceptor>(ackLost));

        var (videoId, videoPublicId, posterId, posterPublicId) = await ArrangeVideoAsync(factory);

        try
        {
            MediaDeleteOutcome outcome;
            await using (var scope = factory.Services.CreateAsyncScope())
            {
                var media = scope.ServiceProvider.GetRequiredService<IMediaService>();
                ackLost.Arm();
                outcome = await media.DeleteAsync(videoPublicId, deletedByUserId: null, Token);
            }

            Assert.Equal(1, ackLost.Fired);
            Assert.IsType<MediaDeleteOutcome.Deleted>(outcome);

            await using (var scope = factory.Services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                Assert.True(await db.MediaAssets.IgnoreQueryFilters().Where(asset => asset.Id == videoId).Select(asset => asset.IsDeleted).SingleAsync(Token));

                var owner = videoId.ToString(CultureInfo.InvariantCulture);
                Assert.False(await db.MediaUsages.AnyAsync(usage => usage.OwnerType == MediaUsageOwnerTypes.MediaAsset && usage.OwnerId == owner, Token));
            }

            // And the poster the video released is deletable, rather than held for ever by a usage row nobody owns.
            await using (var scope = factory.Services.CreateAsyncScope())
            {
                var media = scope.ServiceProvider.GetRequiredService<IMediaService>();
                Assert.IsType<MediaDeleteOutcome.Deleted>(await media.DeleteAsync(posterPublicId, deletedByUserId: null, Token));
            }
        }
        finally
        {
            await using var scope = factory.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Database.ExecuteSqlRawAsync(
                """
                DELETE FROM media.MediaUsages WHERE MediaAssetId IN ({0}, {1}) OR (OwnerType = 'MediaAsset' AND OwnerId = CAST({0} AS varchar(100)));
                DELETE FROM media.MediaAssets WHERE Id = {0};
                DELETE FROM media.MediaAssets WHERE Id = {1};
                """,
                [videoId, posterId],
                CancellationToken.None);
        }
    }

    /// <summary>A poster image, a video pointing at it, and the poster usage row an upload writes.</summary>
    private static async Task<(int VideoId, Guid VideoPublicId, int PosterId, Guid PosterPublicId)> ArrangeVideoAsync(ApiFactory factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var poster = Asset(MediaAssetType.Image, posterAssetId: null);
        db.MediaAssets.Add(poster);
        await db.SaveChangesAsync(Token);

        var video = Asset(MediaAssetType.Video, poster.Id);
        db.MediaAssets.Add(video);
        await db.SaveChangesAsync(Token);

        db.MediaUsages.Add(new MediaUsage
        {
            MediaAssetId = poster.Id,
            OwnerType = MediaUsageOwnerTypes.MediaAsset,
            OwnerId = video.Id.ToString(CultureInfo.InvariantCulture),
            UsageRole = MediaUsageRoles.Poster,
        });
        await db.SaveChangesAsync(Token);

        return (video.Id, video.PublicId, poster.Id, poster.PublicId);
    }

    private static MediaAsset Asset(MediaAssetType type, int? posterAssetId)
    {
        var publicId = Guid.NewGuid();
        var isVideo = type == MediaAssetType.Video;

        return new MediaAsset
        {
            PublicId = publicId,
            MediaType = type,
            Visibility = MediaVisibility.Public,
            Status = MediaAssetStatus.Ready,
            OriginalFileName = isVideo ? "t223.mp4" : "t223.png",
            StorageKey = $"public/{(isVideo ? "videos" : "images")}/{publicId:N}/original.{(isVideo ? "mp4" : "png")}",
            ContentType = isVideo ? "video/mp4" : "image/png",
            SizeBytes = 1024,
            Width = isVideo ? null : 640,
            Height = isVideo ? null : 480,
            DurationSeconds = isVideo ? 8m : null,
            PosterAssetId = posterAssetId,
            RenditionSetVersion = isVideo ? null : MediaRenditions.CurrentSetVersion,
            Checksum = System.Security.Cryptography.SHA256.HashData(publicId.ToByteArray()),
            AltText = isVideo ? null : "A #223 poster",
        };
    }

    /// <summary>Throws once, after a commit has succeeded: the acknowledgement the client never got.</summary>
    private sealed class AckLostInterceptor : DbTransactionInterceptor
    {
        private int _armed;
        private int _fired;

        public int Fired => Volatile.Read(ref _fired);

        public void Arm() => Volatile.Write(ref _armed, 1);

        public override void TransactionCommitted(DbTransaction transaction, TransactionEndEventData eventData)
        {
            FailOnce();
            base.TransactionCommitted(transaction, eventData);
        }

        public override Task TransactionCommittedAsync(
            DbTransaction transaction,
            TransactionEndEventData eventData,
            CancellationToken cancellationToken = default)
        {
            FailOnce();
            return base.TransactionCommittedAsync(transaction, eventData, cancellationToken);
        }

        private void FailOnce()
        {
            if (Volatile.Read(ref _armed) == 1 && Interlocked.CompareExchange(ref _fired, 1, 0) == 0)
            {
                throw new TimeoutException("Injected: the commit landed but its acknowledgement was lost (#223).");
            }
        }
    }
}
