using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SpencerDenim.Domain.Catalog;
using SpencerDenim.Domain.Identity;
using SpencerDenim.Domain.Media;
using SpencerDenim.Infrastructure.Persistence;
using SpencerDenim.IntegrationTests.Infrastructure;

namespace SpencerDenim.IntegrationTests.Media;

/// <summary>
/// #223: <b>a product, category or department save that puts a picture on, and a media-library delete of that
/// picture, cannot both succeed</b> — and two deletes of one picture cannot both answer 204. The saves now lock
/// every asset row they will point at (<c>UPDLOCK, READCOMMITTEDLOCK, ROWLOCK</c>, ascending id, after the
/// department row for a department save) and re-check it inside their transaction; <c>MediaService.DeleteAsync</c>
/// takes the same lock first and re-checks <c>IsDeleted</c> under it (doc 08 §5.3).
/// </summary>
/// <remarks>
/// <b>Deterministic, not a timing race</b>, the way <c>SaleCampaignBannerRaceTests</c> is. The test holds the
/// asset row's update lock from its own connection, starts the first operation through the real API, waits until
/// SQL Server reports one request in this database waiting on a lock, starts the second, waits for two, and only
/// then releases. SQL Server grants a row's waiting update locks in arrival order, so the first one started is the
/// first one through, every time. If either path stopped taking the lock it would never be seen waiting, and the
/// wait fails with a message naming which.
/// </remarks>
public sealed class CatalogMediaLockRaceTests(SqlServerDatabaseFixture sqlServer)
{
    private const string ProductsUrl = "/api/admin/products";
    private const string CategoriesUrl = "/api/admin/catalog/categories";
    private const string DepartmentsUrl = "/api/admin/catalog/departments";

    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(30);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static readonly string[] Everything =
    [
        Permissions.Products.View,
        Permissions.Products.Create,
        Permissions.Products.Edit,
        Permissions.Products.Delete,
        Permissions.Categories.View,
        Permissions.Categories.Create,
        Permissions.Categories.Edit,
        Permissions.Categories.Delete,
        Permissions.Media.View,
        Permissions.Media.Delete,
    ];

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    // ================================================================== products

    [Fact]
    public async Task When_a_product_save_gets_the_picture_first_the_delete_is_refused_as_in_use()
    {
        await using var world = await WorldAsync();
        var picture = await world.AddImageAsync();
        var productId = await world.CreateProductAsync("t223-prod-save-first", null);

        try
        {
            var (save, delete) = await world.InterleaveAsync(
                picture.Id,
                () => world.PutProductAsync(productId, "t223-prod-save-first", picture.PublicId),
                () => world.DeleteAssetAsync(picture.PublicId));

            Assert.Equal(HttpStatusCode.NoContent, save.StatusCode);
            Assert.Equal(HttpStatusCode.Conflict, delete.StatusCode);
            Assert.Contains("Product", await OwnerTypesAsync(delete));

            Assert.False(await world.AssetDeletedAsync(picture.Id));
            Assert.Equal(1, await world.ScalarAsync($"SELECT COUNT(*) FROM catalog.ProductMedia WHERE ProductId = {productId} AND MediaAssetId = {picture.Id}"));
        }
        finally
        {
            await world.HardDeleteProductAsync(productId);
        }
    }

    [Fact]
    public async Task When_the_delete_gets_the_picture_first_a_product_save_is_refused_and_writes_nothing()
    {
        await using var world = await WorldAsync();
        var picture = await world.AddImageAsync();
        var productId = await world.CreateProductAsync("t223-prod-delete-first", null);

        try
        {
            var (delete, save) = await world.InterleaveAsync(
                picture.Id,
                () => world.DeleteAssetAsync(picture.PublicId),
                () => world.PutProductAsync(productId, "t223-prod-delete-first", picture.PublicId));

            Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, save.StatusCode);
            Assert.True((await ErrorsAsync(save)).TryGetProperty("media[0].assetPublicId", out _));

            Assert.True(await world.AssetDeletedAsync(picture.Id));
            Assert.Equal(0, await world.ScalarAsync($"SELECT COUNT(*) FROM catalog.ProductMedia WHERE MediaAssetId = {picture.Id}"));
            Assert.Equal(0, await world.ScalarAsync($"SELECT COUNT(*) FROM media.MediaUsages WHERE MediaAssetId = {picture.Id}"));
        }
        finally
        {
            await world.HardDeleteProductAsync(productId);
        }
    }

    [Fact]
    public async Task When_the_delete_gets_the_picture_first_a_new_product_is_refused_and_nothing_is_created()
    {
        await using var world = await WorldAsync();
        var picture = await world.AddImageAsync();
        const string Code = "t223-prod-create";

        var (delete, create) = await world.InterleaveAsync(
            picture.Id,
            () => world.DeleteAssetAsync(picture.PublicId),
            () => world.PostProductAsync(Code, picture.PublicId));

        var leaked = await world.ScalarAsync($"SELECT COUNT(*) FROM catalog.Products WHERE ProductCode = {Code.ToUpperInvariant()}");

        try
        {
            Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, create.StatusCode);
            Assert.True((await ErrorsAsync(create)).TryGetProperty("media[0].assetPublicId", out _));
            Assert.Equal(0, leaked);
            Assert.Equal(0, await world.ScalarAsync($"SELECT COUNT(*) FROM media.MediaUsages WHERE MediaAssetId = {picture.Id}"));
        }
        finally
        {
            if (leaked > 0)
            {
                await world.HardDeleteProductAsync(await world.ScalarAsync($"SELECT Id FROM catalog.Products WHERE ProductCode = {Code.ToUpperInvariant()}"));
            }
        }
    }

    // ================================================================== categories

    [Fact]
    public async Task When_a_category_save_gets_the_picture_first_the_delete_is_refused_as_in_use()
    {
        await using var world = await WorldAsync();
        var picture = await world.AddImageAsync();
        var categoryId = await world.CreateCategoryAsync("t223-cat-save-first");

        try
        {
            var (save, delete) = await world.InterleaveAsync(
                picture.Id,
                () => world.PutCategoryAsync(categoryId, picture.Id),
                () => world.DeleteAssetAsync(picture.PublicId));

            Assert.Equal(HttpStatusCode.NoContent, save.StatusCode);
            Assert.Equal(HttpStatusCode.Conflict, delete.StatusCode);
            Assert.Contains("Category", await OwnerTypesAsync(delete));

            Assert.False(await world.AssetDeletedAsync(picture.Id));
            Assert.Equal(1, await world.UsageRowsAsync(MediaUsageOwnerTypes.Category, categoryId));
        }
        finally
        {
            await world.HardDeleteCategoryAsync(categoryId);
        }
    }

    [Fact]
    public async Task When_the_delete_gets_the_picture_first_a_category_save_is_refused_and_writes_nothing()
    {
        await using var world = await WorldAsync();
        var picture = await world.AddImageAsync();
        var categoryId = await world.CreateCategoryAsync("t223-cat-delete-first");

        try
        {
            var (delete, save) = await world.InterleaveAsync(
                picture.Id,
                () => world.DeleteAssetAsync(picture.PublicId),
                () => world.PutCategoryAsync(categoryId, picture.Id));

            Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, save.StatusCode);
            Assert.True((await ErrorsAsync(save)).TryGetProperty("imageAssetId", out _));

            Assert.True(await world.AssetDeletedAsync(picture.Id));
            Assert.Equal(0, await world.ScalarAsync($"SELECT COUNT(*) FROM catalog.Categories WHERE Id = {categoryId} AND ImageAssetId IS NOT NULL"));
            Assert.Equal(0, await world.UsageRowsAsync(MediaUsageOwnerTypes.Category, categoryId));
        }
        finally
        {
            await world.HardDeleteCategoryAsync(categoryId);
        }
    }

    // ================================================================== departments

    [Fact]
    public async Task When_a_department_save_gets_the_picture_first_the_delete_is_refused_as_in_use()
    {
        await using var world = await WorldAsync();
        var picture = await world.AddImageAsync();
        var kids = await world.DepartmentAsync("kids");

        try
        {
            var (save, delete) = await world.InterleaveAsync(
                picture.Id,
                () => world.PutDepartmentAsync(kids, picture.Id),
                () => world.DeleteAssetAsync(picture.PublicId));

            Assert.Equal(HttpStatusCode.NoContent, save.StatusCode);
            Assert.Equal(HttpStatusCode.Conflict, delete.StatusCode);
            Assert.Contains("Department", await OwnerTypesAsync(delete));

            Assert.False(await world.AssetDeletedAsync(picture.Id));
            Assert.Equal(1, await world.ScalarAsync($"SELECT COUNT(*) FROM media.MediaUsages WHERE OwnerType = 'Department' AND OwnerId = {kids.Id.ToString(CultureInfo.InvariantCulture)} AND UsageRole = 'Hero' AND MediaAssetId = {picture.Id}"));
        }
        finally
        {
            await world.RestoreDepartmentAsync(kids);
        }
    }

    [Fact]
    public async Task When_the_delete_gets_the_picture_first_a_department_save_is_refused_and_writes_nothing()
    {
        await using var world = await WorldAsync();
        var picture = await world.AddImageAsync();
        var kids = await world.DepartmentAsync("kids");

        try
        {
            var (delete, save) = await world.InterleaveAsync(
                picture.Id,
                () => world.DeleteAssetAsync(picture.PublicId),
                () => world.PutDepartmentAsync(kids, picture.Id));

            Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, save.StatusCode);
            Assert.True((await ErrorsAsync(save)).TryGetProperty("heroImageAssetId", out _));

            Assert.True(await world.AssetDeletedAsync(picture.Id));
            Assert.Equal(0, await world.ScalarAsync($"SELECT COUNT(*) FROM catalog.Departments WHERE HeroImageAssetId = {picture.Id}"));
            Assert.Equal(0, await world.ScalarAsync($"SELECT COUNT(*) FROM media.MediaUsages WHERE MediaAssetId = {picture.Id}"));
        }
        finally
        {
            await world.RestoreDepartmentAsync(kids);
        }
    }

    /// <summary>
    /// Doc 08 §5.3's order for a department save is its own row, then the asset. The test holds the department
    /// row instead of the asset: the save must be seen waiting on it (and so not yet holding the asset), which is
    /// what keeps it from deadlocking against a mega-menu card save that locks the same two rows in that order.
    /// </summary>
    [Fact]
    public async Task A_department_save_locks_its_department_row_before_the_picture()
    {
        await using var world = await WorldAsync();
        var picture = await world.AddImageAsync();
        var kids = await world.DepartmentAsync("kids");

        try
        {
            await using var holder = new SqlConnection(sqlServer.ConnectionString);
            await holder.OpenAsync(Token);
            var transaction = (SqlTransaction)await holder.BeginTransactionAsync(Token);

            Task<HttpResponseMessage> save;
            try
            {
                await using (var hold = holder.CreateCommand())
                {
                    hold.Transaction = transaction;
                    hold.CommandText = "SELECT Id FROM catalog.Departments WITH (UPDLOCK, READCOMMITTEDLOCK, ROWLOCK) WHERE Id = @id";
                    hold.Parameters.AddWithValue("@id", kids.Id);
                    await hold.ExecuteScalarAsync(Token);
                }

                save = world.PutDepartmentAsync(kids, picture.Id);
                await world.WaitForLockWaitersAsync(1, "the department save never waited on its department row");

                // While it waits on the department row it holds no lock on the picture: a delete goes straight through.
                using var probe = new SqlConnection(sqlServer.ConnectionString);
                await probe.OpenAsync(Token);
                await using var check = probe.CreateCommand();
                check.CommandText = "SET LOCK_TIMEOUT 2000; BEGIN TRAN; SELECT Id FROM media.MediaAssets WITH (UPDLOCK, READCOMMITTEDLOCK, ROWLOCK) WHERE Id = @id; ROLLBACK;";
                check.Parameters.AddWithValue("@id", picture.Id);
                Assert.Equal(picture.Id, Convert.ToInt32(await check.ExecuteScalarAsync(Token), CultureInfo.InvariantCulture));
            }
            finally
            {
                await transaction.RollbackAsync(Token);
            }

            using var answer = await save;
            Assert.Equal(HttpStatusCode.NoContent, answer.StatusCode);
        }
        finally
        {
            await world.RestoreDepartmentAsync(kids);
        }
    }

    // ================================================================== legacy owners, no usage row

    [Fact]
    public async Task A_department_hero_saved_before_223_with_no_usage_row_still_blocks_the_delete()
    {
        await using var world = await WorldAsync();
        var picture = await world.AddImageAsync();
        var men = await world.DepartmentAsync("men");

        try
        {
            await world.ExecuteAsync($"UPDATE catalog.Departments SET HeroImageAssetId = {picture.Id} WHERE Id = {men.Id}");
            Assert.Equal(0, await world.ScalarAsync($"SELECT COUNT(*) FROM media.MediaUsages WHERE MediaAssetId = {picture.Id}"));

            using var delete = await world.DeleteAssetAsync(picture.PublicId);

            Assert.Equal(HttpStatusCode.Conflict, delete.StatusCode);
            var usage = await SingleUsageAsync(delete);
            Assert.Equal("Department", usage.GetProperty("ownerType").GetString());
            Assert.Equal("Hero", usage.GetProperty("usageRole").GetString());
            Assert.False(await world.AssetDeletedAsync(picture.Id));
        }
        finally
        {
            await world.RestoreDepartmentAsync(men);
        }
    }

    [Fact]
    public async Task A_category_picture_saved_before_223_with_no_usage_row_still_blocks_the_delete()
    {
        await using var world = await WorldAsync();
        var picture = await world.AddImageAsync();
        var categoryId = await world.CreateCategoryAsync("t223-cat-legacy");

        try
        {
            await world.ExecuteAsync($"UPDATE catalog.Categories SET BannerAssetId = {picture.Id} WHERE Id = {categoryId}");
            Assert.Equal(0, await world.ScalarAsync($"SELECT COUNT(*) FROM media.MediaUsages WHERE MediaAssetId = {picture.Id}"));

            using var delete = await world.DeleteAssetAsync(picture.PublicId);

            Assert.Equal(HttpStatusCode.Conflict, delete.StatusCode);
            var usage = await SingleUsageAsync(delete);
            Assert.Equal("Category", usage.GetProperty("ownerType").GetString());
            Assert.Equal("Banner", usage.GetProperty("usageRole").GetString());
            Assert.False(await world.AssetDeletedAsync(picture.Id));
        }
        finally
        {
            await world.HardDeleteCategoryAsync(categoryId);
        }
    }

    [Fact]
    public async Task A_removed_category_releases_its_picture()
    {
        await using var world = await WorldAsync();
        var picture = await world.AddImageAsync();
        var categoryId = await world.CreateCategoryAsync("t223-cat-removed");

        try
        {
            using (var saved = await world.PutCategoryAsync(categoryId, picture.Id))
            {
                Assert.Equal(HttpStatusCode.NoContent, saved.StatusCode);
            }

            using (var refused = await world.DeleteAssetAsync(picture.PublicId))
            {
                Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
            }

            using (var removed = await world.RemoveCategoryAsync(categoryId))
            {
                Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
            }

            Assert.Equal(0, await world.UsageRowsAsync(MediaUsageOwnerTypes.Category, categoryId));

            using var delete = await world.DeleteAssetAsync(picture.PublicId);
            Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        }
        finally
        {
            await world.HardDeleteCategoryAsync(categoryId);
        }
    }

    // ================================================================== two deletes

    /// <summary>
    /// #223 edge case 1: the second of two deletes waits for the first one's lock, then finds the asset deleted
    /// and answers 404, leaving the first delete's <c>DeletedByUserId</c> and <c>DeletedAtUtc</c> as they were.
    /// </summary>
    [Fact]
    public async Task Two_deletes_of_one_picture_answer_204_then_404_and_the_first_stamp_stands()
    {
        await using var world = await WorldAsync();
        using var second = await world.Host.SignInAsync(Everything);
        var secondUserId = await world.NewestStaffUserIdAsync();
        var picture = await world.AddImageAsync();

        var (a, b) = await world.InterleaveAsync(
            picture.Id,
            () => world.DeleteAssetAsync(picture.PublicId),
            () => second.DeleteAsync(new Uri($"/api/admin/media/{picture.PublicId}", UriKind.Relative), Token));

        Assert.Equal(HttpStatusCode.NoContent, a.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, b.StatusCode);

        var deletedBy = await world.ScalarAsync($"SELECT DeletedByUserId FROM media.MediaAssets WHERE Id = {picture.Id}");
        Assert.Equal(world.UserId, deletedBy);
        Assert.NotEqual(secondUserId, deletedBy);
    }

    // ================================================================== helpers

    private async Task<World> WorldAsync()
    {
        sqlServer.SkipIfUnavailable();
        await TestDatabaseMigrator.MigrateAsync(sqlServer.ConnectionString, Token);

        var host = MediaTestHost.Create(sqlServer.ConnectionString);
        var client = await host.SignInAsync(Everything);
        var world = new World(sqlServer.ConnectionString, host, client);
        world.UserId = await world.NewestStaffUserIdAsync();
        return world;
    }

    private static async Task<JsonElement> ErrorsAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions, Token)).GetProperty("errors");

    private static async Task<List<string?>> OwnerTypesAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions, Token))
            .GetProperty("usages")
            .EnumerateArray()
            .Select(usage => usage.GetProperty("ownerType").GetString())
            .ToList();

    private static async Task<JsonElement> SingleUsageAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions, Token))
            .GetProperty("usages")
            .EnumerateArray()
            .Single();

    internal sealed record Picture(int Id, Guid PublicId);

    internal sealed record DepartmentSnapshot(int Id, string? Description, int? HeroImageAssetId, string? SeoTitle, string? SeoDescription, List<(int AssetId, string Role)> Usages);

    /// <summary>One API host, one signed-in client, and the arrange/clean-up steps the tests share.</summary>
    internal sealed class World(string connectionString, MediaTestHost host, HttpClient client) : IAsyncDisposable
    {
        private readonly List<Guid> _assets = [];

        public MediaTestHost Host => host;

        public HttpClient Client => client;

        public int UserId { get; set; }

        /// <summary>A ready public image, inserted directly: the saves read rows, never files.</summary>
        public async Task<Picture> AddImageAsync()
        {
            await using var scope = host.Factory.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var publicId = Guid.NewGuid();

            var asset = new MediaAsset
            {
                PublicId = publicId,
                MediaType = MediaAssetType.Image,
                Visibility = MediaVisibility.Public,
                Status = MediaAssetStatus.Ready,
                OriginalFileName = "t223.png",
                StorageKey = $"public/images/{publicId:N}/original.png",
                ContentType = "image/png",
                SizeBytes = 1024,
                Width = 640,
                Height = 480,
                RenditionSetVersion = MediaRenditions.CurrentSetVersion,
                Checksum = System.Security.Cryptography.SHA256.HashData(publicId.ToByteArray()),
                AltText = "A #223 picture",
            };

            db.MediaAssets.Add(asset);
            await db.SaveChangesAsync(Token);
            _assets.Add(publicId);

            return new Picture(asset.Id, publicId);
        }

        public Task<HttpResponseMessage> DeleteAssetAsync(Guid publicId) =>
            client.DeleteAsync(new Uri($"/api/admin/media/{publicId}", UriKind.Relative), Token);

        // ---- products

        public async Task<int> CreateProductAsync(string code, Guid? picture)
        {
            using var response = await PostProductAsync(code, picture);
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            return (await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions, Token)).GetProperty("id").GetInt32();
        }

        public async Task<HttpResponseMessage> PostProductAsync(string code, Guid? picture) =>
            await SendAsync(HttpMethod.Post, ProductsUrl, await ProductBodyAsync(code, picture, null));

        public async Task<HttpResponseMessage> PutProductAsync(int productId, string code, Guid? picture) =>
            await SendAsync(
                HttpMethod.Put,
                string.Create(CultureInfo.InvariantCulture, $"{ProductsUrl}/{productId}"),
                await ProductBodyAsync(code, picture, await RowVersionAsync($"SELECT RowVersion FROM catalog.Products WHERE Id = {productId}")));

        public async Task<Dictionary<string, object?>> ProductBodyAsync(string code, Guid? picture, string? rowVersion)
        {
            var (departmentId, categoryId, materialId) = await ProductReferencesAsync();

            return new Dictionary<string, object?>
            {
                ["productCode"] = code.ToUpperInvariant(),
                ["name"] = code,
                ["slug"] = code.ToLowerInvariant(),
                ["departmentId"] = departmentId,
                ["primaryCategoryId"] = categoryId,
                ["materialId"] = materialId,
                ["basePrice"] = 5490m,
                ["colours"] = Array.Empty<object>(),
                ["variants"] = Array.Empty<object>(),
                ["relatedProducts"] = Array.Empty<object>(),
                ["media"] = picture is { } id
                    ? new object[]
                    {
                        new Dictionary<string, object?>
                        {
                            ["assetPublicId"] = id,
                            ["colourName"] = null,
                            ["mediaRole"] = "Gallery",
                            ["sortOrder"] = 0,
                            ["altTextOverride"] = "Front view",
                        },
                    }
                    : Array.Empty<object>(),
                ["rowVersion"] = rowVersion,
            };
        }

        private async Task<(int DepartmentId, int CategoryId, int MaterialId)> ProductReferencesAsync()
        {
            await using var scope = host.Factory.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var department = await db.Departments.Where(row => row.Slug == "women").Select(row => row.Id).SingleAsync(Token);
            var category = await db.Categories.Where(row => row.DepartmentId == department).OrderBy(row => row.Id).Select(row => row.Id).FirstAsync(Token);
            var material = await db.AttributeOptions.Where(row => row.AttributeType == AttributeOptionType.Material).OrderBy(row => row.Id).Select(row => row.Id).FirstAsync(Token);

            return (department, category, material);
        }

        public Task HardDeleteProductAsync(int productId) =>
            ExecuteRawAsync(
                """
                DELETE FROM ops.AuditLogs WHERE EntityType = 'Product' AND EntityId = CAST({0} AS varchar(100));
                DELETE FROM media.MediaUsages WHERE OwnerType = 'Product' AND OwnerId = CAST({0} AS varchar(100));
                DELETE FROM inventory.StockLevels WHERE VariantId IN (SELECT Id FROM catalog.ProductVariants WHERE ProductId = {0});
                DELETE FROM catalog.ProductVariants WHERE ProductId = {0};
                DELETE FROM catalog.RelatedProducts WHERE ProductId = {0} OR RelatedProductId = {0};
                DELETE FROM catalog.ProductMedia WHERE ProductId = {0};
                DELETE FROM catalog.ProductColours WHERE ProductId = {0};
                DELETE FROM catalog.ProductCategories WHERE ProductId = {0};
                DELETE FROM catalog.ProductStats WHERE ProductId = {0};
                DELETE FROM catalog.ProductSearchIndex WHERE ProductId = {0};
                DELETE FROM catalog.Products WHERE Id = {0};
                """,
                productId);

        // ---- categories

        public async Task<int> CreateCategoryAsync(string slug)
        {
            var departmentId = await ScalarAsync($"SELECT Id FROM catalog.Departments WHERE Slug = 'women'");
            using var response = await SendAsync(HttpMethod.Post, CategoriesUrl, new Dictionary<string, object?>
            {
                ["departmentId"] = departmentId,
                ["parentCategoryId"] = null,
                ["name"] = slug,
                ["slug"] = slug,
                ["showInMegaMenu"] = false,
            });

            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            return (await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions, Token)).GetProperty("id").GetInt32();
        }

        public async Task<HttpResponseMessage> PutCategoryAsync(int categoryId, int? imageAssetId)
        {
            await using var scope = host.Factory.Services.CreateAsyncScope();
            var category = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Categories.AsNoTracking()
                .SingleAsync(row => row.Id == categoryId, Token);

            return await SendAsync(HttpMethod.Put, string.Create(CultureInfo.InvariantCulture, $"{CategoriesUrl}/{categoryId}"), new Dictionary<string, object?>
            {
                ["parentCategoryId"] = category.ParentCategoryId,
                ["name"] = category.Name,
                ["slug"] = category.Slug,
                ["description"] = category.Description,
                ["imageAssetId"] = imageAssetId,
                ["bannerAssetId"] = category.BannerAssetId,
                ["bannerLinkUrl"] = category.BannerLinkUrl,
                ["ageGroupId"] = category.AgeGroupId,
                ["sizeGuideId"] = category.SizeGuideId,
                ["showInMegaMenu"] = category.ShowInMegaMenu,
                ["megaMenuColumn"] = category.MegaMenuColumn,
                ["seoTitle"] = category.SeoTitle,
                ["seoDescription"] = category.SeoDescription,
                ["rowVersion"] = Convert.ToBase64String(category.RowVersion!),
            });
        }

        public async Task<HttpResponseMessage> RemoveCategoryAsync(int categoryId)
        {
            using var request = new HttpRequestMessage(HttpMethod.Delete, new Uri(string.Create(CultureInfo.InvariantCulture, $"{CategoriesUrl}/{categoryId}"), UriKind.Relative));
            request.Headers.TryAddWithoutValidation("If-Match", await RowVersionAsync($"SELECT RowVersion FROM catalog.Categories WHERE Id = {categoryId}"));
            return await client.SendAsync(request, Token);
        }

        public Task HardDeleteCategoryAsync(int categoryId) =>
            ExecuteRawAsync(
                """
                DELETE FROM ops.AuditLogs WHERE EntityType = 'Category' AND EntityId = CAST({0} AS varchar(100));
                DELETE FROM media.MediaUsages WHERE OwnerType = 'Category' AND OwnerId = CAST({0} AS varchar(100));
                DELETE FROM catalog.Categories WHERE Id = {0};
                """,
                categoryId);

        // ---- departments

        public async Task<DepartmentSnapshot> DepartmentAsync(string slug)
        {
            await using var scope = host.Factory.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var row = await db.Departments.AsNoTracking().SingleAsync(department => department.Slug == slug, Token);
            var owner = row.Id.ToString(CultureInfo.InvariantCulture);
            var usages = await db.MediaUsages.AsNoTracking()
                .Where(usage => usage.OwnerType == MediaUsageOwnerTypes.Department && usage.OwnerId == owner)
                .Select(usage => new { usage.MediaAssetId, usage.UsageRole })
                .ToListAsync(Token);

            return new DepartmentSnapshot(row.Id, row.Description, row.HeroImageAssetId, row.SeoTitle, row.SeoDescription, usages.Select(usage => (usage.MediaAssetId, usage.UsageRole)).ToList());
        }

        public async Task<HttpResponseMessage> PutDepartmentAsync(DepartmentSnapshot department, int? heroImageAssetId) =>
            await SendAsync(HttpMethod.Put, string.Create(CultureInfo.InvariantCulture, $"{DepartmentsUrl}/{department.Id}"), new Dictionary<string, object?>
            {
                ["description"] = department.Description,
                ["heroImageAssetId"] = heroImageAssetId,
                ["seoTitle"] = department.SeoTitle,
                ["seoDescription"] = department.SeoDescription,
                ["rowVersion"] = await RowVersionAsync($"SELECT RowVersion FROM catalog.Departments WHERE Id = {department.Id}"),
            });

        /// <summary>Puts the department's hero and its usage rows back as they were before the test.</summary>
        public async Task RestoreDepartmentAsync(DepartmentSnapshot department)
        {
            await ExecuteRawAsync(
                "DELETE FROM media.MediaUsages WHERE OwnerType = 'Department' AND OwnerId = CAST({0} AS varchar(100));",
                department.Id);

            await using (var scope = host.Factory.Services.CreateAsyncScope())
            {
                await scope.ServiceProvider.GetRequiredService<AppDbContext>().Departments
                    .Where(row => row.Id == department.Id)
                    .ExecuteUpdateAsync(set => set.SetProperty(row => row.HeroImageAssetId, department.HeroImageAssetId), CancellationToken.None);
            }

            foreach (var (assetId, role) in department.Usages)
            {
                await ExecuteRawAsync(
                    "INSERT INTO media.MediaUsages (MediaAssetId, OwnerType, OwnerId, UsageRole, CreatedAtUtc) VALUES ({0}, 'Department', CAST({1} AS varchar(100)), {2}, SYSUTCDATETIME());",
                    assetId,
                    department.Id,
                    role);
            }
        }

        // ---- the interleave

        /// <summary>
        /// Holds the asset row's update lock, starts <paramref name="first"/>, waits until it is blocked, starts
        /// <paramref name="second"/>, waits until both are blocked, then releases and returns both answers.
        /// </summary>
        public async Task<(HttpResponseMessage First, HttpResponseMessage Second)> InterleaveAsync(
            int assetId,
            Func<Task<HttpResponseMessage>> first,
            Func<Task<HttpResponseMessage>> second)
        {
            await using var holder = new SqlConnection(connectionString);
            await holder.OpenAsync(Token);

            var transaction = (SqlTransaction)await holder.BeginTransactionAsync(Token);

            await using (var hold = holder.CreateCommand())
            {
                hold.Transaction = transaction;
                hold.CommandText = "SELECT Id FROM media.MediaAssets WITH (UPDLOCK, READCOMMITTEDLOCK, ROWLOCK) WHERE Id = @id";
                hold.Parameters.AddWithValue("@id", assetId);
                Assert.Equal(assetId, Convert.ToInt32(await hold.ExecuteScalarAsync(Token), CultureInfo.InvariantCulture));
            }

            Task<HttpResponseMessage> firstTask;
            Task<HttpResponseMessage> secondTask;

            try
            {
                firstTask = first();
                await WaitForLockWaitersAsync(1, "the first operation never waited on the asset row's lock");

                secondTask = second();
                await WaitForLockWaitersAsync(2, "the second operation never waited on the asset row's lock");
            }
            finally
            {
                await transaction.RollbackAsync(Token);
            }

            return (await firstTask, await secondTask);
        }

        /// <summary>Polls until this database has at least <paramref name="count"/> requests waiting on a lock.</summary>
        public async Task WaitForLockWaitersAsync(int count, string failure)
        {
            await using var probe = new SqlConnection(connectionString);
            await probe.OpenAsync(Token);

            var deadline = DateTime.UtcNow + WaitLimit;

            while (DateTime.UtcNow < deadline)
            {
                await using var command = probe.CreateCommand();
                command.CommandText =
                    "SELECT COUNT(*) FROM sys.dm_exec_requests WHERE database_id = DB_ID() AND wait_type LIKE 'LCK[_]M[_]%'";

                if ((int)(await command.ExecuteScalarAsync(Token))! >= count)
                {
                    return;
                }

                await Task.Delay(50, Token);
            }

            Assert.Fail(failure);
        }

        // ---- reads

        public async Task<bool> AssetDeletedAsync(int assetId) =>
            await ScalarAsync($"SELECT CAST(IsDeleted AS int) FROM media.MediaAssets WHERE Id = {assetId}") == 1;

        public Task<int> UsageRowsAsync(string ownerType, int ownerId) =>
            ScalarAsync($"SELECT COUNT(*) FROM media.MediaUsages WHERE OwnerType = {ownerType} AND OwnerId = {ownerId.ToString(CultureInfo.InvariantCulture)}");

        public Task<int> NewestStaffUserIdAsync() =>
            ScalarAsync($"SELECT MAX(Id) FROM [identity].Users WHERE Email LIKE 'media-%'");

        public async Task<int> ScalarAsync(FormattableString sql)
        {
            await using var scope = host.Factory.Services.CreateAsyncScope();
            var values = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database
                .SqlQuery<int?>(FormattableStringFactory(sql))
                .ToListAsync(Token);

            return values.SingleOrDefault() ?? 0;
        }

        public async Task ExecuteAsync(FormattableString sql)
        {
            await using var scope = host.Factory.Services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.ExecuteSqlAsync(sql, Token);
        }

        private async Task ExecuteRawAsync(string sql, params object[] parameters)
        {
            await using var scope = host.Factory.Services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.ExecuteSqlRawAsync(sql, parameters, Token);
        }

        private async Task<string> RowVersionAsync(FormattableString sql)
        {
            await using var scope = host.Factory.Services.CreateAsyncScope();
            var version = (await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database
                .SqlQuery<byte[]>(FormattableStringFactory(sql))
                .ToListAsync(Token)).Single();

            return Convert.ToBase64String(version);
        }

        private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, object body)
        {
            using var request = new HttpRequestMessage(method, new Uri(url, UriKind.Relative))
            {
                Content = JsonContent.Create(body, options: JsonOptions),
            };

            return await client.SendAsync(request, Token);
        }

        /// <summary>Wraps a scalar query as the single-column <c>Value</c> shape <c>SqlQuery</c> composes over.</summary>
        private static FormattableString FormattableStringFactory(FormattableString sql) =>
            System.Runtime.CompilerServices.FormattableStringFactory.Create(
                "SELECT (" + sql.Format + ") AS Value",
                sql.GetArguments());

        public async ValueTask DisposeAsync()
        {
            try
            {
                await using var scope = host.Factory.Services.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

                foreach (var publicId in _assets)
                {
                    await db.Database.ExecuteSqlRawAsync(
                        """
                        DELETE FROM media.MediaUsages WHERE MediaAssetId IN (SELECT Id FROM media.MediaAssets WHERE PublicId = {0});
                        DELETE FROM media.MediaAssets WHERE PublicId = {0} AND Id NOT IN (SELECT HeroImageAssetId FROM catalog.Departments WHERE HeroImageAssetId IS NOT NULL);
                        """,
                        [publicId],
                        CancellationToken.None);
                }
            }
            finally
            {
                client.Dispose();
                await host.DisposeAsync();
            }
        }
    }
}
