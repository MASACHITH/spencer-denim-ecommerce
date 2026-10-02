using System.Collections.Frozen;
using System.Globalization;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SpencerDenim.Application.Media.Abstractions;
using SpencerDenim.Application.Media.ClientAssets;
using SpencerDenim.Application.Media.Contracts;
using SpencerDenim.Domain.Branding;
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

    /// <summary>The campaign banner's words (HOME-05). Nothing before them to supersede.</summary>
    private static readonly BannerCopy CampaignCopy = new(
        "The new season",
        "Linen, denim and everything in between.",
        "/new-arrivals",
        "Shop new arrivals",
        FrozenSet<string>.Empty);

    /// <summary>
    /// The HOME-06 banner's words (W1-14), written to match the photograph the client actually sent.
    /// <para>
    /// <b>The picture, the words and the link have to agree, and this is where all three are decided.</b>
    /// The delivered <c>activewear.jpg</c> is a flat-lay of a white linen co-ord and a mid-blue denim
    /// co-ord with a straw basket — nothing athletic — so the slot said "Activewear … Pieces that move
    /// with you" over linen and pointed at <c>/women/activewear</c>, which holds leggings and a sports
    /// bra. That contradiction is issue #63 and is why W1-13 held the section off the front page.
    /// </para>
    /// <para>
    /// The destination is a <b>listing that genuinely contains what the picture shows</b> rather than a
    /// department that happens to include it: <c>/women?material=linen&amp;material=linen-blend&amp;material=denim</c>
    /// returns the relaxed linen-blend shirt, the linen drawstring trousers, both jeans and the denim
    /// wrap skirt — a linen shirt and wide linen trousers and wide-leg denim, which is the flat-lay.
    /// The copy says fabrics, not sets, because the catalogue sells separates and promising a co-ord
    /// would be the same mistake one step further on.
    /// </para>
    /// </summary>
    private static readonly BannerCopy ActivewearCopy = new(
        "Linen and denim",
        "Breathable linen for the heat, washed indigo for everything else.",
        "/women?material=linen&material=linen-blend&material=denim",
        "Shop linen and denim",
        new[]
        {
            BannerCopy.Retracted(nameof(HomeSectionItem.Title), "Activewear"),
            BannerCopy.Retracted(nameof(HomeSectionItem.Subtitle), "Pieces that move with you."),
            BannerCopy.Retracted(nameof(HomeSectionItem.LinkUrl), "/women/activewear"),
            BannerCopy.Retracted(nameof(HomeSectionItem.ButtonLabel), "Shop activewear"),
        }.ToFrozenSet(StringComparer.Ordinal));

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

    /// <summary>
    /// Stand-in ids for files a dry run would store, one per distinct checksum.
    /// <para>
    /// Negative, so they can never collide with a real identity column. They exist so a dry run runs the
    /// <i>same</i> placement logic as the real run: two byte-identical files deduplicate to one asset in
    /// the library, and the only way to predict what that does to the gallery is to let the planner see
    /// them as one asset too.
    /// </para>
    /// </summary>
    private readonly Dictionary<string, int> _plannedAssets = new(StringComparer.Ordinal);

    /// <summary>Whether this attempt may write. A dry run resolves and reports; it never stages a row.</summary>
    private bool _dryRun;

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
        _plannedAssets.Clear();
        _dryRun = request.DryRun;

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
        catch (OperationCanceledException)
        {
            // Ctrl+C. The rows would go anyway when the transaction is disposed, but the files would
            // not: every byte written before the interrupt would stay with no row to find it by. None
            // of this may use the cancelled token.
            await transaction.RollbackAsync(CancellationToken.None);
            await DiscardAsync(run, CancellationToken.None);

            run.Cancel();
            run.Note(
                "One upload may have been part way through when the interrupt arrived; its files are "
                + "left on the volume, with no row anywhere pointing at them. They are not cleaned up "
                + "because the only way to find them is to sweep the asset table, and an upload running "
                + "on another connection at the same moment would be swept up with it — a file nobody "
                + "references is litter, a file somebody else references is data loss.");
            LogFinished(logger, run.Entries.Count, run.Failures.Count, request.DryRun);
            return run.ToReport();
        }
        catch (Exception)
        {
            await transaction.RollbackAsync(CancellationToken.None);
            await DiscardAsync(run, CancellationToken.None);
            throw;
        }

        // Rolled back, so not one row survives. The files do, because storage is not transactional —
        // they are taken back out here, which is the same compensating delete the upload pipeline makes
        // when a rendition fails half way through a set.
        await DiscardAsync(run, cancellationToken);

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

            // Loaded on a dry run as well: the whole point of a plan is that it ran the same placement
            // logic against the same rows. Nothing is saved, so the edits are discarded with the scope.
            var entity = await dbContext.Products.FirstAsync(
                candidate => candidate.Id == product.Id,
                cancellationToken);

            int? cardAssetId = null;
            int? hoverAssetId = null;
            var sawCard = false;

            // Which file put each asset in the gallery, so a second file carrying the same picture can
            // be told what it is the same as rather than just refused.
            var placedBy = new Dictionary<int, string>();

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
                string? sameAs = null;

                if (stored.AssetId is { } assetId)
                {
                    var (mediaRole, sortOrder) = file.Index switch
                    {
                        1 => (ProductMediaRole.Gallery, CardSortOrder),
                        2 => (ProductMediaRole.Hover, HoverSortOrder),
                        _ => (ProductMediaRole.Gallery, (short)(file.Index - 2)),
                    };

                    outcome = PlaceProductMedia(product.Id, assetId, mediaRole, sortOrder, run.Replace);

                    if (outcome == SlotOutcome.AlreadyPlaced)
                    {
                        sameAs = placedBy.GetValueOrDefault(assetId);
                    }
                    else
                    {
                        placedBy[assetId] = file.FileName;
                    }

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

                // The sentence the library holds, which is the one a shopper hears — not the one this
                // file offered. They differ whenever the bytes were already in the library under
                // another product's name, and reporting the offered one then is simply untrue: the
                // kids stand-ins (2026-10-02) make that the normal case rather than a curiosity.
                //
                // A dry run cannot know. It reads the library as it stands, so for bytes that are not in
                // it yet there is no sentence to report and the offered one is a guess — and on a freshly
                // seeded database, which is exactly where the documented two-step order starts, that
                // guess is the kids product's own name on an adult garment's photograph: the one
                // statement this whole arrangement exists to avoid making. So it is marked provisional
                // rather than printed as fact, in the same spirit as this command's standing warning
                // that a plan's "0 refused" means NOT CHECKED.
                var altNote = stored.AltText is { } libraryAlt
                    ? string.Equals(libraryAlt, alt, StringComparison.Ordinal)
                        ? $"alt text \"{libraryAlt}\""
                        : $"alt text \"{libraryAlt}\" — the sentence already on this photograph, kept rather "
                            + $"than overwritten with \"{alt}\", because it is one asset and one sentence, "
                            + "shared with every product that shows it"
                    : _dryRun
                        ? $"alt text \"{alt}\" — PROVISIONAL. These bytes are not in the library yet, so this "
                            + "is what would be written only if they are still absent when the run happens. If "
                            + "another product's photograph turns out to carry them — which is what borrowing a "
                            + "photograph means — that product's sentence is kept and this one is never written."
                        : $"no alt text — the photograph already in the library carries none, and a repeat "
                            + $"upload does not add one. \"{alt}\" was offered and not written.";

                var detail = outcome switch
                {
                    SlotOutcome.AlreadyPlaced =>
                        $"{product.Slug} — byte for byte the same picture as {sameAs ?? "a photograph already in this product's gallery"}, "
                        + $"so it is one asset in the library and fills one gallery slot. It is still {role}.",

                    // Nothing was applied, so nothing was kept and nothing was written: what the alt
                    // text would have been is not this line's business, and saying a sentence was
                    // "kept rather than overwritten" about a file that was refused is simply wrong.
                    // Describe() adds the "the slot already holds a different picture" sentence.
                    SlotOutcome.Conflict => $"{product.Slug} — {role}",

                    _ => $"{product.Slug} — {role}, {altNote}",
                };

                run.Add(Describe(
                    ClientAssetFolder.Products,
                    file.FileName,
                    outcome,
                    stored,
                    detail,
                    file.RepairedFrom));
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
                else
                {
                    ClearWithdrawnHover(entity, run);
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

            var alt = $"{department.Name} at {StoreBrand.Name}";
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
            CampaignCopy,
            files,
            run,
            cancellationToken);
        await ImportBannerAsync(
            HomeSectionType.ActivewearFeature,
            ClientAssetNaming.ActivewearFile,
            ActivewearCopy,
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

        if (_dryRun)
        {
            await PlanHeroAsync(section, videoPath, posterPath, run, cancellationToken);
            return;
        }

        // Asked *before* the upload, because afterwards the two cases are indistinguishable: the poster
        // is stored inside UploadVideoAsync and never surfaces as its own outcome, so if it turns out to
        // be a duplicate of something already in the library there is no later signal saying so — and
        // deleting that file on a rollback would be deleting somebody else's.
        var posterExisted = await IsAlreadyStoredAsync(posterPath, MediaAssetType.Image, cancellationToken);

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

        run.NoteHold(holds.For(dto.Checksum));

        if (outcome is MediaUploadOutcome.Created)
        {
            run.Created(dto.PublicId, dto.StorageKey);

            // The only asset an outcome-derived list cannot see. Its key is read now, while the row is
            // still there, so the rollback needs no query at all.
            if (!posterExisted && dto.PosterPublicId is { } createdPosterId)
            {
                var posterKey = await dbContext.MediaAssets
                    .Where(asset => asset.PublicId == createdPosterId)
                    .Select(asset => asset.StorageKey)
                    .FirstAsync(cancellationToken);

                run.Created(createdPosterId, posterKey);
            }
        }

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

    /// <summary>
    /// The hero, as a dry run sees it: the same slots, filled from resolved identities instead of from
    /// an upload, so the plan and the run agree about what would change.
    /// </summary>
    private async Task PlanHeroAsync(
        HomeSection section,
        string videoPath,
        string posterPath,
        Run run,
        CancellationToken cancellationToken)
    {
        var video = await PlanAsync(videoPath, MediaAssetType.Video, run, cancellationToken);
        var poster = await PlanAsync(posterPath, MediaAssetType.Image, run, cancellationToken);

        var item = await ItemAtAsync(section, sortOrder: 1, cancellationToken);

        var videoSlot = Fill(item.VideoAssetId, video.AssetId!.Value, run.Replace, value => item.VideoAssetId = value, _ => { });
        var posterSlot = Fill(item.ImageAssetId, poster.AssetId!.Value, run.Replace, value => item.ImageAssetId = value, _ => { });

        run.Add(Describe(
            ClientAssetFolder.Home, Path.GetFileName(videoPath), videoSlot, video, "the hero video (HOME-01)"));

        run.Add(Describe(
            ClientAssetFolder.Home,
            Path.GetFileName(posterPath),
            posterSlot,
            poster,
            "the hero's poster and its still for phones and reduced motion"));

        run.WhenDone(section, "HOME-01 hero");
    }

    private async Task ImportBannerAsync(
        HomeSectionType type,
        string contractFile,
        BannerCopy copy,
        IReadOnlyList<string> files,
        Run run,
        CancellationToken cancellationToken)
    {
        var (title, subtitle, linkUrl, buttonLabel, _) = copy;

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

            Reword(item, copy, type, run);

            run.WhenDone(section, type.ToString());
        }

        run.Add(Describe(ClientAssetFolder.Home, Path.GetFileName(path), outcome, stored, $"the {type} banner"));
    }

    /// <summary>
    /// Puts the banner's words in the item, honouring the same "never overwrite a person" rule the
    /// picture slots use.
    /// <para>
    /// <b>Why this is not just <c>??=</c> any more.</b> A re-run is how a corrected delivery reaches the
    /// site, and in W1-14 what was corrected was the <i>copy</i>: the heading, standfirst, button and
    /// link the previous version of this importer wrote for HOME-06 described activewear and the
    /// photograph does not. With <c>??=</c> those fields are already non-null, so a re-import would have
    /// switched the section on and left the contradiction on the front page — the exact failure the hold
    /// existed for (issue #63). A picture, its words and its destination arrive together or not at all.
    /// </para>
    /// <para>
    /// <b>And it is not an unconditional write either.</b> The only values it may overwrite are the ones
    /// it put there itself and has now retracted (<see cref="BannerCopy.Supersedes"/>) — the same
    /// reasoning as "a placeholder was never a choice". Anything else is somebody's editorial decision
    /// in ADM-17 and survives, with a note saying it did, because an import that silently rewrote the
    /// client's own headline would be far worse than one that leaves a stale one.
    /// </para>
    /// </summary>
    private static void Reword(HomeSectionItem item, BannerCopy copy, HomeSectionType type, Run run)
    {
        var kept = new List<string>();

        item.Title = Pick(item.Title, copy.Title, nameof(item.Title), "heading");
        item.Subtitle = Pick(item.Subtitle, copy.Subtitle, nameof(item.Subtitle), "standfirst");
        item.LinkUrl = Pick(item.LinkUrl, copy.LinkUrl, nameof(item.LinkUrl), "link");
        item.ButtonLabel = Pick(item.ButtonLabel, copy.ButtonLabel, nameof(item.ButtonLabel), "button label");

        if (kept.Count > 0)
        {
            run.Note(
                $"{type}: the {Listed(kept)} on that banner "
                + $"{(kept.Count == 1 ? "was" : "were")} left as "
                + $"{(kept.Count == 1 ? "it is" : "they are")} — somebody wrote "
                + $"{(kept.Count == 1 ? "it" : "them")} in ADM-17. Check that the picture, the words and "
                + "the link still agree.");
        }

        // The field is part of the key, not just part of the message. A flat set of the four
        // retracted strings was tested against every field, so an admin who typed "Activewear" as
        // the *button label* had it silently overwritten by a retraction that was only ever about
        // the heading — the exact thing the summary above says must never happen, and with no note
        // either, because the write looked like a retraction rather than a kept value.
        string Pick(string? current, string replacement, string key, string field)
        {
            if (string.IsNullOrWhiteSpace(current)
                || copy.Supersedes.Contains(BannerCopy.Retracted(key, current)))
            {
                return replacement;
            }

            if (current != replacement)
            {
                kept.Add(field);
            }

            return current;
        }
    }

    /// <summary>Names in a sentence: "the heading", "the heading and the link", "a, b and c".</summary>
    private static string Listed(List<string> names) => names.Count switch
    {
        0 => string.Empty,
        1 => names[0],
        _ => $"{string.Join(", ", names.Take(names.Count - 1))} and {names[^1]}",
    };

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
                $"{StoreBrand.Name} clothes, photographed for social media",
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
                $"{StoreBrand.Name} {stem.Replace('-', ' ')}",
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

        if (!_dryRun)
        {
            dbContext.HomeSectionItems.Add(item);

            // Saved now because a MediaUsages row names the item by id (doc 08 §3.3) and a new item has
            // none until it is. The save is inside this run's transaction, so it is still all or nothing.
            await dbContext.SaveChangesAsync(cancellationToken);
        }

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
        if (item.Id == 0 && !_dryRun)
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
        var atSlot = _productMedia.FirstOrDefault(
            row => row.ProductId == productId
                && row.MediaRole == role
                && row.SortOrder == sortOrder
                && row.ProductColourId is null);

        if (atSlot is not null && atSlot.MediaAssetId == assetId)
        {
            return SlotOutcome.Unchanged;
        }

        // A slot holding a picture somebody chose is a conflict whatever else is true, and it is asked
        // first: the operator has to hear "this slot is taken" before anything is said about duplicates.
        if (atSlot is not null && !replace)
        {
            return SlotOutcome.Conflict;
        }

        // The table's unique index is UX_ProductMedia_Product_Asset_Colour — (product, asset, colour) —
        // so one photograph can appear in a product's gallery exactly once. The slot key above is the
        // importer's idea of a position; this is the database's idea of identity, and every write has to
        // satisfy both. Checking it here is what stops two byte-identical files, which deduplicate to
        // one asset, from being inserted twice and failing the index.
        var elsewhere = _productMedia.FirstOrDefault(
            row => row.ProductId == productId
                && row.MediaAssetId == assetId
                && row.ProductColourId is null);

        if (elsewhere is not null)
        {
            // The picture is left where it already is rather than moved: moving it would undo the
            // earlier file's placement on every run, so two runs of the same drop would report work
            // both times. The card and hover pointers are both set either way, so it shows in both
            // places — it simply occupies one row.
            if (atSlot is not null)
            {
                // --replace, and this slot held something the drop no longer sends. One asset is one
                // row, so this slot cannot also hold the picture: the superseded row goes, rather than
                // being left behind pointing at a photograph nobody asked for any more.
                Remove(atSlot);
            }

            return SlotOutcome.AlreadyPlaced;
        }

        if (atSlot is not null)
        {
            atSlot.MediaAssetId = assetId;
            return SlotOutcome.Replaced;
        }

        var newRow = new ProductMedia
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

        if (!_dryRun)
        {
            dbContext.ProductMedia.Add(newRow);
        }

        _productMedia.Add(newRow);
        return SlotOutcome.Filled;
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
            if (!_dryRun)
            {
                dbContext.MediaUsages.Remove(stale);
            }

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

    /// <summary>
    /// Empties the hover slot of a product whose drop sent a card and no second photograph.
    /// <para>
    /// <b>A drop that stops sending a file must not leave the site showing it.</b> The client replaced
    /// <c>poplin-formal-shirt-1</c> with the right garment and withdrew the wrong one, so the product has
    /// one photograph now — and the hover slot still held the withdrawn picture, pointer and gallery row
    /// both, because nothing in the import ever visits a slot the drop has no file for. The card showed
    /// the poplin shirt and flipped, on hover, to the cream tee the client had just rejected. Reading the
    /// code it looks handled; it only ever handled the <i>placeholder</i> case.
    /// </para>
    /// <para>
    /// <b>It is still not an unconditional delete.</b> A hover image that is not a placeholder may be an
    /// admin's own upload (ADM-11) and the importer cannot tell that from its own earlier work — the same
    /// wall every slot in this class runs into. So the rule is the same one: a placeholder goes without
    /// asking, anything else needs <c>--replace</c>, and until then it is a <b>conflict</b> rather than a
    /// note. A note would be read by nobody and the run would exit 0 over a product showing a garment the
    /// client has withdrawn; a conflict names the file the drop no longer contains and exits 3.
    /// </para>
    /// </summary>
    private void ClearWithdrawnHover(Product product, Run run)
    {
        var row = _productMedia.FirstOrDefault(
            candidate => candidate.ProductId == product.Id
                && candidate.MediaRole == ProductMediaRole.Hover
                && candidate.SortOrder == HoverSortOrder
                && candidate.ProductColourId is null);

        if (product.HoverImageAssetId is null && row is null)
        {
            return;
        }

        var placeholder = product.HoverImageAssetId is { } held && _placeholders.Contains(held);

        if (!placeholder && !run.Replace)
        {
            run.Add(new ClientAssetEntry(
                ClientAssetFolder.Products,
                $"{product.Slug}-2",
                ClientAssetOutcome.Conflict,
                $"{product.Slug} — the drop sends one photograph now, and the hover slot still holds the "
                + "one it used to send. The card is the new picture and hovering it still shows the old "
                + "one. It is not a placeholder, so somebody may have chosen it: re-run with --replace to "
                + "clear the slot."));
            return;
        }

        product.HoverImageAssetId = null;

        if (row is not null)
        {
            // **The row may be holding the card's own photograph, and then it must not be deleted.**
            // This is exactly what the poplin correction does: the client promoted the picture that was
            // `-2` to `-1`, so the asset the card now points at is the one already sitting in the hover
            // slot. `PlaceProductMedia` saw it there, left it there (one asset is one row, by
            // UX_ProductMedia_Product_Asset_Colour) and emptied the card slot — so deleting this row
            // deletes the client's only photograph out of the product's gallery, leaving a card with a
            // real garment on it above three demo placeholders. Measured: card 191, gallery 73/74/75.
            // It moves into the card slot the same call has just vacated instead.
            var cardSlotTaken = _productMedia.Any(
                candidate => candidate.ProductId == product.Id
                    && candidate.MediaRole == ProductMediaRole.Gallery
                    && candidate.SortOrder == CardSortOrder
                    && candidate.ProductColourId is null);

            if (row.MediaAssetId != product.CardImageAssetId)
            {
                Remove(row);
            }
            else if (!cardSlotTaken)
            {
                row.MediaRole = ProductMediaRole.Gallery;
                row.SortOrder = CardSortOrder;
            }
            else
            {
                // The card slot is occupied by a different photograph and this row holds the one the
                // card pointer names, so it can neither move nor go: `galleryFor` draws every row
                // whose role is not Video, so deleting it would take the card's own picture out of
                // the gallery — the defect above in a third shape. It is left where it is and said
                // out loud instead, because a Hover row under a null hover pointer is a state only a
                // person can resolve.
                run.Note(
                    $"{product.Slug}: the hover image was cleared, but the gallery row at that slot "
                    + "holds the card's own photograph and the card slot is taken by another, so the "
                    + "row was left where it is rather than deleted. Tidy the gallery in ADM-11.");
            }
        }

        run.Note(
            placeholder
                ? $"{product.Slug}: one photograph was supplied, so the card is the client's and the "
                    + "hover image was cleared rather than left pointing at a placeholder."
                : $"{product.Slug}: one photograph was supplied, so the hover image the drop no longer "
                    + "sends was cleared. The card does not flip.");
    }

    /// <summary>Drops a gallery row, from the context and from this attempt's picture of the table.</summary>
    private void Remove(ProductMedia row)
    {
        if (!_dryRun)
        {
            dbContext.ProductMedia.Remove(row);
        }

        _productMedia.Remove(row);
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

        if (!_dryRun)
        {
            dbContext.MediaUsages.Add(row);
        }

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

        if (!_dryRun)
        {
            dbContext.MediaUsages.Remove(row);
        }

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
        if (_dryRun)
        {
            return await PlanAsync(path, MediaAssetType.Image, run, cancellationToken);
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
                run.NoteHold(holds.For(created.Asset.Checksum));
                run.Created(created.Asset.PublicId, created.Asset.StorageKey);
                return new StoredAsset(
                    await AssetIdAsync(created.Asset.PublicId, cancellationToken),
                    false,
                    null,
                    created.Asset.AltText);

            case MediaUploadOutcome.Duplicate duplicate:
                run.NoteHold(holds.For(duplicate.Asset.Checksum));
                return new StoredAsset(
                    await AssetIdAsync(duplicate.Asset.PublicId, cancellationToken),
                    true,
                    null,
                    duplicate.Asset.AltText);

            case MediaUploadOutcome.Rejected rejected:
                return new StoredAsset(null, false, $"{rejected.Reason}: {rejected.Message}");

            default:
                throw new InvalidOperationException("Unknown upload outcome.");
        }
    }

    /// <summary>
    /// Whether the library already holds these exact bytes as a live, published asset — which is the
    /// same question <c>IMediaService</c>'s deduplication asks, over the same SHA-256 of the same file.
    /// </summary>
    private async Task<bool> IsAlreadyStoredAsync(
        string path,
        MediaAssetType mediaType,
        CancellationToken cancellationToken) =>
        await FindByChecksumAsync(await ChecksumAsync(path, cancellationToken), mediaType, cancellationToken)
            is not null;

    /// <summary>The sentence a dry run would find already on the asset these bytes resolve to.</summary>
    private sealed record ExistingAsset(int Id, string? AltText);

    private static async Task<byte[]> ChecksumAsync(string path, CancellationToken cancellationToken)
    {
        await using var content = File.OpenRead(path);
        return await SHA256.HashDataAsync(content, cancellationToken);
    }

    private async Task<ExistingAsset?> FindByChecksumAsync(
        byte[] checksum,
        MediaAssetType mediaType,
        CancellationToken cancellationToken) =>
        await dbContext.MediaAssets
            .AsNoTracking()
            .Where(asset => asset.Checksum == checksum
                && asset.MediaType == mediaType
                && asset.Visibility == MediaVisibility.Public
                && asset.Status == MediaAssetStatus.Ready)
            .OrderBy(asset => asset.Id)
            .Select(asset => new ExistingAsset(asset.Id, asset.AltText))
            .FirstOrDefaultAsync(cancellationToken);

    /// <summary>
    /// What a dry run would end up with for one file: the asset already in the library that carries
    /// these bytes, or a stand-in identity for the one the real run would create.
    /// <para>
    /// <b>A plan that does not resolve identity is not a plan.</b> The library deduplicates by SHA-256,
    /// so two byte-identical files become one asset and one gallery row — and a dry run that handed each
    /// file a separate nothing reported that pair as a clean green plan, where the real run refused the
    /// second insert on the unique index. The checksum is the same one the pipeline computes, over the
    /// same bytes, so the two agree by construction.
    /// </para>
    /// <para>
    /// It does <b>not</b> re-run the pipeline's validation: a dry run cannot say whether a file would be
    /// refused for its signature, its size or its codec, because answering that means decoding it. It
    /// answers where every file would go.
    /// </para>
    /// </summary>
    private async Task<StoredAsset> PlanAsync(
        string path,
        MediaAssetType mediaType,
        Run run,
        CancellationToken cancellationToken)
    {
        var checksum = await ChecksumAsync(path, cancellationToken);

        run.NoteHold(holds.For(Convert.ToHexStringLower(checksum)));

        var existing = await FindByChecksumAsync(checksum, mediaType, cancellationToken);

        if (existing is { } already)
        {
            return new StoredAsset(already.Id, WasExisting: true, Refusal: null, AltText: already.AltText);
        }

        // One stand-in per distinct checksum, negative so it can never be mistaken for a real id. Two
        // identical files therefore share one, which is exactly what the real run would do with them.
        var key = Convert.ToHexStringLower(checksum);
        if (!_plannedAssets.TryGetValue(key, out var planned))
        {
            planned = -(_plannedAssets.Count + 1);
            _plannedAssets[key] = planned;
        }

        return new StoredAsset(planned, WasExisting: false, Refusal: null);
    }

    /// <summary>
    /// Records a file this attempt's upload created, so a rollback can take it back out.
    /// <para>
    /// <b>Only what the pipeline told <i>this attempt</i> it created ever goes in here.</b> The
    /// previous version asked the database instead — every asset row now, minus a snapshot taken at the
    /// start — which is correct about this attempt's own uncommitted inserts and wrong about everybody
    /// else's: a row another connection committed while the run was in flight is committed and was not
    /// in the snapshot, so a failed import deleted a stranger's file and left their row pointing at
    /// nothing. That end state is the worst one available — the row survives, the bytes do not, there
    /// is nothing to restore from and nothing reports it.
    /// </para>
    /// <para>
    /// A missed file is litter; a deleted file is data loss. So the set is built from outcomes, which
    /// cannot name anything this attempt did not create, and the one asset the outcomes genuinely
    /// cannot see — a video's poster, stored inside <c>UploadVideoAsync</c> — is added deliberately at
    /// the one call site that makes one, and only after checking that it was not already in the library.
    /// </para>
    /// </summary>
    /// <summary>
    /// Takes back out the files of every asset this attempt created, after the transaction rolled its
    /// rows away. Storage is not transactional, so without this a failed run leaves a few hundred files
    /// nothing will ever reference and nothing will ever purge.
    /// </summary>
    private async Task DiscardAsync(Run run, CancellationToken cancellationToken)
    {
        var created = run.CreatedFiles;

        if (created.Count == 0)
        {
            return;
        }

        var removed = 0;

        foreach (var asset in created)
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

        run.Note(
            string.Create(
                CultureInfo.InvariantCulture,
                $"The run was rolled back: no row survives it, and {removed} file(s) belonging to the "
                + $"{created.Count} asset(s) it had created were removed again."));
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
            SlotOutcome.AlreadyPlaced => ClientAssetOutcome.SamePicture,
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

        /// <summary>
        /// This photograph is already in the product's gallery under another slot, and the unique index
        /// (product, asset, colour) lets it appear once. Not an error and not a conflict — the file is in
        /// the library and the pointers still name it — but the report has to say so.
        /// </summary>
        AlreadyPlaced = 3,
        Conflict = 4,
    }

    /// <summary>A file an attempt wrote, as the keys that address it.</summary>
    private sealed record CreatedFile(Guid PublicId, string StorageKey);

    /// <summary>
    /// What the library did with one file.
    /// </summary>
    /// <param name="AltText">
    /// The sentence the library <b>actually holds</b> for this asset, which is not always the one that
    /// was offered. Alt text lives on the asset and the pipeline answers a repeat upload with the asset
    /// it already has, so a file whose bytes are already in the library keeps the earlier sentence — and
    /// the report has to say the one a shopper will hear rather than the one this run asked for. It
    /// matters since the kids stand-ins (2026-10-02), where a photograph is deliberately shared by two
    /// products and only one of them named it.
    /// </param>
    private sealed record StoredAsset(int? AssetId, bool WasExisting, string? Refusal, string? AltText = null);

    /// <summary>
    /// The words one banner carries, and the words this import is allowed to take back.
    /// </summary>
    /// <param name="Supersedes">
    /// Exact <b>(field, value)</b> pairs an <b>earlier version of this importer</b> wrote into the slot
    /// and that are now wrong, built with <see cref="Retracted"/>. They are the only stored copy
    /// <see cref="Reword"/> may overwrite: everything else is treated as somebody's editorial decision.
    /// Keyed on the literal strings rather than on a flag or a version number for the reason
    /// <see cref="ClientAssetHolds"/> is keyed on a checksum — it expires by itself. Once the client
    /// edits the heading in ADM-17 the old string is gone, and so is this entry's power over it.
    /// <para>
    /// <b>The field belongs in the key.</b> A flat set of values was tested against every field, so a
    /// retraction of one field reached across to another that happened to hold the same words — an admin
    /// button label of "Activewear" was overwritten by the heading's retraction, silently, because the
    /// write looked like a retraction and not like a value being kept.
    /// </para>
    /// </param>
    private sealed record BannerCopy(
        string Title,
        string Subtitle,
        string LinkUrl,
        string ButtonLabel,
        IReadOnlySet<string> Supersedes)
    {
        /// <summary>One retracted value, scoped to the field it was written into.</summary>
        /// <remarks>
        /// <c>\0</c> cannot appear in either half: <c>HomeSectionItem</c>'s names are C# identifiers and
        /// the stored copy comes from a form, so there is no pair of different (field, value) inputs that
        /// can collide on one key.
        /// </remarks>
        public static string Retracted(string field, string value) => $"{field}\0{value}";
    }

    /// <summary>Everything one attempt accumulates, so the methods above stay about the import.</summary>
    private sealed class Run(ClientAssetImportRequest request)
    {
        private readonly HashSet<string> _touched = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<HomeSectionType, string> _holds = [];
        private readonly List<(HomeSection Section, string Label)> _pending = [];
        private readonly List<CreatedFile> _created = [];
        public bool DryRun { get; } = request.DryRun;

        public bool Replace { get; } = request.Replace;

        public List<ClientAssetEntry> Entries { get; } = [];

        public List<string> Failures { get; } = [];

        public List<string> Notes { get; } = [];

        /// <summary>
        /// The files this attempt's own uploads created. Nothing another connection wrote can reach this
        /// list, which is the whole point of building it from outcomes rather than from the table.
        /// </summary>
        public List<CreatedFile> CreatedFiles => _created;

        /// <summary>Sections to consider switching on, once the run is known to have succeeded.</summary>
        public List<(HomeSection Section, string Label)> Pending => _pending;

        public bool Succeeded => Failures.Count == 0;

        /// <summary>Whether the operator interrupted the run.</summary>
        public bool Cancelled { get; private set; }

        public void Cancel()
        {
            Cancelled = true;
            Fail("The import was cancelled. The whole run was rolled back and the files it had written were removed.");
        }

        public void Add(ClientAssetEntry entry) => Entries.Add(entry);

        public void Fail(string reason) => Failures.Add(reason);

        public void Note(string note) => Notes.Add(note);

        public void Created(Guid publicId, string storageKey) => _created.Add(new CreatedFile(publicId, storageKey));

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

        public ClientAssetImportReport ToReport() => new(Entries, Failures, Notes, Cancelled);
    }

    [LoggerMessage(
        EventId = 1401,
        Level = LogLevel.Information,
        Message = "Client asset import finished: {Files} files, {Failures} failures (dry run: {DryRun}).")]
    private static partial void LogFinished(ILogger logger, int files, int failures, bool dryRun);
}
