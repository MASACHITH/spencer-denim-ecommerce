using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SpencerDenim.Domain.Content;
using SpencerDenim.Infrastructure.Persistence;
using SpencerDenim.IntegrationTests.Infrastructure;

namespace SpencerDenim.IntegrationTests.Content;

/// <summary>
/// The migration, against a real SQL Server: the tables, the check constraints and the indexes doc
/// 08 §3.6 and §3.7 specify, proved by making the database refuse a row that breaks each rule.
/// <para>
/// A constraint asserted from <c>sys.check_constraints</c> only proves the text was written down.
/// These insert the row the constraint exists to stop, and fail if it lands.
/// </para>
/// </summary>
public sealed class HomeContentSchemaTests(SqlServerDatabaseFixture sqlServer)
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("content", "HomeSections")]
    [InlineData("content", "HomeSectionItems")]
    [InlineData("content", "AnnouncementMessages")]
    [InlineData("marketing", "NewsletterSubscribers")]
    public async Task The_migration_created_the_table(string schema, string table)
    {
        sqlServer.SkipIfUnavailable();

        using var scope = sqlServer.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var exists = await dbContext.Database
            .SqlQuery<int>($"""
                SELECT COUNT(*) AS [Value]
                FROM sys.tables t
                JOIN sys.schemas s ON s.schema_id = t.schema_id
                WHERE s.name = {schema} AND t.name = {table}
                """)
            .SingleAsync(Token);

        Assert.Equal(1, exists);
    }

    /// <summary>
    /// Doc 08 §3.7's static-fallback rule. A hero with a video and no still is the one thing this
    /// column combination must never hold: a phone, a data-saver and reduced motion would all have
    /// nothing to show (HOME-01).
    /// </summary>
    [Fact]
    public async Task A_hero_video_without_a_still_is_refused_by_the_database()
    {
        sqlServer.SkipIfUnavailable();

        using var scope = sqlServer.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var sectionId = await dbContext.HomeSections
            .Where(section => section.SectionType == HomeSectionType.HeroVideo)
            .Select(section => section.Id)
            .SingleAsync(Token);

        var assetId = await dbContext.MediaAssets.Select(asset => asset.Id).FirstAsync(Token);

        var error = await Assert.ThrowsAsync<SqlException>(async () =>
            await dbContext.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO content.HomeSectionItems (HomeSectionId, VideoAssetId, SortOrder, IsEnabled)
                VALUES (@sectionId, @assetId, 99, 1);
                """,
                [new SqlParameter("@sectionId", sectionId), new SqlParameter("@assetId", assetId)],
                Token));

        Assert.Contains("CK_HomeSectionItems_VideoFallback", error.Message, StringComparison.Ordinal);

        // The same row with a still lands, so the constraint is the reason the first one did not.
        await dbContext.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO content.HomeSectionItems (HomeSectionId, VideoAssetId, MobileImageAssetId, SortOrder, IsEnabled)
            VALUES (@sectionId, @assetId, @assetId, 99, 0);
            """,
            [new SqlParameter("@sectionId", sectionId), new SqlParameter("@assetId", assetId)],
            Token);

        await dbContext.Database.ExecuteSqlRawAsync(
            "DELETE FROM content.HomeSectionItems WHERE SortOrder = 99;",
            Token);
    }

    [Fact]
    public async Task A_section_type_outside_the_ten_blocks_is_refused()
    {
        sqlServer.SkipIfUnavailable();

        using var scope = sqlServer.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var error = await Assert.ThrowsAsync<SqlException>(async () =>
            await dbContext.Database.ExecuteSqlRawAsync(
                "INSERT INTO content.HomeSections (SectionType, IsEnabled, SortOrder) VALUES (11, 0, 99);",
                Token));

        Assert.Contains("CK_HomeSections_SectionType", error.Message, StringComparison.Ordinal);
    }

    /// <summary>Doc 08 §1.4 (DB15): a JSON column is guarded by an <c>ISJSON</c> check.</summary>
    [Fact]
    public async Task Settings_that_are_not_json_are_refused()
    {
        sqlServer.SkipIfUnavailable();

        using var scope = sqlServer.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var error = await Assert.ThrowsAsync<SqlException>(async () =>
            await dbContext.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO content.HomeSections (SectionType, IsEnabled, SortOrder, SettingsJson)
                VALUES (10, 0, 98, N'not json');
                """,
                Token));

        Assert.Contains("CK_HomeSections_SettingsJson", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Doc 08 §1.6: <c>HomeSectionItems</c> is a pure child row and dies with its section. Nothing
    /// else in this schema cascades.
    /// </summary>
    [Fact]
    public async Task Deleting_a_section_takes_its_items_with_it()
    {
        sqlServer.SkipIfUnavailable();

        using var scope = sqlServer.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var section = new HomeSection
        {
            SectionType = HomeSectionType.SocialGallery,
            SortOrder = 97,
            IsEnabled = false,
        };
        section.Items.Add(new HomeSectionItem { SortOrder = 1, IsEnabled = true, Title = "Doomed" });

        dbContext.HomeSections.Add(section);
        await dbContext.SaveChangesAsync(Token);

        var itemId = section.Items.Single().Id;

        dbContext.HomeSections.Remove(section);
        await dbContext.SaveChangesAsync(Token);

        Assert.False(await dbContext.HomeSectionItems.AnyAsync(item => item.Id == itemId, Token));
    }

    /// <summary>
    /// Doc 08 §3.6: the unique index on <c>EmailNormalized</c>. It is what makes a second signup an
    /// update rather than a duplicate, under a race as well as under a repeat click.
    /// </summary>
    [Fact]
    public async Task One_address_cannot_be_stored_twice()
    {
        sqlServer.SkipIfUnavailable();

        using var scope = sqlServer.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        const string Normalised = "SCHEMA-TEST@EXAMPLE.COM";

        await dbContext.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO marketing.NewsletterSubscribers
                (Email, EmailNormalized, Status, Source, ConsentAtUtc, UnsubscribeToken)
            VALUES (N'schema-test@example.com', @normalised, 1, 'homepage', SYSUTCDATETIME(), NEWID());
            """,
            [new SqlParameter("@normalised", Normalised)],
            Token);

        try
        {
            var error = await Assert.ThrowsAsync<SqlException>(async () =>
                await dbContext.Database.ExecuteSqlRawAsync(
                    """
                    INSERT INTO marketing.NewsletterSubscribers
                        (Email, EmailNormalized, Status, Source, ConsentAtUtc, UnsubscribeToken)
                    VALUES (N'Schema-Test@Example.com', @normalised, 1, 'homepage', SYSUTCDATETIME(), NEWID());
                    """,
                    [new SqlParameter("@normalised", Normalised)],
                    Token));

            Assert.Contains("UX_NewsletterSubscribers_EmailNormalized", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            await dbContext.Database.ExecuteSqlRawAsync(
                "DELETE FROM marketing.NewsletterSubscribers WHERE EmailNormalized = @normalised;",
                [new SqlParameter("@normalised", Normalised)],
                Token);
        }
    }

    [Fact]
    public async Task A_status_outside_the_two_doc_08_gives_is_refused()
    {
        sqlServer.SkipIfUnavailable();

        using var scope = sqlServer.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var error = await Assert.ThrowsAsync<SqlException>(async () =>
            await dbContext.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO marketing.NewsletterSubscribers
                    (Email, EmailNormalized, Status, Source, ConsentAtUtc, UnsubscribeToken)
                VALUES (N'bad-status@example.com', N'BAD-STATUS@EXAMPLE.COM', 3, 'homepage', SYSUTCDATETIME(), NEWID());
                """,
                Token));

        Assert.Contains("CK_NewsletterSubscribers_Status", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("content", "HomeSections", "IX_HomeSections_Enabled_SortOrder")]
    [InlineData("content", "HomeSectionItems", "IX_HomeSectionItems_Section_SortOrder")]
    [InlineData("content", "AnnouncementMessages", "IX_AnnouncementMessages_Active_SortOrder")]
    [InlineData("marketing", "NewsletterSubscribers", "UX_NewsletterSubscribers_EmailNormalized")]
    [InlineData("marketing", "NewsletterSubscribers", "UX_NewsletterSubscribers_UnsubscribeToken")]
    [InlineData("marketing", "NewsletterSubscribers", "IX_NewsletterSubscribers_Status_CreatedAtUtc")]
    public async Task The_indexes_doc_08_specifies_exist(string schema, string table, string index)
    {
        sqlServer.SkipIfUnavailable();

        using var scope = sqlServer.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var exists = await dbContext.Database
            .SqlQuery<int>($"""
                SELECT COUNT(*) AS [Value]
                FROM sys.indexes i
                JOIN sys.tables t ON t.object_id = i.object_id
                JOIN sys.schemas s ON s.schema_id = t.schema_id
                WHERE s.name = {schema} AND t.name = {table} AND i.name = {index}
                """)
            .SingleAsync(Token);

        Assert.Equal(1, exists);
    }
}
