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
using SpencerDenim.Infrastructure.Persistence.Seeding;

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
/// <b>A run is all or nothing.</b> <see cref="IMediaService"/> saves on this same scoped
/// <c>AppDbContext</c> for every upload, so without a transaction a run that failed half way through
/// had already committed everything before the failure — measured once at 88 assets, 358 files, 70
/// gallery rows and four homepage sections switched live, under a report that said FAILED. The whole
/// run is now one transaction, committed only when nothing has failed, and the files written by assets
/// this run created are deleted when it rolls back.
/// </para>
/// <para>
/// <b>Running it again changes nothing, and never overwrites a person.</b> The pipeline deduplicates by
/// checksum, so a second run of the same drop gets the same assets back and writes no file. A slot that
/// already holds a picture somebody chose is <b>not</b> overwritten: the file is reported as a conflict
/// and the operator re-runs with <c>--replace</c>, which is the "the client sent a corrected
/// photograph" case said out loud. A slot holding one of the demo placeholders counts as empty, because
/// a placeholder is not a choice.
/// </para>
/// </summary>
public sealed partial class ClientAssetImporter(
    AppDbContext dbContext,
    IMediaService media,
    IFileStorage storage,
    ClientAssetHolds holds,
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
    /// <para>
    /// These two numbers are also the importer's <b>slot keys</b>: a gallery row is identified by
    /// (product, role, sort order), not by the asset in it, so a corrected photograph <i>replaces</i>
    /// the one it supersedes instead of being added beside it. Keying on the asset meant two rows at
    /// −2 both claiming to be the card, the superseded photograph still in the gallery, and a delete
    /// guard that would never let go of it.
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
    /// The reference rows that already exist, read once per attempt as <b>tracked</b> entities.
    /// <para>
    /// Tracked rather than projected because the import edits them — replacing the asset in a slot is an
    /// update of the row that holds it, not an insert — and because "an owner's save rewrites its usage
    /// rows" (doc 08 §3.3) needs the rows in hand to rewrite. There are a few hundred of each on a
    /// seeded catalogue, and the importer is a one-shot object resolved per command, so this state lives
    /// exactly as long as one attempt does.
    /// </para>
    /// </summary>
    private readonly List<ProductMedia> _productMedia = [];

    private readonly List<MediaUsage> _usages = [];

    /// <summary>
    /// The demo placeholder assets, by id. A slot pointing at one of these counts as <b>empty</b>: a
    /// placeholder is what the seeder put there because nothing better existed, not a choice anyone made,
    /// so filling it needs no <c>--replace</c>.
    /// </summary>
    private readonly HashSet<int> _placeholders = [];

    public async Task<ClientAssetImportReport> ImportAsync(
        ClientAssetImportRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!storage.IsConfigured)
        {
            var refused = new Run(request);
            refused.Fail(
                "Media storage is not configured on this deployment (Media__PublicRoot is empty), so "
                + "nothing can be stored. Set it and run the import again.");
            return refused.ToReport();
        }

        if (!Directory.Exists(request.SourceDirectory))
        {
            var missing = new Run(request);
            missing.Fail($"The asset drop '{request.SourceDirectory}' does not exist.");
            return missing.ToReport();
        }

        // The connection is configured with EnableRetryOnFailure, and that execution strategy refuses a
        // user-initiated transaction unless the whole unit of work is inside it — so the attempt, not
        // the transaction, is the thing that gets retried. Every attempt starts from a clean change
        // tracker and a fresh Run, because a retry on a context still holding the rolled-back attempt's
        // edits would be worse than no retry at all.
        var strategy = dbContext.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(
            async token => await AttemptAsync(request, token),
            cancellationToken);
    }

    private async Task<ClientAssetImportReport> AttemptAsync(
        ClientAssetImportRequest request,
        CancellationToken cancellationToken)
    {
        dbContext.ChangeTracker.Clear();
        _productMedia.Clear();
        _usages.Clear();
        _placeholders.Clear();

        var run = new Run(request);

        // A dry run reads and reports; there is nothing to wrap.
        if (request.DryRun)
        {
            await WalkAsync(request, run, cancellationToken);
            LogFinished(logger, run.Entries.Count, run.Failures.Count, request.DryRun);
            return run.ToReport();
        }

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);

        try
        {
            await WalkAsync(request, run, cancellationToken);

            if (run.Succeeded)
            {
                ApplySectionSwitches(run);
                await dbContext.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                LogFinished(logger, run.Entries.Count, run.Failures.Count, request.DryRun);
                return run.ToReport();
            }

            await transaction.RollbackAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await transaction.RollbackAsync(CancellationToken.None);
            await DiscardCreatedFilesAsync(run, CancellationToken.None);
            throw;
        }

        // Rolled back, so not one row survives. The files do, because storage is not transactional —
        // they are taken back out here, which is the same compensating delete the upload pipeline makes
        // when a rendition fails half way through a set.
        await DiscardCreatedFilesAsync(run, cancellationToken);

        LogFinished(logger, run.Entries.Count, run.Failures.Count, request.DryRun);
        return run.ToReport();
    }

    /// <summary>Everything the import looks at, in dependency order. Writes nothing on its own.</summary>
    private async Task WalkAsync(
        ClientAssetImportRequest request,
        Run run,
        CancellationToken cancellationToken)
    {
        await LoadExistingReferencesAsync(cancellationToken);

        await ImportProductsAsync(request.SourceDirectory, run, cancellationToken);

        // A photograph nobody can place stops the run before anything else is even read. The transaction
        // would undo the rest anyway; stopping here means the report is about the one thing that needs
        // fixing rather than about ninety files that were fine.
        if (!run.Succeeded)
        {
            return;
        }

        await ImportDepartmentsAsync(request.SourceDirectory, run, cancellationToken);
        await ImportHomeAsync(request.SourceDirectory, run, cancellationToken);
        await ImportSocialAsync(request.SourceDirectory, run, cancellationToken);
        await ImportLogoAsync(request.SourceDirectory, run, cancellationToken);
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

        // Case-insensitive, because a filename's case is not a fact about the product. On Windows
        // `Chino-Shorts-1.jpg` and `chino-shorts-1.jpg` are the same file; matching it ordinally made a
        // capital letter abort the entire run under a message about an unknown product. Slugs are
        // unique under this comparer too (an integration test asserts it), so nothing becomes ambiguous.
        var slugs = products.Select(product => product.Slug).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var byslug = products.ToDictionary(product => product.Slug, StringComparer.OrdinalIgnoreCase);

        // Resolve every name before a single byte is stored, so a batch with an unplaceable photograph
        // in it fails before it has stored the rest.
        var resolved = new Dictionary<string, List<ProductAssetName>>(StringComparer.OrdinalIgnoreCase);

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
            var sawCard = false;

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

                var outcome = SlotOutcome.Unchanged;

                if (entity is not null && stored.AssetId is { } assetId)
                {
                    var (mediaRole, sortOrder) = file.Index switch
                    {
                        1 => (ProductMediaRole.Gallery, CardSortOrder),
                        2 => (ProductMediaRole.Hover, HoverSortOrder),
                        _ => (ProductMediaRole.Gallery, (short)(file.Index - 2)),
                    };

                    outcome = PlaceProductMedia(product.Id, assetId, mediaRole, sortOrder, run.Replace);

                    if (outcome != SlotOutcome.Conflict)
                    {
                        switch (file.Index)
                        {
                            case 1:
                                cardAssetId = assetId;
                                sawCard = true;
                                break;
                            case 2:
                                hoverAssetId = assetId;
                                break;
                            default:
                                break;
                        }
                    }
                }

                run.Add(Describe(
                    ClientAssetFolder.Products,
                    file.FileName,
                    outcome,
                    stored,
                    $"{product.Slug} — {role}, alt text \"{alt}\"",
                    file.RepairedFrom));
            }

            if (entity is null)
            {
                continue;
            }

            if (cardAssetId is { } card)
            {
                SetProductPointer(entity, isCard: true, card, run);
            }

            // A product with one photograph gets a card and **no hover**. Leaving the seeded placeholder
            // in the hover slot would mean a real garment on the card flipping to a grey frame that says
            // no photograph was supplied — which is worse than a card that simply does not flip.
            if (sawCard)
            {
                if (hoverAssetId is { } hover)
                {
                    SetProductPointer(entity, isCard: false, hover, run);
                }
                else if (entity.HoverImageAssetId is { } current && _placeholders.Contains(current))
                {
                    SetProductPointer(entity, isCard: false, null, run);
                    run.Note(
                        $"{product.Slug}: one photograph was supplied, so the card is the client's and the "
                        + "hover image was cleared rather than left pointing at a placeholder.");
                }
            }

            RewriteProductUsages(entity);
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

            var outcome = SlotOutcome.Unchanged;

            if (stored.AssetId is { } assetId)
            {
                outcome = Fill(
                    department.HeroImageAssetId,
                    assetId,
                    run.Replace,
                    value => department.HeroImageAssetId = value,
                    old => RemoveUsage(old, MediaUsageOwnerTypes.Department, department.Id, MediaUsageRoles.Hero));

                if (outcome != SlotOutcome.Conflict)
                {
                    AddUsage(assetId, MediaUsageOwnerTypes.Department, department.Id, MediaUsageRoles.Hero);
                }

                // HOME-02's tile is a different row from the department itself, and it is the one the
                // homepage draws. Both get the picture.
                var tile = tiles.FirstOrDefault(item => item.DepartmentId == department.Id);
                if (tile is not null)
                {
                    var tileOutcome = await PlaceItemImageAsync(tile, assetId, MediaUsageRoles.Image, run, cancellationToken);
                    outcome = Worse(outcome, tileOutcome);
                }
            }

            run.Add(Describe(
                ClientAssetFolder.Departments,
                fileName,
                outcome,
                stored,
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

        await ImportHeroAsync(files, run, cancellationToken);
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
        await ImportTrendingAsync(files, run, cancellationToken);

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
        IReadOnlyList<string> files,
        Run run,
        CancellationToken cancellationToken)
    {
        var videoPath = files.FirstOrDefault(
            path => ClientAssetNaming.IsNamed(Path.GetFileName(path), ClientAssetNaming.HeroVideoFile)
                && Path.GetExtension(path).Equals(".mp4", StringComparison.OrdinalIgnoreCase));

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

        if (outcome is MediaUploadOutcome.Created)
        {
            run.Created(dto);
        }

        run.NoteHold(holds.For(dto.Checksum));

        var videoAssetId = await AssetIdAsync(dto.PublicId, cancellationToken);
        var posterAssetId = dto.PosterPublicId is { } posterPublicId
            ? await AssetIdAsync(posterPublicId, cancellationToken)
            : throw new InvalidOperationException("A stored video has no poster, which the schema forbids.");

        var item = await ItemAtAsync(section, sortOrder: 1, cancellationToken);

        var videoSlot = Fill(
            item.VideoAssetId,
            videoAssetId,
            run.Replace,
            value => item.VideoAssetId = value,
            old => RemoveUsage(old, MediaUsageOwnerTypes.HomeSectionItem, item.Id, MediaUsageRoles.Video));

        if (videoSlot != SlotOutcome.Conflict)
        {
            AddUsage(videoAssetId, MediaUsageOwnerTypes.HomeSectionItem, item.Id, MediaUsageRoles.Video);
        }

        // Doc 08 §3.7's CK: a video item must carry a still. The poster is that still — it is the frame
        // the phone, the data saver and prefers-reduced-motion all get, which is exactly what it is for.
        var posterSlot = Fill(
            item.ImageAssetId,
            posterAssetId,
            run.Replace,
            value => item.ImageAssetId = value,
            old => RemoveUsage(old, MediaUsageOwnerTypes.HomeSectionItem, item.Id, MediaUsageRoles.Image));

        if (posterSlot != SlotOutcome.Conflict)
        {
            AddUsage(posterAssetId, MediaUsageOwnerTypes.HomeSectionItem, item.Id, MediaUsageRoles.Image);
        }

        item.Title ??= "Dressed for Colombo";
        item.Subtitle ??= "Linen, denim and everyday pieces made for the weather here.";
        item.ButtonLabel ??= "Shop new arrivals";
        item.LinkUrl ??= "/new-arrivals";

        run.Add(Describe(
            ClientAssetFolder.Home,
            Path.GetFileName(videoPath),
            videoSlot,
            new StoredAsset(videoAssetId, outcome is MediaUploadOutcome.Duplicate, null),
            string.Create(
                CultureInfo.InvariantCulture,
                $"the hero video (HOME-01), {dto.Width}x{dto.Height}, {dto.DurationSeconds}s")));

        run.Add(Describe(
            ClientAssetFolder.Home,
            Path.GetFileName(posterPath),
            posterSlot,
            new StoredAsset(posterAssetId, outcome is MediaUploadOutcome.Duplicate, null),
            "the hero's poster and its still for phones and reduced motion"));

        run.WhenDone(section, "HOME-01 hero");
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

        var outcome = SlotOutcome.Unchanged;

        if (stored.AssetId is { } assetId)
        {
            var item = await ItemAtAsync(section, sortOrder: 1, cancellationToken);
            outcome = await PlaceItemImageAsync(item, assetId, MediaUsageRoles.Banner, run, cancellationToken);

            item.Title ??= title;
            item.Subtitle ??= subtitle;
            item.LinkUrl ??= linkUrl;
            item.ButtonLabel ??= buttonLabel;

            run.WhenDone(section, type.ToString());
        }

        run.Add(Describe(ClientAssetFolder.Home, Path.GetFileName(path), outcome, stored, $"the {type} banner"));
    }

    private async Task ImportTrendingAsync(
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

            var outcome = SlotOutcome.Unchanged;

            if (stored.AssetId is { } assetId)
            {
                var item = await ItemAtAsync(section, (short)index.Value, cancellationToken);
                outcome = await PlaceItemImageAsync(item, assetId, MediaUsageRoles.Image, run, cancellationToken);

                item.Title ??= title;
                item.LinkUrl ??= url;
            }

            run.Add(Describe(
                ClientAssetFolder.Home,
                fileName,
                outcome,
                stored,
                $"trending tile {index.Value} — \"{title}\", opening {url}"));
        }

        run.WhenDone(section, "HOME-04 trending");
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

            var outcome = SlotOutcome.Unchanged;

            if (stored.AssetId is { } assetId)
            {
                var item = await ItemAtAsync(section, (short)index.Value, cancellationToken);
                outcome = await PlaceItemImageAsync(item, assetId, MediaUsageRoles.Image, run, cancellationToken);

                // Ordinary admin-editable content: a row in content.HomeSectionItems that ADM-17 will
                // rewrite the moment the client sends the real account URLs.
                item.Title ??= label;
                item.LinkUrl ??= url;
            }

            run.Add(Describe(
                ClientAssetFolder.Social,
                fileName,
                outcome,
                stored,
                $"social tile {index.Value} — {network}, linked to the dummy {url}"));
        }

        run.WhenDone(section, "HOME-08 social gallery");

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

            // A wordmark has to sit on whatever is behind it, so client-assets/README.md asks for a PNG
            // with a transparent background. The operator reads this report and nothing else, so a file
            // that cannot meet that is said here rather than discovered later by a designer.
            if (!run.DryRun && ImageTransparency.Of(path) is false)
            {
                run.Note(
                    $"logo/{fileName} has no transparent background — it is {ImageTransparency.Describe(path)}, "
                    + "whatever its extension says, and the media pipeline stored it by its signature. A "
                    + "wordmark on an opaque rectangle cannot be drawn over a photograph or a dark "
                    + "section, so this one cannot be wired into the header as delivered.");
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
    /// Switches on every section whose media arrived, now that the run is known to have succeeded.
    /// <para>
    /// Deferred to the very end for the reason the transaction exists: a run that fails after the
    /// campaign banner has been wired up must not leave the campaign banner live. The condition is the
    /// storefront's own <c>hasContent</c> rule restated — a hero, a banner or a gallery <i>is</i> its
    /// artwork — plus the held-back list, which keeps a section off when the picture it was given does
    /// not fit the slot its filename claims.
    /// </para>
    /// </summary>
    private static void ApplySectionSwitches(Run run)
    {
        foreach (var (section, label) in run.Pending)
        {
            if (run.HoldFor(section.SectionType) is { } reason)
            {
                run.Note($"{label} left switched off: {reason}");
                continue;
            }

            var hasArtwork = section.Items.Any(
                item => item.ImageAssetId is not null
                    || item.MobileImageAssetId is not null
                    || item.VideoAssetId is not null);

            if (!hasArtwork)
            {
                run.Note($"{label} stayed switched off: none of its items has a picture yet.");
                continue;
            }

            if (section.IsEnabled)
            {
                continue;
            }

            section.IsEnabled = true;
            run.Note($"{label} switched on: it now has media to show.");
        }
    }

    /// <summary>
    /// The item at a section's sort order, created when there is none. Saved immediately, because a
    /// <c>MediaUsages</c> row names the item by id (doc 08 §3.3) and a new item has none until it is.
    /// The save is inside this run's transaction, so it is still all or nothing.
    /// </summary>
    private async Task<HomeSectionItem> ItemAtAsync(
        HomeSection section,
        short sortOrder,
        CancellationToken cancellationToken)
    {
        var existing = section.Items.FirstOrDefault(candidate => candidate.SortOrder == sortOrder);
        if (existing is not null)
        {
            return existing;
        }

        var item = new HomeSectionItem
        {
            HomeSectionId = section.Id,
            SortOrder = sortOrder,
            IsEnabled = true,
        };

        section.Items.Add(item);
        dbContext.HomeSectionItems.Add(item);
        await dbContext.SaveChangesAsync(cancellationToken);
        return item;
    }

    /// <summary>Puts an asset in a homepage item's image slot, with the usage row that names the item.</summary>
    private async Task<SlotOutcome> PlaceItemImageAsync(
        HomeSectionItem item,
        int assetId,
        string usageRole,
        Run run,
        CancellationToken cancellationToken)
    {
        if (item.Id == 0)
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        var outcome = Fill(
            item.ImageAssetId,
            assetId,
            run.Replace,
            value => item.ImageAssetId = value,
            old => RemoveUsage(old, MediaUsageOwnerTypes.HomeSectionItem, item.Id, usageRole));

        if (outcome != SlotOutcome.Conflict)
        {
            AddUsage(assetId, MediaUsageOwnerTypes.HomeSectionItem, item.Id, usageRole);
        }

        return outcome;
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
    /// Puts a photograph in one gallery slot, where a slot is <b>(product, role, sort order)</b>.
    /// <para>
    /// Keying on the slot rather than on the asset is what makes a corrected photograph a replacement.
    /// With the asset as the key a re-encoded file was a different asset, so it was <i>added</i>: two
    /// rows at sort order −2, both claiming to be the card, the superseded photograph still in the
    /// gallery, and its usage row keeping the delete guard shut for ever.
    /// </para>
    /// </summary>
    private SlotOutcome PlaceProductMedia(
        int productId,
        int assetId,
        ProductMediaRole role,
        short sortOrder,
        bool replace)
    {
        var existing = _productMedia.FirstOrDefault(
            row => row.ProductId == productId
                && row.MediaRole == role
                && row.SortOrder == sortOrder
                && row.ProductColourId is null);

        if (existing is null)
        {
            var row = new ProductMedia
            {
                ProductId = productId,
                MediaAssetId = assetId,

                // Null, not a colourway: the client sent two photographs per garment, not one per
                // colour, so claiming either belongs to a particular colourway would be inventing
                // information. A null row shows for every colour, which is what galleryFor does with it.
                ProductColourId = null,
                MediaRole = role,
                SortOrder = sortOrder,

                // No override: the alt text lives on the asset, where the card and the listing read it
                // from too, so there is one sentence per photograph rather than two that can drift.
                AltTextOverride = null,
            };

            dbContext.ProductMedia.Add(row);
            _productMedia.Add(row);
            return SlotOutcome.Filled;
        }

        if (existing.MediaAssetId == assetId)
        {
            return SlotOutcome.Unchanged;
        }

        if (!replace)
        {
            return SlotOutcome.Conflict;
        }

        existing.MediaAssetId = assetId;
        return SlotOutcome.Replaced;
    }

    /// <summary>
    /// Moves a product's denormalised card or hover pointer (doc 08 §7.4).
    /// <para>
    /// A pointer at one of the demo placeholders counts as empty — that is what the seeder puts there
    /// when there is no photograph, not a choice. A pointer at anything else is somebody's decision and
    /// survives unless the operator asked for <c>--replace</c>.
    /// </para>
    /// </summary>
    private void SetProductPointer(Product product, bool isCard, int? assetId, Run run)
    {
        var current = isCard ? product.CardImageAssetId : product.HoverImageAssetId;

        if (current == assetId)
        {
            return;
        }

        if (current is { } held && !_placeholders.Contains(held) && !run.Replace)
        {
            run.Note(
                $"{product.Slug}: the {(isCard ? "card" : "hover")} image was left as it is — it already "
                + "points at a picture that is not a placeholder, so somebody chose it. Re-run with "
                + "--replace to overwrite it.");
            return;
        }

        if (isCard)
        {
            product.CardImageAssetId = assetId;
        }
        else
        {
            product.HoverImageAssetId = assetId;
        }
    }

    /// <summary>
    /// Rewrites one product's usage rows from what the product now actually references — doc 08 §3.3's
    /// "an owner's save rewrites its usage rows in the same transaction", literally.
    /// <para>
    /// Recomputed rather than patched because the same asset can legitimately fill two slots (two
    /// identical files in a drop deduplicate to one asset), so "the slot changed, therefore drop the old
    /// usage" is not sound. The desired set is derived from the rows and pointers themselves, which
    /// cannot disagree with them.
    /// </para>
    /// </summary>
    private void RewriteProductUsages(Product product)
    {
        var ownerId = product.Id.ToString(CultureInfo.InvariantCulture);

        var desired = new HashSet<(int AssetId, string Role)>();

        if (product.CardImageAssetId is { } card)
        {
            desired.Add((card, MediaUsageRoles.Card));
        }

        if (product.HoverImageAssetId is { } hover)
        {
            desired.Add((hover, MediaUsageRoles.Hover));
        }

        foreach (var row in _productMedia.Where(row => row.ProductId == product.Id))
        {
            desired.Add((row.MediaAssetId, MediaUsageRoles.Gallery));
        }

        var existing = _usages
            .Where(usage => usage.OwnerType == MediaUsageOwnerTypes.Product && usage.OwnerId == ownerId)
            .ToList();

        foreach (var stale in existing.Where(usage => !desired.Contains((usage.MediaAssetId, usage.UsageRole))))
        {
            dbContext.MediaUsages.Remove(stale);
            _usages.Remove(stale);
        }

        foreach (var (assetId, role) in desired)
        {
            AddUsage(assetId, MediaUsageOwnerTypes.Product, product.Id, role);
        }
    }

    /// <summary>
    /// Fills a nullable asset reference, honouring the "never overwrite a person" rule.
    /// </summary>
    /// <param name="onReplace">Called with the superseded asset id so its usage row can go.</param>
    private static SlotOutcome Fill(
        int? current,
        int assetId,
        bool replace,
        Action<int?> set,
        Action<int> onReplace)
    {
        if (current == assetId)
        {
            return SlotOutcome.Unchanged;
        }

        if (current is null)
        {
            set(assetId);
            return SlotOutcome.Filled;
        }

        if (!replace)
        {
            return SlotOutcome.Conflict;
        }

        onReplace(current.Value);
        set(assetId);
        return SlotOutcome.Replaced;
    }

    private void AddUsage(int assetId, string ownerType, int ownerId, string role)
    {
        var id = ownerId.ToString(CultureInfo.InvariantCulture);

        var present = _usages.Any(
            usage => usage.MediaAssetId == assetId
                && usage.OwnerType == ownerType
                && usage.OwnerId == id
                && usage.UsageRole == role);

        if (present)
        {
            return;
        }

        var row = new MediaUsage
        {
            MediaAssetId = assetId,
            OwnerType = ownerType,
            OwnerId = id,
            UsageRole = role,
        };

        dbContext.MediaUsages.Add(row);
        _usages.Add(row);
    }

    private void RemoveUsage(int assetId, string ownerType, int ownerId, string role)
    {
        var id = ownerId.ToString(CultureInfo.InvariantCulture);

        var row = _usages.FirstOrDefault(
            usage => usage.MediaAssetId == assetId
                && usage.OwnerType == ownerType
                && usage.OwnerId == id
                && usage.UsageRole == role);

        if (row is null)
        {
            return;
        }

        dbContext.MediaUsages.Remove(row);
        _usages.Remove(row);
    }

    /// <summary>
    /// Reads the rows the import edits, as tracked entities, plus the ids of the demo placeholders.
    /// </summary>
    private async Task LoadExistingReferencesAsync(CancellationToken cancellationToken)
    {
        _productMedia.AddRange(await dbContext.ProductMedia.ToListAsync(cancellationToken));
        _usages.AddRange(await dbContext.MediaUsages.ToListAsync(cancellationToken));

        var placeholders = await dbContext.MediaAssets
            .Where(asset => asset.StorageKey.StartsWith(DemoMediaPlaceholders.StorageKeyPrefix))
            .Select(asset => asset.Id)
            .ToListAsync(cancellationToken);

        foreach (var id in placeholders)
        {
            _placeholders.Add(id);
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

        switch (outcome)
        {
            case MediaUploadOutcome.Created created:
                run.Created(created.Asset);
                run.NoteHold(holds.For(created.Asset.Checksum));
                return new StoredAsset(await AssetIdAsync(created.Asset.PublicId, cancellationToken), false, null);

            case MediaUploadOutcome.Duplicate duplicate:
                run.NoteHold(holds.For(duplicate.Asset.Checksum));
                return new StoredAsset(await AssetIdAsync(duplicate.Asset.PublicId, cancellationToken), true, null);

            case MediaUploadOutcome.Rejected rejected:
                return new StoredAsset(null, false, $"{rejected.Reason}: {rejected.Message}");

            default:
                throw new InvalidOperationException("Unknown upload outcome.");
        }
    }

    /// <summary>
    /// Takes back out the files of every asset this attempt created, after the transaction rolled its
    /// rows away. Storage is not transactional, so without this a failed run leaves a few hundred files
    /// nothing will ever reference and nothing will ever purge.
    /// </summary>
    private async Task DiscardCreatedFilesAsync(Run run, CancellationToken cancellationToken)
    {
        var removed = 0;

        foreach (var asset in run.CreatedAssets)
        {
            try
            {
                if (await storage.DeleteAsync(asset.StorageKey, cancellationToken))
                {
                    removed++;
                }

                removed += await storage.DeletePrefixAsync(
                    MediaStorageKey.RenditionFolderFor(asset.PublicId),
                    cancellationToken);
            }
            catch (IOException exception)
            {
                // Best effort, and said out loud: a file left behind is litter, not corruption, and a
                // second failure here must not hide the first one.
                run.Note($"Could not remove '{asset.StorageKey}' after the rollback: {exception.Message}");
            }
        }

        if (run.CreatedAssets.Count > 0)
        {
            run.Note(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"The run was rolled back: no row survives it, and {removed} file(s) written by the "
                    + $"{run.CreatedAssets.Count} asset(s) it had created were removed again."));
        }
    }

    private async Task<int> AssetIdAsync(Guid publicId, CancellationToken cancellationToken) =>
        await dbContext.MediaAssets
            .Where(asset => asset.PublicId == publicId)
            .Select(asset => asset.Id)
            .FirstAsync(cancellationToken);

    /// <summary>Files in a folder, in a stable order, with the operating system's order not trusted.</summary>
    private static IEnumerable<string> Files(string folder) =>
        Directory.EnumerateFiles(folder).Order(StringComparer.OrdinalIgnoreCase);

    /// <summary>One report line, from what the pipeline did and what the slot did.</summary>
    private static ClientAssetEntry Describe(
        ClientAssetFolder folder,
        string fileName,
        SlotOutcome slot,
        StoredAsset stored,
        string detail,
        string? repairedFrom = null)
    {
        var outcome = slot switch
        {
            SlotOutcome.Conflict => ClientAssetOutcome.Conflict,
            SlotOutcome.Replaced => ClientAssetOutcome.Replaced,
            SlotOutcome.Filled => ClientAssetOutcome.Imported,
            _ => stored.WasExisting ? ClientAssetOutcome.AlreadyPresent : ClientAssetOutcome.Imported,
        };

        var text = slot == SlotOutcome.Conflict
            ? detail + " — the slot already holds a different picture that somebody chose. "
                + "Re-run with --replace to overwrite it."
            : detail;

        return new ClientAssetEntry(folder, fileName, outcome, text, repairedFrom);
    }

    /// <summary>The worse of two slot outcomes, for a file that fills more than one slot.</summary>
    private static SlotOutcome Worse(SlotOutcome left, SlotOutcome right) =>
        (SlotOutcome)Math.Max((int)left, (int)right);

    /// <summary>What happened to one reference slot. Ordered worst last, for <see cref="Worse"/>.</summary>
    private enum SlotOutcome
    {
        Unchanged = 0,
        Filled = 1,
        Replaced = 2,
        Conflict = 3,
    }

    private sealed record StoredAsset(int? AssetId, bool WasExisting, string? Refusal);

    /// <summary>Everything one attempt accumulates, so the methods above stay about the import.</summary>
    private sealed class Run(ClientAssetImportRequest request)
    {
        private readonly HashSet<string> _touched = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<HomeSectionType, string> _holds = [];
        private readonly List<(HomeSection Section, string Label)> _pending = [];
        private readonly List<MediaAssetDto> _created = [];

        public bool DryRun { get; } = request.DryRun;

        public bool Replace { get; } = request.Replace;

        public List<ClientAssetEntry> Entries { get; } = [];

        public List<string> Failures { get; } = [];

        public List<string> Notes { get; } = [];

        /// <summary>Assets this attempt created, so their files can be taken back out on a rollback.</summary>
        public List<MediaAssetDto> CreatedAssets => _created;

        /// <summary>Sections to consider switching on, once the run is known to have succeeded.</summary>
        public List<(HomeSection Section, string Label)> Pending => _pending;

        public bool Succeeded => Failures.Count == 0;

        public void Add(ClientAssetEntry entry) => Entries.Add(entry);

        public void Fail(string reason) => Failures.Add(reason);

        public void Note(string note) => Notes.Add(note);

        public void Created(MediaAssetDto asset) => _created.Add(asset);

        public void Touch(string fileName) => _touched.Add(fileName);

        public bool Touched(string fileName) => _touched.Contains(fileName);

        public void WhenDone(HomeSection section, string label) => _pending.Add((section, label));

        /// <summary>Records that a stored file is one of the held-back pictures.</summary>
        public void NoteHold(HeldBackArtwork? held)
        {
            if (held is not null)
            {
                _holds[held.Section] = held.Reason;
            }
        }

        public string? HoldFor(HomeSectionType type) => _holds.GetValueOrDefault(type);

        public ClientAssetImportReport ToReport() => new(Entries, Failures, Notes);
    }

    [LoggerMessage(
        EventId = 1401,
        Level = LogLevel.Information,
        Message = "Client asset import finished: {Files} files, {Failures} failures (dry run: {DryRun}).")]
    private static partial void LogFinished(ILogger logger, int files, int failures, bool dryRun);
}
