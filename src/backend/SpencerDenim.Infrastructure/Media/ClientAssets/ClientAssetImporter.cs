using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SpencerDenim.Application.Media.Abstractions;
using SpencerDenim.Application.Media.ClientAssets;
using SpencerDenim.Application.Media.Contracts;
using SpencerDenim.Domain.Catalog;
using SpencerDenim.Domain.Content;
using SpencerDenim.Domain.Media;
using SpencerDenim.Infrastructure.Persistence;

namespace SpencerDenim.Infrastructure.Media.ClientAssets;

/// <summary>
/// Reads the client's asset drop and wires it into the catalogue and the homepage (W1-13).
/// <para>
/// <b>Why this is an import and not a seeder.</b> The demo catalogue's seeder composes
/// <c>media.MediaAssets</c> rows itself, because it is describing a shape a development database starts
/// in and the files behind those rows are drawn, not uploaded. These files are the client's own
/// photographs: they have to be signature-checked, stripped of their EXIF, rendered to WebP and
/// recorded in <c>media.MediaUsages</c> exactly as an admin upload is, and they have to be importable
/// against a production database that has never been seeded at all. So the bytes go through
/// <see cref="IMediaService"/> — the one pipeline — and this class only writes the reference rows that
/// say which photograph belongs to which garment.
/// </para>
/// <para>
/// <b>Re-running it is a no-op.</b> The pipeline deduplicates by checksum, so the second run gets the
/// same asset back and writes no file. Every reference row here is written only when it is not already
/// there, and a picture somebody has since changed in the admin is left exactly as they left it.
/// </para>
/// </summary>
public sealed partial class ClientAssetImporter(
    AppDbContext dbContext,
    IMediaService media,
    IFileStorage storage,
    ILogger<ClientAssetImporter> logger) : IClientAssetImporter
{
    /// <summary>
    /// Where the client's own photographs sit in a product's gallery.
    /// <para>
    /// Negative, and deliberately so. The demo seeder numbers its placeholder frames from 1, and those
    /// rows are not this importer's to renumber — a real photograph has to come <i>before</i> a frame
    /// that says no photograph was supplied, and the only way to do that without rewriting rows somebody
    /// else owns is to sort ahead of them.
    /// </para>
    /// </summary>
    private const short CardSortOrder = -2;

    private const short HoverSortOrder = -1;

    /// <summary>
    /// The social gallery's six tiles and the dummy destinations they carry until the client sends the
    /// real account URLs.
    /// <para>
    /// <b>The host is <c>example.invalid</c> on purpose.</b> RFC 2606 reserves the <c>.invalid</c>
    /// top-level domain so that it can never be registered and never resolves, and the label says what
    /// it is in the accessible name a screen reader reads out. A plausible-looking
    /// <c>instagram.com/colombowardrobe</c> would test exactly as well and could ship by accident; this
    /// cannot — it is wrong in the data, wrong on the screen and wrong in the DNS.
    /// </para>
    /// </summary>
    private static readonly (string Network, string Url)[] SocialPlaceholders =
    [
        ("Instagram", "https://example.invalid/PLACEHOLDER/instagram"),
        ("Facebook", "https://example.invalid/PLACEHOLDER/facebook"),
        ("TikTok", "https://example.invalid/PLACEHOLDER/tiktok"),
        ("Pinterest", "https://example.invalid/PLACEHOLDER/pinterest"),
        ("YouTube", "https://example.invalid/PLACEHOLDER/youtube"),
        ("WhatsApp", "https://example.invalid/PLACEHOLDER/whatsapp"),
    ];

    /// <summary>
    /// The four trending tiles (HOME-04), each opening a listing the catalogue can really serve.
    /// <para>
    /// The copy follows the photographs the client delivered, in their numbered order: a leather bag, a
    /// stack of linen shirts, a dress with raffia slides, and children's knitwear. A tile whose picture
    /// and link disagree is worse than no tile.
    /// </para>
    /// </summary>
    private static readonly (string Title, string Url)[] TrendingTiles =
    [
        ("Bags and accessories", "/women/accessories"),
        ("Linen shirts", "/men/shirts"),
        ("Dresses and sandals", "/women/dresses"),
        ("Kids' new in", "/kids?sort=newest"),
    ];

    /// <summary>
    /// The reference rows that already exist, read once at the start of a run.
    /// <para>
    /// Held rather than queried per file because "is this photograph already on this product?" is asked
    /// seventy-odd times, and because the answer has to count rows this run has added but not yet saved.
    /// The importer is a one-shot object resolved per command, so this state lives exactly as long as the
    /// run does.
    /// </para>
    /// </summary>
    private readonly HashSet<(int ProductId, int AssetId)> _productMedia = [];

    private readonly HashSet<(int AssetId, string OwnerType, string OwnerId, string Role)> _usages = [];

    public async Task<ClientAssetImportReport> ImportAsync(
        ClientAssetImportRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var run = new Run(request.DryRun);

        if (!storage.IsConfigured)
        {
            run.Fail(
                "Media storage is not configured on this deployment (Media__PublicRoot is empty), so "
                + "nothing can be stored. Set it and run the import again.");
            return run.ToReport();
        }

        if (!Directory.Exists(request.SourceDirectory))
        {
            run.Fail($"The asset drop '{request.SourceDirectory}' does not exist.");
            return run.ToReport();
        }

        await LoadExistingReferencesAsync(cancellationToken);

        await ImportProductsAsync(request.SourceDirectory, run, cancellationToken);

        // A photograph nobody can place stops the whole run, before anything else is stored. Half an
        // import is the worst outcome available: the failure is reported, somebody fixes the name, and
        // the next run is still the first run.
        if (!run.Succeeded)
        {
            LogFinished(logger, run.Entries.Count, run.Failures.Count, request.DryRun);
            return run.ToReport();
        }

        await ImportDepartmentsAsync(request.SourceDirectory, run, cancellationToken);
        await ImportHomeAsync(request.SourceDirectory, run, cancellationToken);
        await ImportSocialAsync(request.SourceDirectory, run, cancellationToken);
        await ImportLogoAsync(request.SourceDirectory, run, cancellationToken);

        if (!request.DryRun)
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        LogFinished(logger, run.Entries.Count, run.Failures.Count, request.DryRun);
        return run.ToReport();
    }

    // ------------------------------------------------------------------ products

    private async Task ImportProductsAsync(string root, Run run, CancellationToken cancellationToken)
    {
        var folder = Path.Combine(root, "products");
        if (!Directory.Exists(folder))
        {
            run.Fail($"'{folder}' is missing, so no product photograph was imported.");
            return;
        }

        var products = await dbContext.Products
            .Select(product => new { product.Id, product.Slug, product.Name })
            .ToListAsync(cancellationToken);

        if (products.Count == 0)
        {
            run.Fail(
                "The catalogue has no products, so no photograph can be attached to one. Seed or create "
                + "the catalogue first.");
            return;
        }

        var slugs = products.Select(product => product.Slug).ToHashSet(StringComparer.Ordinal);
        var byslug = products.ToDictionary(product => product.Slug, StringComparer.Ordinal);

        // Resolve every name before a single byte is stored, so a batch with an unplaceable photograph
        // in it fails before it has half-imported the rest.
        var resolved = new Dictionary<string, List<ProductAssetName>>(StringComparer.Ordinal);

        foreach (var path in Files(folder))
        {
            var fileName = Path.GetFileName(path);

            switch (ClientAssetNaming.ParseProductFile(fileName, slugs))
            {
                case ProductAssetNameResult.Resolved resolvedName:
                    var name = resolvedName.Name;
                    if (!resolved.TryGetValue(name.Slug, out var list))
                    {
                        list = [];
                        resolved[name.Slug] = list;
                    }

                    list.Add(name);
                    break;

                case ProductAssetNameResult.Unmappable unmappable:
                    run.Add(new ClientAssetEntry(
                        ClientAssetFolder.Products,
                        fileName,
                        ClientAssetOutcome.Unmappable,
                        unmappable.Reason));
                    run.Fail($"products/{fileName}: {unmappable.Reason}");
                    break;

                default:
                    throw new InvalidOperationException("Unknown product asset name result.");
            }
        }

        if (!run.Succeeded)
        {
            return;
        }

        foreach (var (slug, files) in resolved.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            var product = byslug[slug];

            var entity = run.DryRun
                ? null
                : await dbContext.Products.FirstAsync(
                    candidate => candidate.Id == product.Id,
                    cancellationToken);

            int? cardAssetId = null;
            int? hoverAssetId = null;

            foreach (var file in files.OrderBy(file => file.Index))
            {
                var alt = ClientAssetNaming.ProductAltText(product.Name, file.Index);
                var stored = await StoreImageAsync(
                    Path.Combine(folder, file.FileName),
                    alt,
                    product.Name,
                    run,
                    cancellationToken);

                if (stored.Refusal is { } refusal)
                {
                    run.Add(new ClientAssetEntry(
                        ClientAssetFolder.Products,
                        file.FileName,
                        ClientAssetOutcome.Refused,
                        refusal,
                        file.RepairedFrom));
                    run.Fail($"products/{file.FileName}: {refusal}");
                    continue;
                }

                var role = file.Index switch
                {
                    1 => "the card image",
                    2 => "the hover image",
                    _ => string.Create(CultureInfo.InvariantCulture, $"gallery frame {file.Index}"),
                };

                var wrote = false;

                if (entity is not null && stored.AssetId is { } assetId)
                {
                    switch (file.Index)
                    {
                        case 1:
                            cardAssetId = assetId;
                            wrote = EnsureProductMedia(product.Id, assetId, ProductMediaRole.Gallery, CardSortOrder);
                            break;
                        case 2:
                            hoverAssetId = assetId;
                            wrote = EnsureProductMedia(product.Id, assetId, ProductMediaRole.Hover, HoverSortOrder);
                            break;
                        default:
                            wrote = EnsureProductMedia(
                                product.Id,
                                assetId,
                                ProductMediaRole.Gallery,
                                (short)(file.Index - 2));
                            break;
                    }

                    EnsureUsage(assetId, MediaUsageOwnerTypes.Product, product.Id, file.Index switch
                    {
                        1 => MediaUsageRoles.Card,
                        2 => MediaUsageRoles.Hover,
                        _ => MediaUsageRoles.Gallery,
                    });
                }

                run.Add(new ClientAssetEntry(
                    ClientAssetFolder.Products,
                    file.FileName,
                    stored.WasExisting && !wrote ? ClientAssetOutcome.AlreadyPresent : ClientAssetOutcome.Imported,
                    $"{product.Slug} — {role}, alt text \"{alt}\"",
                    file.RepairedFrom));
            }

            if (entity is null)
            {
                continue;
            }

            if (cardAssetId is { } card)
            {
                entity.CardImageAssetId = card;
            }

            // A product with one photograph gets a card and **no hover**. Leaving the seeded placeholder
            // in the hover slot would mean a real garment on the card flipping to a grey frame that says
            // no photograph was supplied — which is worse than a card that simply does not flip.
            if (cardAssetId is not null)
            {
                if (hoverAssetId is { } hover)
                {
                    entity.HoverImageAssetId = hover;
                }
                else if (entity.HoverImageAssetId is not null)
                {
                    entity.HoverImageAssetId = null;
                    run.Note(
                        $"{product.Slug}: one photograph was supplied, so the card is the client's and the "
                        + "hover image was cleared rather than left pointing at a placeholder.");
                }
            }
        }
    }

    // --------------------------------------------------------------- departments

    private async Task ImportDepartmentsAsync(string root, Run run, CancellationToken cancellationToken)
    {
        var folder = Path.Combine(root, "departments");
        if (!Directory.Exists(folder))
        {
            run.Fail($"'{folder}' is missing, so no department picture was imported.");
            return;
        }

        var departments = await dbContext.Departments.ToListAsync(cancellationToken);
        var tiles = await TilesOfAsync(HomeSectionType.ShopByDepartment, cancellationToken);

        foreach (var path in Files(folder))
        {
            var fileName = Path.GetFileName(path);
            var (slug, _) = ClientAssetNaming.StripMediaExtensions(fileName);

            var department = departments.FirstOrDefault(
                candidate => string.Equals(candidate.Slug, slug, StringComparison.OrdinalIgnoreCase));

            if (department is null)
            {
                run.Add(new ClientAssetEntry(
                    ClientAssetFolder.Departments,
                    fileName,
                    ClientAssetOutcome.NotInContract,
                    $"'{slug}' is not a department slug. The contract is women.jpg, men.jpg and kids.jpg."));
                continue;
            }

            var alt = $"{department.Name} at Colombo Wardrobe";
            var stored = await StoreImageAsync(path, alt, department.Name, run, cancellationToken);

            if (stored.Refusal is { } refusal)
            {
                run.Add(new ClientAssetEntry(
                    ClientAssetFolder.Departments, fileName, ClientAssetOutcome.Refused, refusal));
                run.Fail($"departments/{fileName}: {refusal}");
                continue;
            }

            var wrote = false;

            if (stored.AssetId is { } assetId)
            {
                if (department.HeroImageAssetId is null)
                {
                    department.HeroImageAssetId = assetId;
                    wrote = true;
                }

                EnsureUsage(assetId, MediaUsageOwnerTypes.Department, department.Id, MediaUsageRoles.Hero);

                // HOME-02's tile is a different row from the department itself, and it is the one the
                // homepage draws. Both get the picture; neither is overwritten if it already has one.
                var tile = tiles.FirstOrDefault(item => item.DepartmentId == department.Id);
                if (tile is not null && tile.ImageAssetId is null)
                {
                    tile.ImageAssetId = assetId;
                    wrote = true;
                }
            }

            run.Add(new ClientAssetEntry(
                ClientAssetFolder.Departments,
                fileName,
                stored.WasExisting && !wrote ? ClientAssetOutcome.AlreadyPresent : ClientAssetOutcome.Imported,
                $"the {department.Name} department and its homepage tile"));
        }
    }

    // ---------------------------------------------------------------- home page

    private async Task ImportHomeAsync(string root, Run run, CancellationToken cancellationToken)
    {
        var folder = Path.Combine(root, "home");
        if (!Directory.Exists(folder))
        {
            run.Fail($"'{folder}' is missing, so the homepage sections were left switched off.");
            return;
        }

        var files = Files(folder).ToList();

        await ImportHeroAsync(folder, files, run, cancellationToken);
        await ImportBannerAsync(
            HomeSectionType.CampaignBanner,
            ClientAssetNaming.CampaignFile,
            "The new season",
            "Linen, denim and everything in between.",
            "/new-arrivals",
            "Shop new arrivals",
            files,
            run,
            cancellationToken);
        await ImportBannerAsync(
            HomeSectionType.ActivewearFeature,
            ClientAssetNaming.ActivewearFile,
            "Activewear",
            "Pieces that move with you.",
            "/women/activewear",
            "Shop activewear",
            files,
            run,
            cancellationToken);
        await ImportTrendingAsync(folder, files, run, cancellationToken);

        foreach (var path in files.Where(path => !run.Touched(Path.GetFileName(path))))
        {
            run.Add(new ClientAssetEntry(
                ClientAssetFolder.Home,
                Path.GetFileName(path),
                ClientAssetOutcome.NotInContract,
                "not one of the names the homepage contract uses "
                + "(hero.mp4, hero-poster.jpg, campaign.jpg, activewear.jpg, trending-1..N.jpg)"));
        }
    }

    private async Task ImportHeroAsync(
        string folder,
        IReadOnlyList<string> files,
        Run run,
        CancellationToken cancellationToken)
    {
        var videoPath = files.FirstOrDefault(
            path => string.Equals(Path.GetFileName(path), ClientAssetNaming.HeroVideoFile, StringComparison.OrdinalIgnoreCase));

        var posterPath = files.FirstOrDefault(
            path => ClientAssetNaming.IsNamed(Path.GetFileName(path), ClientAssetNaming.HeroPosterFile));

        if (videoPath is null || posterPath is null)
        {
            run.Note(
                $"The hero stayed switched off: '{ClientAssetNaming.HeroVideoFile}' and "
                + $"'{ClientAssetNaming.HeroPosterFile}' are both required and one is missing.");
            return;
        }

        var section = await SectionAsync(HomeSectionType.HeroVideo, cancellationToken);
        if (section is null)
        {
            run.Fail("There is no HeroVideo section to attach the hero to. Run the content seeder first.");
            return;
        }

        run.Touch(Path.GetFileName(videoPath));
        run.Touch(Path.GetFileName(posterPath));

        const string alt = "A linen outfit from the new season, photographed in Colombo";

        if (run.DryRun)
        {
            run.Add(new ClientAssetEntry(
                ClientAssetFolder.Home,
                Path.GetFileName(videoPath),
                ClientAssetOutcome.Imported,
                "the hero video (HOME-01), with its poster"));
            return;
        }

        await using var video = File.OpenRead(videoPath);
        await using var poster = File.OpenRead(posterPath);

        var outcome = await media.UploadVideoAsync(
            new UploadVideoRequest(
                new MediaUploadFile(video, Path.GetFileName(videoPath)),
                new MediaUploadFile(poster, Path.GetFileName(posterPath)),
                alt,
                "Homepage hero",
                UploadedByUserId: null),
            cancellationToken);

        if (outcome is MediaUploadOutcome.Rejected rejected)
        {
            // Never worked around. The pipeline takes MP4/H.264 only and converts nothing (doc 03 §3b),
            // so a refusal here means the file has to be re-exported — and the reason it gives is the
            // real one, repeated verbatim.
            var reason = $"{rejected.Reason}: {rejected.Message}";
            run.Add(new ClientAssetEntry(
                ClientAssetFolder.Home, Path.GetFileName(videoPath), ClientAssetOutcome.Refused, reason));
            run.Fail($"home/{Path.GetFileName(videoPath)}: {reason}");
            return;
        }

        var dto = outcome switch
        {
            MediaUploadOutcome.Created created => created.Asset,
            MediaUploadOutcome.Duplicate duplicate => duplicate.Asset,
            _ => throw new InvalidOperationException("Unknown upload outcome."),
        };

        var videoAssetId = await AssetIdAsync(dto.PublicId, cancellationToken);
        var posterAssetId = dto.PosterPublicId is { } posterPublicId
            ? await AssetIdAsync(posterPublicId, cancellationToken)
            : throw new InvalidOperationException("A stored video has no poster, which the schema forbids.");

        var item = section.Items.FirstOrDefault(candidate => candidate.SortOrder == 1)
            ?? AddItem(section, sortOrder: 1);

        var wrote = false;

        if (item.VideoAssetId is null)
        {
            item.VideoAssetId = videoAssetId;
            wrote = true;
        }

        // Doc 08 §3.7's CK: a video item must carry a still. The poster is that still — it is the frame
        // the phone, the data saver and prefers-reduced-motion all get, which is exactly what it is for.
        if (item.ImageAssetId is null)
        {
            item.ImageAssetId = posterAssetId;
            wrote = true;
        }

        item.Title ??= "Dressed for Colombo";
        item.Subtitle ??= "Linen, denim and everyday pieces made for the weather here.";
        item.ButtonLabel ??= "Shop new arrivals";
        item.LinkUrl ??= "/new-arrivals";

        EnsureUsage(videoAssetId, MediaUsageOwnerTypes.HomeSection, section.Id, MediaUsageRoles.Hero);

        run.Add(new ClientAssetEntry(
            ClientAssetFolder.Home,
            Path.GetFileName(videoPath),
            wrote ? ClientAssetOutcome.Imported : ClientAssetOutcome.AlreadyPresent,
            string.Create(
                CultureInfo.InvariantCulture,
                $"the hero video (HOME-01), {dto.Width}×{dto.Height}, {dto.DurationSeconds}s")));

        run.Add(new ClientAssetEntry(
            ClientAssetFolder.Home,
            Path.GetFileName(posterPath),
            wrote ? ClientAssetOutcome.Imported : ClientAssetOutcome.AlreadyPresent,
            "the hero's poster and its still for phones and reduced motion"));

        EnableIfReady(section, run, "HOME-01 hero");
    }

    private async Task ImportBannerAsync(
        HomeSectionType type,
        string contractFile,
        string title,
        string subtitle,
        string linkUrl,
        string buttonLabel,
        IReadOnlyList<string> files,
        Run run,
        CancellationToken cancellationToken)
    {
        var path = files.FirstOrDefault(candidate => ClientAssetNaming.IsNamed(Path.GetFileName(candidate), contractFile));
        if (path is null)
        {
            run.Note($"{type} stayed switched off: '{contractFile}' is not in the drop.");
            return;
        }

        run.Touch(Path.GetFileName(path));

        var section = await SectionAsync(type, cancellationToken);
        if (section is null)
        {
            run.Fail($"There is no {type} section to attach '{contractFile}' to. Run the content seeder first.");
            return;
        }

        var stored = await StoreImageAsync(path, $"{title} — {subtitle}", title, run, cancellationToken);

        if (stored.Refusal is { } refusal)
        {
            run.Add(new ClientAssetEntry(
                ClientAssetFolder.Home, Path.GetFileName(path), ClientAssetOutcome.Refused, refusal));
            run.Fail($"home/{Path.GetFileName(path)}: {refusal}");
            return;
        }

        var wrote = false;

        if (stored.AssetId is { } assetId)
        {
            var item = section.Items.FirstOrDefault(candidate => candidate.SortOrder == 1)
                ?? AddItem(section, sortOrder: 1);

            if (item.ImageAssetId is null)
            {
                item.ImageAssetId = assetId;
                wrote = true;
            }

            item.Title ??= title;
            item.Subtitle ??= subtitle;
            item.LinkUrl ??= linkUrl;
            item.ButtonLabel ??= buttonLabel;

            EnsureUsage(assetId, MediaUsageOwnerTypes.HomeSection, section.Id, MediaUsageRoles.Banner);
            EnableIfReady(section, run, type.ToString());
        }

        run.Add(new ClientAssetEntry(
            ClientAssetFolder.Home,
            Path.GetFileName(path),
            stored.WasExisting && !wrote ? ClientAssetOutcome.AlreadyPresent : ClientAssetOutcome.Imported,
            $"the {type} banner"));
    }

    private async Task ImportTrendingAsync(
        string folder,
        IReadOnlyList<string> files,
        Run run,
        CancellationToken cancellationToken)
    {
        var tiles = files
            .Select(path => (Path: path, Index: ClientAssetNaming.TrendingIndexOf(Path.GetFileName(path))))
            .Where(tile => tile.Index is not null)
            .OrderBy(tile => tile.Index)
            .ToList();

        if (tiles.Count == 0)
        {
            run.Note("TrendingItems stayed switched off: no trending-N.jpg is in the drop.");
            return;
        }

        var section = await SectionAsync(HomeSectionType.TrendingItems, cancellationToken);
        if (section is null)
        {
            run.Fail("There is no TrendingItems section to attach the tiles to. Run the content seeder first.");
            return;
        }

        foreach (var (path, index) in tiles)
        {
            var fileName = Path.GetFileName(path);
            run.Touch(fileName);

            if (index is null || index.Value > TrendingTiles.Length)
            {
                run.Add(new ClientAssetEntry(
                    ClientAssetFolder.Home,
                    fileName,
                    ClientAssetOutcome.NotInContract,
                    $"there are only {TrendingTiles.Length} trending tiles with a listing to open"));
                continue;
            }

            var (title, url) = TrendingTiles[index.Value - 1];
            var stored = await StoreImageAsync(path, title, title, run, cancellationToken);

            if (stored.Refusal is { } refusal)
            {
                run.Add(new ClientAssetEntry(
                    ClientAssetFolder.Home, fileName, ClientAssetOutcome.Refused, refusal));
                run.Fail($"home/{fileName}: {refusal}");
                continue;
            }

            var wrote = false;

            if (stored.AssetId is { } assetId)
            {
                var sortOrder = (short)index.Value;
                var item = section.Items.FirstOrDefault(candidate => candidate.SortOrder == sortOrder)
                    ?? AddItem(section, sortOrder);

                if (item.ImageAssetId is null)
                {
                    item.ImageAssetId = assetId;
                    wrote = true;
                }

                item.Title ??= title;
                item.LinkUrl ??= url;

                EnsureUsage(assetId, MediaUsageOwnerTypes.HomeSection, section.Id, MediaUsageRoles.Image);
            }

            run.Add(new ClientAssetEntry(
                ClientAssetFolder.Home,
                fileName,
                stored.WasExisting && !wrote ? ClientAssetOutcome.AlreadyPresent : ClientAssetOutcome.Imported,
                $"trending tile {index.Value} — \"{title}\", opening {url}"));
        }

        EnableIfReady(section, run, "HOME-04 trending");
    }

    // -------------------------------------------------------------------- social

    private async Task ImportSocialAsync(string root, Run run, CancellationToken cancellationToken)
    {
        var folder = Path.Combine(root, "social");
        if (!Directory.Exists(folder))
        {
            run.Fail($"'{folder}' is missing, so the social gallery stayed switched off.");
            return;
        }

        var section = await SectionAsync(HomeSectionType.SocialGallery, cancellationToken);
        if (section is null)
        {
            run.Fail("There is no SocialGallery section to attach the tiles to. Run the content seeder first.");
            return;
        }

        foreach (var path in Files(folder))
        {
            var fileName = Path.GetFileName(path);
            var index = ClientAssetNaming.SocialIndexOf(fileName);

            if (index is null || index.Value > SocialPlaceholders.Length)
            {
                run.Add(new ClientAssetEntry(
                    ClientAssetFolder.Social,
                    fileName,
                    ClientAssetOutcome.NotInContract,
                    $"the contract is social-1 … social-{SocialPlaceholders.Length}"));
                continue;
            }

            var (network, url) = SocialPlaceholders[index.Value - 1];
            var label = $"{network} — PLACEHOLDER LINK, the real account URL has not been supplied yet";

            var stored = await StoreImageAsync(
                path,
                "Colombo Wardrobe clothes, photographed for social media",
                label,
                run,
                cancellationToken);

            if (stored.Refusal is { } refusal)
            {
                run.Add(new ClientAssetEntry(
                    ClientAssetFolder.Social, fileName, ClientAssetOutcome.Refused, refusal));
                run.Fail($"social/{fileName}: {refusal}");
                continue;
            }

            var wrote = false;

            if (stored.AssetId is { } assetId)
            {
                var sortOrder = (short)index.Value;
                var item = section.Items.FirstOrDefault(candidate => candidate.SortOrder == sortOrder)
                    ?? AddItem(section, sortOrder);

                if (item.ImageAssetId is null)
                {
                    item.ImageAssetId = assetId;
                    wrote = true;
                }

                // Ordinary admin-editable content: a row in content.HomeSectionItems that ADM-17 will
                // rewrite the moment the client sends the real account URLs.
                item.Title ??= label;
                item.LinkUrl ??= url;

                EnsureUsage(assetId, MediaUsageOwnerTypes.HomeSection, section.Id, MediaUsageRoles.Image);
            }

            run.Add(new ClientAssetEntry(
                ClientAssetFolder.Social,
                fileName,
                stored.WasExisting && !wrote ? ClientAssetOutcome.AlreadyPresent : ClientAssetOutcome.Imported,
                $"social tile {index.Value} — {network}, linked to the dummy {url}"));
        }

        EnableIfReady(section, run, "HOME-08 social gallery");

        run.Note(
            "The six social tiles carry dummy destinations on example.invalid (RFC 2606: a domain that "
            + "can never be registered), and each tile's accessible name says PLACEHOLDER LINK out loud. "
            + "Replace them in ADM-17 when the client sends the real account URLs.");
    }

    // ---------------------------------------------------------------------- logo

    private async Task ImportLogoAsync(string root, Run run, CancellationToken cancellationToken)
    {
        var folder = Path.Combine(root, "logo");
        if (!Directory.Exists(folder))
        {
            run.Note("No logo/ folder in the drop, so no logo artwork was imported.");
            return;
        }

        foreach (var path in Files(folder))
        {
            var fileName = Path.GetFileName(path);
            var (stem, _) = ClientAssetNaming.StripMediaExtensions(fileName);

            var stored = await StoreImageAsync(
                path,
                $"Colombo Wardrobe {stem.Replace('-', ' ')}",
                $"Logo — {stem}",
                run,
                cancellationToken);

            if (stored.Refusal is { } refusal)
            {
                run.Add(new ClientAssetEntry(
                    ClientAssetFolder.Logo, fileName, ClientAssetOutcome.Refused, refusal));
                run.Fail($"logo/{fileName}: {refusal}");
                continue;
            }

            run.Add(new ClientAssetEntry(
                ClientAssetFolder.Logo,
                fileName,
                stored.WasExisting ? ClientAssetOutcome.AlreadyPresent : ClientAssetOutcome.Imported,
                "the media library. Nothing references it yet — the header's wordmark is unchanged."));
        }
    }

    // ------------------------------------------------------------------- plumbing

    /// <summary>
    /// Switches a section on, but only once what it holds would actually draw.
    /// <para>
    /// The condition is the storefront's own <c>hasContent</c> rule restated: a hero, a banner or a
    /// gallery <i>is</i> its artwork, so a section whose items carry no asset is left off. That is the
    /// whole reason these five were seeded disabled — a bare heading over a gap is worse than nothing.
    /// </para>
    /// </summary>
    private static void EnableIfReady(HomeSection section, Run run, string label)
    {
        var hasArtwork = section.Items.Any(
            item => item.ImageAssetId is not null
                || item.MobileImageAssetId is not null
                || item.VideoAssetId is not null);

        if (!hasArtwork)
        {
            run.Note($"{label} stayed switched off: none of its items has a picture yet.");
            return;
        }

        if (section.IsEnabled)
        {
            return;
        }

        section.IsEnabled = true;
        run.Note($"{label} switched on: it now has media to show.");
    }

    private HomeSectionItem AddItem(HomeSection section, short sortOrder)
    {
        var item = new HomeSectionItem
        {
            HomeSectionId = section.Id,
            SortOrder = sortOrder,
            IsEnabled = true,
        };

        section.Items.Add(item);
        dbContext.HomeSectionItems.Add(item);
        return item;
    }

    private async Task<HomeSection?> SectionAsync(HomeSectionType type, CancellationToken cancellationToken) =>
        await dbContext.HomeSections
            .Include(section => section.Items)
            .FirstOrDefaultAsync(section => section.SectionType == type, cancellationToken);

    private async Task<List<HomeSectionItem>> TilesOfAsync(
        HomeSectionType type,
        CancellationToken cancellationToken)
    {
        var section = await SectionAsync(type, cancellationToken);
        return section is null ? [] : [.. section.Items];
    }

    /// <summary>
    /// Adds the reference row that puts a photograph in a product's gallery, unless it is already there.
    /// The unique index is (product, asset, colour), so that is what "already there" means.
    /// </summary>
    private bool EnsureProductMedia(int productId, int assetId, ProductMediaRole role, short sortOrder)
    {
        if (!_productMedia.Add((productId, assetId)))
        {
            return false;
        }

        dbContext.ProductMedia.Add(new ProductMedia
        {
            ProductId = productId,
            MediaAssetId = assetId,

            // Null, not a colourway: the client sent two photographs per garment, not one per colour, so
            // claiming either belongs to a particular colourway would be inventing information. A null
            // row shows for every colour, which is what galleryFor already does with it.
            ProductColourId = null,
            MediaRole = role,
            SortOrder = sortOrder,

            // No override: the alt text lives on the asset, where the card and the listing read it from
            // too, so there is one sentence per photograph rather than two that can drift.
            AltTextOverride = null,
        });

        return true;
    }

    /// <summary>
    /// Mirrors a reference in <c>media.MediaUsages</c> (doc 08 §3.3) — the "used in…" panel and the
    /// delete guard. The unique key is (asset, owner type, owner id, role), so a re-run adds nothing.
    /// </summary>
    private void EnsureUsage(int assetId, string ownerType, int ownerId, string role)
    {
        var id = ownerId.ToString(CultureInfo.InvariantCulture);

        if (!_usages.Add((assetId, ownerType, id, role)))
        {
            return;
        }

        dbContext.MediaUsages.Add(new MediaUsage
        {
            MediaAssetId = assetId,
            OwnerType = ownerType,
            OwnerId = id,
            UsageRole = role,
        });
    }

    /// <summary>
    /// Reads the reference rows that are already in the database, so the "unless it is already there"
    /// tests above are answered from memory and a second run of the same drop writes nothing.
    /// </summary>
    private async Task LoadExistingReferencesAsync(CancellationToken cancellationToken)
    {
        var media = await dbContext.ProductMedia
            .Where(row => row.ProductColourId == null)
            .Select(row => new { row.ProductId, row.MediaAssetId })
            .ToListAsync(cancellationToken);

        foreach (var row in media)
        {
            _productMedia.Add((row.ProductId, row.MediaAssetId));
        }

        var usages = await dbContext.MediaUsages
            .Select(usage => new { usage.MediaAssetId, usage.OwnerType, usage.OwnerId, usage.UsageRole })
            .ToListAsync(cancellationToken);

        foreach (var usage in usages)
        {
            _usages.Add((usage.MediaAssetId, usage.OwnerType, usage.OwnerId, usage.UsageRole));
        }
    }

    private async Task<StoredAsset> StoreImageAsync(
        string path,
        string altText,
        string title,
        Run run,
        CancellationToken cancellationToken)
    {
        if (run.DryRun)
        {
            return new StoredAsset(AssetId: null, WasExisting: false, Refusal: null);
        }

        await using var content = File.OpenRead(path);

        var outcome = await media.UploadImageAsync(
            new UploadImageRequest(
                new MediaUploadFile(content, Path.GetFileName(path)),
                MediaVisibility.Public,
                altText,
                title,
                UploadedByUserId: null),
            cancellationToken);

        return outcome switch
        {
            MediaUploadOutcome.Created created =>
                new StoredAsset(await AssetIdAsync(created.Asset.PublicId, cancellationToken), false, null),
            MediaUploadOutcome.Duplicate duplicate =>
                new StoredAsset(await AssetIdAsync(duplicate.Asset.PublicId, cancellationToken), true, null),
            MediaUploadOutcome.Rejected rejected =>
                new StoredAsset(null, false, $"{rejected.Reason}: {rejected.Message}"),
            _ => throw new InvalidOperationException("Unknown upload outcome."),
        };
    }

    private async Task<int> AssetIdAsync(Guid publicId, CancellationToken cancellationToken) =>
        await dbContext.MediaAssets
            .Where(asset => asset.PublicId == publicId)
            .Select(asset => asset.Id)
            .FirstAsync(cancellationToken);

    /// <summary>Files in a folder, in a stable order, with the operating system's order not trusted.</summary>
    private static IEnumerable<string> Files(string folder) =>
        Directory.EnumerateFiles(folder).Order(StringComparer.OrdinalIgnoreCase);

    private sealed record StoredAsset(int? AssetId, bool WasExisting, string? Refusal);

    /// <summary>Everything one run accumulates, so the methods above stay about the import.</summary>
    private sealed class Run(bool dryRun)
    {
        private readonly HashSet<string> _touched = new(StringComparer.OrdinalIgnoreCase);

        public bool DryRun { get; } = dryRun;

        public List<ClientAssetEntry> Entries { get; } = [];

        public List<string> Failures { get; } = [];

        public List<string> Notes { get; } = [];

        public bool Succeeded => Failures.Count == 0;

        public void Add(ClientAssetEntry entry) => Entries.Add(entry);

        public void Fail(string reason) => Failures.Add(reason);

        public void Note(string note) => Notes.Add(note);

        public void Touch(string fileName) => _touched.Add(fileName);

        public bool Touched(string fileName) => _touched.Contains(fileName);

        public ClientAssetImportReport ToReport() => new(Entries, Failures, Notes);
    }

    [LoggerMessage(
        EventId = 1401,
        Level = LogLevel.Information,
        Message = "Client asset import finished: {Files} files, {Failures} failures (dry run: {DryRun}).")]
    private static partial void LogFinished(ILogger logger, int files, int failures, bool dryRun);
}
