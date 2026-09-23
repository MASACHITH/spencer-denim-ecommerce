using SpencerDenim.Domain.Catalog;

namespace SpencerDenim.Infrastructure.Persistence.Seeding;

/// <summary>
/// How much stock a demo product carries, so the seeded catalogue exercises every storefront state:
/// the in-stock path, the Low Stock badge, a sold-out product and a product where only some sizes are
/// gone.
/// </summary>
internal enum StockProfile
{
    /// <summary>Plenty of every size.</summary>
    Healthy = 0,

    /// <summary>Below the low-stock threshold on every variant: the card shows the Low Stock badge.</summary>
    Low = 1,

    /// <summary>Nothing left: the card and the product page show "Out of stock".</summary>
    OutOfStock = 2,

    /// <summary>Some sizes sold out, the rest healthy.</summary>
    Mixed = 3,
}

/// <summary>One colourway of a demo product.</summary>
internal sealed record DemoColour(string Name, string FamilySlug, string Hex);

/// <summary>
/// One demo product. All copy is written for this project; nothing is taken from the old prototype's
/// catalogue.
/// </summary>
internal sealed record DemoProductSeed(
    string Code,
    string Name,
    string Slug,
    string DepartmentSlug,
    string CategorySlug,
    string? AgeGroupSlug,
    string ShortDescription,
    string Description,
    string CareInstructions,
    string MaterialSlug,
    string FitSlug,
    string OccasionSlug,
    decimal BasePrice,
    decimal? CompareAtPrice,
    ProductBadge? BadgeOverride,
    int? RecommendedRank,
    string SizeScale,
    string[] SizeLabels,
    DemoColour[] Colours,
    StockProfile Stock,
    decimal? Rating,
    int RatingCount,
    int SoldQty30d,
    int SoldQtyTotal,
    int PublishedDaysAgo);

/// <summary>
/// The dev and staging sample catalogue (doc 08 §9, "Sample catalogue"). Prices are in LKR and are
/// placeholders until the client supplies the real price list.
/// </summary>
internal static class DemoCatalogueData
{
    private static readonly DemoColour Beige = new("Sandstone Beige", "beige", "#D8CBB6");
    private static readonly DemoColour White = new("Optic White", "white", "#FFFFFF");
    private static readonly DemoColour Cream = new("Coconut Cream", "cream", "#F3EADA");
    private static readonly DemoColour Navy = new("Deep Navy", "navy", "#1F2A44");
    private static readonly DemoColour Black = new("True Black", "black", "#101010");
    private static readonly DemoColour Charcoal = new("Charcoal Grey", "grey", "#4A4D52");
    private static readonly DemoColour Indigo = new("Indigo Rinse", "blue", "#3A5A8C");
    private static readonly DemoColour MidBlue = new("Mid Wash Blue", "blue", "#6E93C4");
    private static readonly DemoColour Olive = new("Olive Green", "green", "#5C6B4A");
    private static readonly DemoColour Terracotta = new("Terracotta", "orange", "#C4703F");
    private static readonly DemoColour Blush = new("Blush Pink", "pink", "#E8A0BF");
    private static readonly DemoColour Maroon = new("Deep Maroon", "red", "#7E2B2B");
    private static readonly DemoColour Mustard = new("Mustard", "yellow", "#D9A93C");
    private static readonly DemoColour Plum = new("Plum", "purple", "#6C4A8F");
    private static readonly DemoColour Chocolate = new("Chocolate", "brown", "#5B3F2E");
    private static readonly DemoColour Teal = new("Deep Teal", "green", "#2E6B66");

    private static readonly string[] AlphaCore = ["XS", "S", "M", "L", "XL"];
    private static readonly string[] AlphaFull = ["XS", "S", "M", "L", "XL", "XXL"];
    private static readonly string[] MenAlpha = ["S", "M", "L", "XL", "XXL"];
    private static readonly string[] WomenWaist = ["26", "28", "30", "32", "34"];
    private static readonly string[] MenWaist = ["30", "32", "34", "36", "38"];
    private static readonly string[] KidsAges = ["3-4Y", "5-6Y", "7-8Y", "9-10Y", "11-12Y"];
    private static readonly string[] BabyMonths = ["0-3M", "3-6M", "6-9M", "9-12M", "12-18M"];
    private static readonly string[] WomenShoes = ["36", "37", "38", "39", "40", "41"];
    private static readonly string[] MenShoes = ["40", "41", "42", "43", "44", "45"];
    private static readonly string[] KidsShoes = ["35", "36", "37", "38"];
    private static readonly string[] OneSize = ["One Size"];

    private const string CottonCare = "Machine wash cold on a gentle cycle with like colours. Do not bleach. Dry in shade. Warm iron.";
    private const string LinenCare = "Machine wash cold, gentle cycle. Reshape while damp and dry flat in shade. Iron on medium while slightly damp.";
    private const string DenimCare = "Wash inside out, cold, with like colours. Do not soak. Line dry in shade to keep the wash and the fit.";
    private const string DelicateCare = "Hand wash cold or dry clean. Do not wring. Dry flat in shade. Cool iron on the reverse.";
    private const string ActiveCare = "Machine wash cold with like colours. No fabric softener, it coats the fibres. Do not tumble dry.";
    private const string KidsCare = "Machine wash warm with like colours. Do not bleach. Tumble dry low. Warm iron if needed.";

    public static IReadOnlyList<DemoProductSeed> Products { get; } =
    [
        // ---------------------------------------------------------------- Women
        new("SD-W-0001", "Relaxed Linen Blend Shirt", "relaxed-linen-blend-shirt", "women", "shirts-blouses", null,
            "An easy shirt in a breathable linen blend, cut long enough to wear open over a tee.",
            "<p>Our most forgiving shirt shape, in a linen and cotton blend that stays cool through a Colombo afternoon. The shoulder drops a little past the natural line and the body skims rather than clings, so it works buttoned at the office and open over a camisole at the weekend.</p><p>Finished with a soft-fused collar, a curved hem and shell-look buttons.</p>",
            LinenCare, "linen-blend", "relaxed", "smart-casual", 5490m, 6990m, null, 1,
            CatalogReferenceData.AlphaScale, AlphaCore, [Beige, White, Navy], StockProfile.Healthy, 4.6m, 128, 64, 940, 120),

        new("SD-W-0002", "Poplin Point-Collar Shirt", "poplin-point-collar-shirt", "women", "shirts-blouses", null,
            "Crisp cotton poplin with a clean point collar — the shirt the rest of the wardrobe answers to.",
            "<p>A properly structured shirt in tightly woven cotton poplin. The collar holds its point without starching, the placket sits flat under a blazer and the cuffs take a link if the evening asks for it.</p><p>Cut with a gentle waist shape so it stays tidy tucked in.</p>",
            CottonCare, "cotton", "regular", "formal", 4790m, null, null, null,
            CatalogReferenceData.AlphaScale, AlphaCore, [White, MidBlue], StockProfile.Healthy, 4.4m, 71, 31, 505, 200),

        new("SD-W-0003", "Cropped Cotton Camisole", "cropped-cotton-camisole", "women", "tops-t-shirts", null,
            "A fine-gauge cotton camisole with adjustable straps, made to layer.",
            "<p>The layering piece we reach for most. Fine-gauge cotton jersey with a bound neckline and straps that adjust, cropped to sit just at the waistband.</p>",
            CottonCare, "100-cotton", "slim", "casual", 2290m, null, null, null,
            CatalogReferenceData.AlphaScale, AlphaCore, [White, Black, Blush], StockProfile.Healthy, 4.2m, 54, 40, 610, 150),

        new("SD-W-0004", "Ribbed Scoop-Neck Tee", "ribbed-scoop-neck-tee", "women", "tops-t-shirts", null,
            "A stretch rib tee that holds its shape wash after wash.",
            "<p>Cut from a cotton-rich rib with just enough stretch to follow the body without pulling. The scoop neck is bound, not folded, so it will not ripple after the first wash.</p>",
            CottonCare, "cotton-blend", "slim", "casual", 1990m, 2590m, null, null,
            CatalogReferenceData.AlphaScale, AlphaCore, [Black, Cream, Olive], StockProfile.Low, 4.5m, 96, 52, 820, 90),

        new("SD-W-0005", "Drape Viscose Blouse", "drape-viscose-blouse", "women", "shirts-blouses", null,
            "A fluid blouse with a tie neck, for days that move from desk to dinner.",
            "<p>Viscose with a soft, liquid drape and a neck tie you can knot loose or leave open. The sleeve gathers into a narrow cuff, which keeps the volume off the wrist.</p>",
            DelicateCare, "viscose", "regular", "smart-casual", 5290m, null, null, null,
            CatalogReferenceData.AlphaScale, AlphaCore, [Cream, Plum], StockProfile.Mixed, 4.1m, 38, 18, 240, 260),

        new("SD-W-0006", "Tiered Midi Dress", "tiered-midi-dress", "women", "dresses", null,
            "Three soft tiers, a shirred bodice and pockets where you actually want them.",
            "<p>A dress built for warm evenings: a shirred bodice that adjusts to you, three gathered tiers that move, and deep side pockets set into the first seam. The midi length sits at mid-calf on most heights.</p>",
            DelicateCare, "viscose", "relaxed", "party", 8490m, 10990m, null, 2,
            CatalogReferenceData.AlphaScale, AlphaCore, [Maroon, Navy, Mustard], StockProfile.Healthy, 4.7m, 143, 71, 1120, 45),

        new("SD-W-0007", "Belted Shirt Dress", "belted-shirt-dress", "women", "dresses", null,
            "A cotton shirt dress with a self belt, so the waist is wherever you decide.",
            "<p>Shirting details on a dress that works all week: a full button placket, a soft collar and a detachable self belt. Leave it open over trousers on the days it is not a dress.</p>",
            CottonCare, "cotton", "regular", "smart-casual", 7990m, null, null, null,
            CatalogReferenceData.AlphaScale, AlphaFull, [Beige, Indigo], StockProfile.Healthy, 4.3m, 62, 27, 410, 175),

        new("SD-W-0008", "Sleeveless Column Dress", "sleeveless-column-dress", "women", "dresses", null,
            "A clean column line with a concealed back zip, for occasions that need no explanation.",
            "<p>A narrow, unbroken line from shoulder to hem in a substantial crepe that holds the shape. Fully lined, with a concealed centre-back zip and a walking vent.</p>",
            DelicateCare, "polyester-blend", "slim", "formal", 9490m, null, null, null,
            CatalogReferenceData.AlphaScale, AlphaCore, [Black, Teal], StockProfile.OutOfStock, 4.5m, 44, 9, 330, 300),

        new("SD-W-0009", "High-Rise Slim Fit Jeans", "high-rise-slim-fit-jeans", "women", "jeans", null,
            "High-rise denim with real stretch recovery, so they still fit at the end of the day.",
            "<p>A high rise that sits at the natural waist and a slim leg that tapers gently to the ankle. The denim has enough elastane to move and enough cotton to keep its shape.</p>",
            DenimCare, "denim", "slim", "casual", 7490m, null, null, 3,
            CatalogReferenceData.WomenWaistScale, WomenWaist, [Indigo, Black], StockProfile.Healthy, 4.6m, 187, 88, 1580, 210),

        new("SD-W-0010", "Wide-Leg Rinse Jeans", "wide-leg-rinse-jeans", "women", "jeans", null,
            "A rigid rinse wash with a full, straight-falling wide leg.",
            "<p>Cut from a rigid denim that breaks in rather than gives out. The leg falls straight from the hip with no taper, which is what gives the shape its length.</p>",
            DenimCare, "denim", "wide-leg", "casual", 8290m, 9990m, null, null,
            CatalogReferenceData.WomenWaistScale, WomenWaist, [MidBlue, Cream], StockProfile.Mixed, 4.4m, 77, 45, 690, 60),

        new("SD-W-0011", "Pleated Tailored Trousers", "pleated-tailored-trousers", "women", "trousers", null,
            "Front pleats, a pressed crease and a leg that falls clean to the shoe.",
            "<p>Tailoring cloth with a dry hand and a matte finish. Two front pleats release into a straight leg, and the crease is set so it survives the commute.</p>",
            DelicateCare, "polyester-blend", "regular", "formal", 6890m, null, null, null,
            CatalogReferenceData.AlphaScale, AlphaCore, [Charcoal, Black, Beige], StockProfile.Healthy, 4.2m, 51, 22, 360, 190),

        new("SD-W-0012", "Linen Drawstring Trousers", "linen-drawstring-trousers", "women", "trousers", null,
            "Pure linen with a covered elastic waist and a drawstring you can actually tie.",
            "<p>The trouser for the hottest weeks of the year. Pure linen, a covered elastic waist with a flat drawstring, and a wide straight leg that keeps the air moving.</p>",
            LinenCare, "linen", "relaxed", "casual", 5990m, null, null, null,
            CatalogReferenceData.AlphaScale, AlphaCore, [White, Olive, Terracotta], StockProfile.Healthy, 4.5m, 89, 57, 720, 75),

        new("SD-W-0013", "Bias-Cut Midi Skirt", "bias-cut-midi-skirt", "women", "skirts", null,
            "Cut on the bias so it swings, with a flat front waistband.",
            "<p>Cutting on the bias is what makes a simple skirt move. Lined to the knee, with a flat front waistband and a concealed side zip.</p>",
            DelicateCare, "viscose", "regular", "smart-casual", 5490m, null, null, null,
            CatalogReferenceData.AlphaScale, AlphaCore, [Plum, Charcoal], StockProfile.Mixed, 4.0m, 29, 14, 205, 230),

        new("SD-W-0014", "Denim Wrap Mini Skirt", "denim-wrap-mini-skirt", "women", "skirts", null,
            "A true wrap in washed denim, with a hidden press stud for peace of mind.",
            "<p>A wrap front in a softened, washed denim, fastened with a hidden press stud behind the tie so nothing opens when it should not.</p>",
            DenimCare, "denim", "regular", "casual", 4690m, null, null, null,
            CatalogReferenceData.AlphaScale, AlphaCore, [MidBlue], StockProfile.Healthy, null, 0, 12, 95, 20),

        new("SD-W-0015", "Kandyan Osariya (ඔසරිය) Handloom Saree", "kandyan-osariya-handloom-saree", "women", "traditional-wear", null,
            "Handloom cotton woven in the Kandyan tradition, with a contrast border and a matching blouse piece.",
            "<p>Woven on a handloom by a workshop we have bought from for years, in the Kandyan osariya tradition. The cotton is light enough for a full day and the contrast border is woven in, not applied.</p><p>Supplied with an unstitched blouse piece.</p>",
            DelicateCare, "cotton", "regular", "traditional", 18900m, 22900m, null, null,
            CatalogReferenceData.OneSizeScale, OneSize, [Maroon, Cream, Teal], StockProfile.Low, 4.8m, 33, 11, 180, 150),

        new("SD-W-0016", "Kanjivaram (காஞ்சிபுரம்) Silk-Blend Saree", "kanjivaram-silk-blend-saree", "women", "traditional-wear", null,
            "A silk-blend saree with a woven zari border, in the Kanjivaram idiom.",
            "<p>A silk-blend take on the Kanjivaram, with a woven zari border and pallu. The blend keeps the drape and the shine while making the price and the care realistic for regular wear.</p><p>Supplied with an unstitched blouse piece.</p>",
            DelicateCare, "polyester-blend", "regular", "traditional", 24900m, null, ProductBadge.OnlineExclusive, null,
            CatalogReferenceData.OneSizeScale, OneSize, [Maroon, Mustard], StockProfile.Healthy, 4.9m, 21, 7, 96, 110),

        new("SD-W-0017", "Seamless Studio Leggings", "seamless-studio-leggings", "women", "activewear", null,
            "Seamless, squat-proof and high-rise, with a hidden waistband pocket.",
            "<p>Knitted in the round so there is no inner-leg seam to rub. The high waistband stays put through a full class and hides a flat pocket at the back for a key or a card.</p>",
            ActiveCare, "polyester-blend", "slim", "active", 4290m, null, null, null,
            CatalogReferenceData.AlphaScale, AlphaCore, [Black, Teal, Plum], StockProfile.Healthy, 4.6m, 112, 63, 880, 55),

        new("SD-W-0018", "Everyday Canvas Tote", "everyday-canvas-tote", "women", "accessories", null,
            "Heavy cotton canvas, a flat base and straps long enough for the shoulder.",
            "<p>A tote that stands up on its own. Heavy cotton canvas with a boxed flat base, an internal slip pocket and webbing straps long enough to clear a jacket.</p>",
            CottonCare, "cotton", "regular", "casual", 3290m, null, null, null,
            CatalogReferenceData.OneSizeScale, OneSize, [Beige, Black], StockProfile.Healthy, 4.3m, 47, 34, 520, 140),

        new("SD-W-0019", "Woven Slide Sandals", "woven-slide-sandals", "women", "footwear", null,
            "Woven uppers on a cushioned footbed, for long days on hard floors.",
            "<p>A wide woven upper spreads the pressure across the foot, and the moulded footbed does the rest. The outsole is siped for grip on wet tile.</p>",
            "Wipe clean with a damp cloth. Do not machine wash. Dry away from direct heat.",
            "cotton-blend", "regular", "casual", 5690m, null, null, null,
            CatalogReferenceData.FootwearScale, WomenShoes, [Chocolate, Black], StockProfile.Mixed, 4.1m, 36, 19, 275, 165),

        // ---------------------------------------------------------------- Men
        new("SD-M-0001", "Washed Linen Camp Shirt", "washed-linen-camp-shirt", "men", "shirts", null,
            "A garment-washed linen camp collar, soft from the first wear.",
            "<p>Pure linen, washed after making so it arrives soft instead of stiff. The camp collar sits flat open, the body is cut straight and the hem is short enough to wear out.</p>",
            LinenCare, "linen", "relaxed", "casual", 5990m, 7490m, ProductBadge.BestSeller, 1,
            CatalogReferenceData.AlphaScale, MenAlpha, [Cream, Olive, Navy], StockProfile.Healthy, 4.7m, 164, 94, 1420, 100),

        new("SD-M-0002", "Oxford Button-Down Shirt", "oxford-button-down-shirt", "men", "shirts", null,
            "A proper oxford cloth button-down with a collar that rolls.",
            "<p>Woven oxford cotton with enough body to hold a roll in the collar. Single-needle side seams, a box pleat at the back and a flat-felled armhole.</p>",
            CottonCare, "100-cotton", "regular", "smart-casual", 5290m, null, null, 4,
            CatalogReferenceData.AlphaScale, MenAlpha, [White, MidBlue, Blush], StockProfile.Healthy, 4.5m, 132, 58, 990, 240),

        new("SD-M-0003", "Poplin Formal Shirt", "poplin-formal-shirt", "men", "shirts", null,
            "Fine poplin, a spread collar and a clean placket.",
            "<p>A shirt for meetings and weddings. Fine cotton poplin with a spread collar, removable collar bones and a placket pressed flat enough to disappear under a tie.</p>",
            CottonCare, "cotton", "slim", "formal", 5890m, null, null, null,
            CatalogReferenceData.AlphaScale, MenAlpha, [White, Charcoal], StockProfile.Mixed, 4.2m, 58, 24, 470, 280),

        new("SD-M-0004", "Heavyweight Crew Tee", "heavyweight-crew-tee", "men", "t-shirts-polos", null,
            "A 220 gsm cotton tee with a ribbed collar that will not stretch out.",
            "<p>Heavier cotton than the usual tee, so it hangs instead of clinging. The collar is a double-thickness rib taped across the shoulders.</p>",
            CottonCare, "100-cotton", "regular", "casual", 2490m, null, null, null,
            CatalogReferenceData.AlphaScale, MenAlpha, [White, Black, Olive, Navy], StockProfile.Healthy, 4.6m, 201, 106, 1890, 220),

        new("SD-M-0005", "Piqué Polo Shirt", "pique-polo-shirt", "men", "t-shirts-polos", null,
            "Classic cotton piqué with a two-button placket and ribbed cuffs.",
            "<p>Cotton piqué with the open texture that keeps a polo cool. Two-button placket, ribbed collar and cuffs, and a slightly longer back hem.</p>",
            CottonCare, "cotton-blend", "regular", "smart-casual", 3890m, 4690m, null, null,
            CatalogReferenceData.AlphaScale, MenAlpha, [Navy, White, Maroon], StockProfile.Low, 4.4m, 118, 49, 1040, 190),

        new("SD-M-0006", "Oversized Graphic Tee", "oversized-graphic-tee", "men", "t-shirts-polos", null,
            "A boxy, dropped-shoulder tee with a screen print that softens with washing.",
            "<p>Deliberately boxy, with a dropped shoulder and a wide body. The print is water-based, so it sits in the fabric rather than on top of it.</p>",
            CottonCare, "cotton", "oversized", "casual", 2890m, null, null, null,
            CatalogReferenceData.AlphaScale, MenAlpha, [Black, Cream], StockProfile.OutOfStock, 4.0m, 27, 5, 210, 80),

        new("SD-M-0007", "Straight Fit Rigid Jeans", "straight-fit-rigid-jeans", "men", "jeans", null,
            "Rigid denim, a straight leg and a button fly.",
            "<p>Unwashed rigid denim that fades where you wear it. Straight from hip to hem, with a button fly and copper rivets at the stress points.</p>",
            DenimCare, "denim", "regular", "casual", 7990m, null, null, 2,
            CatalogReferenceData.MenWaistScale, MenWaist, [Indigo], StockProfile.Healthy, 4.5m, 96, 51, 830, 160),

        new("SD-M-0008", "Slim Stretch Jeans", "slim-stretch-jeans", "men", "jeans", null,
            "A slim leg with enough stretch for a full day on your feet.",
            "<p>A slim leg through the thigh and knee with a small taper below. The denim carries a little elastane, which is what keeps the knees from bagging.</p>",
            DenimCare, "denim", "slim", "casual", 7690m, 8990m, null, null,
            CatalogReferenceData.MenWaistScale, MenWaist, [Black, MidBlue], StockProfile.Healthy, 4.3m, 84, 47, 760, 130),

        new("SD-M-0009", "Relaxed Carpenter Jeans", "relaxed-carpenter-jeans", "men", "jeans", null,
            "A roomy leg with a hammer loop and a wide utility pocket.",
            "<p>Built on a workwear block: a roomy seat and thigh, a straight leg and the details that come with the name — a hammer loop, a rule pocket and bar-tacked seams.</p>",
            DenimCare, "denim", "relaxed", "casual", 8490m, null, null, null,
            CatalogReferenceData.MenWaistScale, MenWaist, [MidBlue, Chocolate], StockProfile.Mixed, 4.2m, 41, 23, 320, 70),

        new("SD-M-0010", "Cotton Twill Chinos", "cotton-twill-chinos", "men", "trousers", null,
            "Peached cotton twill with a clean slim leg and a hidden stretch waistband.",
            "<p>Cotton twill peached for a softer hand, cut slim without being tight. The inner waistband has a hidden stretch panel, which is the part you notice after lunch.</p>",
            CottonCare, "cotton", "slim", "smart-casual", 6290m, null, null, null,
            CatalogReferenceData.MenWaistScale, MenWaist, [Beige, Navy, Olive], StockProfile.Healthy, 4.4m, 107, 55, 940, 205),

        new("SD-M-0011", "Tailored Wool-Look Trousers", "tailored-wool-look-trousers", "men", "trousers", null,
            "Matte tailoring cloth with a pressed crease and a clean break.",
            "<p>A wool-look blend with a dry, matte finish that photographs well and travels better. Half lined to the knee, with a set crease and a plain hem.</p>",
            DelicateCare, "polyester-blend", "regular", "formal", 7890m, null, null, null,
            CatalogReferenceData.MenWaistScale, MenWaist, [Charcoal, Black], StockProfile.Healthy, 4.1m, 39, 17, 290, 250),

        new("SD-M-0012", "Linen Drawstring Shorts", "linen-drawstring-shorts", "men", "shorts", null,
            "Pure linen shorts with an elastic back and a flat drawstring.",
            "<p>Cut just above the knee in pure linen, with an elastic back waist so one size covers a slightly bigger day. Deep side pockets and a back patch pocket.</p>",
            LinenCare, "linen", "relaxed", "casual", 4290m, null, null, null,
            CatalogReferenceData.AlphaScale, MenAlpha, [Cream, Teal], StockProfile.Healthy, 4.5m, 73, 44, 610, 65),

        new("SD-M-0013", "Chino Shorts", "chino-shorts", "men", "shorts", null,
            "Cotton chino shorts with a 7-inch inseam and a clean flat front.",
            "<p>A flat front, a seven-inch inseam and a slim-but-not-tight leg. The twill has been washed so the colour settles on the first wear rather than the fifth.</p>",
            CottonCare, "cotton", "regular", "casual", 3990m, 4790m, null, null,
            CatalogReferenceData.MenWaistScale, MenWaist, [Beige, Navy], StockProfile.Mixed, 4.2m, 65, 38, 540, 95),

        new("SD-M-0014", "Training Performance Tee", "training-performance-tee", "men", "activewear", null,
            "A quick-drying training tee with mesh panels where the heat builds.",
            "<p>A light knit that moves the sweat instead of holding it, with mesh under the arms and down the centre back. Flat seams so a backpack strap has nothing to catch.</p>",
            ActiveCare, "polyester-blend", "slim", "active", 3290m, null, null, null,
            CatalogReferenceData.AlphaScale, MenAlpha, [Black, Teal], StockProfile.Healthy, 4.3m, 58, 41, 470, 50),

        new("SD-M-0015", "Leather-Look Belt", "leather-look-belt", "men", "accessories", null,
            "A 35 mm belt with a brushed metal buckle, in a wipe-clean finish.",
            "<p>A 35 mm width that fits both a suit loop and a jean loop, with a brushed metal pin buckle and five holes.</p>",
            "Wipe clean with a damp cloth. Do not soak. Store hanging or rolled, not folded.",
            "polyester-blend", "regular", "smart-casual", 3490m, null, null, null,
            CatalogReferenceData.OneSizeScale, OneSize, [Black, Chocolate], StockProfile.Healthy, 4.0m, 44, 26, 380, 215),

        new("SD-M-0016", "Court Canvas Sneakers", "court-canvas-sneakers", "men", "footwear", null,
            "A low court profile in cotton canvas on a vulcanised rubber sole.",
            "<p>A clean court shape in a heavy cotton canvas, built on a vulcanised sole so it flexes from the first day. Cotton laces and a padded collar.</p>",
            "Wipe clean or hand wash the uppers. Air dry away from direct sun. Do not machine wash.",
            "cotton-blend", "regular", "casual", 7290m, null, null, null,
            CatalogReferenceData.FootwearScale, MenShoes, [White, Navy], StockProfile.Healthy, 4.4m, 88, 52, 690, 115),

        // ---------------------------------------------------------------- Kids
        new("SD-K-0001", "Girls Cotton Sundress", "girls-cotton-sundress", "kids", "girls-dresses", "kids",
            "A light cotton sundress with a shirred back, so it grows with her.",
            "<p>Pure cotton with a shirred back panel that stretches, which buys another season. Adjustable ties at the shoulder and a hem deep enough to let down.</p>",
            KidsCare, "100-cotton", "relaxed", "casual", 2990m, null, null, null,
            CatalogReferenceData.KidsAgeScale, KidsAges, [Blush, Mustard, White], StockProfile.Healthy, 4.6m, 52, 36, 430, 85),

        new("SD-K-0002", "Girls Printed Tee", "girls-printed-tee", "kids", "girls-tops", "kids",
            "Soft cotton jersey with a print that survives the wash basket.",
            "<p>Cotton jersey with a slightly wider neck opening, so it goes over a head without a fight. The print is water-based and will not crack.</p>",
            KidsCare, "cotton", "regular", "casual", 1490m, 1890m, null, null,
            CatalogReferenceData.KidsAgeScale, KidsAges, [White, Blush], StockProfile.Low, 4.4m, 61, 44, 690, 70),

        new("SD-K-0003", "Girls Jersey Leggings", "girls-jersey-leggings", "kids", "girls-bottoms", "kids",
            "Stretch cotton leggings with a soft covered waistband.",
            "<p>Cotton-rich jersey with a flat covered elastic waistband that does not mark. Flatlock seams inside the leg.</p>",
            KidsCare, "cotton-blend", "slim", "casual", 1690m, null, null, null,
            CatalogReferenceData.KidsAgeScale, KidsAges, [Black, Navy, Plum], StockProfile.Healthy, 4.5m, 74, 51, 820, 105),

        new("SD-K-0004", "Boys Checked Shirt", "boys-checked-shirt", "kids", "boys-shirts-t-shirts", "kids",
            "A yarn-dyed cotton check with a button-down collar.",
            "<p>Yarn-dyed cotton, so the check runs through the cloth rather than printed on it. Button-down collar points keep it tidy without ironing.</p>",
            KidsCare, "cotton", "regular", "smart-casual", 2490m, null, null, null,
            CatalogReferenceData.KidsAgeScale, KidsAges, [MidBlue, Maroon], StockProfile.Mixed, 4.2m, 33, 19, 280, 130),

        new("SD-K-0005", "Boys Cotton Tee Two-Pack", "boys-cotton-tee-two-pack", "kids", "boys-shirts-t-shirts", "kids",
            "Two plain cotton tees, because one is never enough.",
            "<p>Two plain crew-neck tees in pure cotton, taped across the shoulders so the neckline stays flat. Sold as a pack.</p>",
            KidsCare, "100-cotton", "regular", "casual", 1990m, null, null, null,
            CatalogReferenceData.KidsAgeScale, KidsAges, [White, Navy], StockProfile.Healthy, 4.7m, 97, 68, 1130, 60),

        new("SD-K-0006", "Boys Pull-On Shorts", "boys-pull-on-shorts", "kids", "boys-bottoms", "kids",
            "Elastic-waist shorts with real pockets and a drawcord.",
            "<p>A soft cotton blend with an elastic waist and an internal drawcord for the days it needs pulling in. Two side pockets deep enough to keep things.</p>",
            KidsCare, "cotton-blend", "relaxed", "casual", 1890m, 2290m, null, null,
            CatalogReferenceData.KidsAgeScale, KidsAges, [Olive, Charcoal], StockProfile.Healthy, 4.3m, 45, 30, 510, 95),

        new("SD-K-0007", "Baby Ribbed Bodysuit Set", "baby-ribbed-bodysuit-set", "kids", "baby-bodysuits-sets", "baby",
            "Two ribbed cotton bodysuits with envelope necks and popper gussets.",
            "<p>Ribbed pure cotton with envelope shoulders, so a bodysuit comes off downwards when it has to. Poppers along the gusset and down one leg.</p>",
            KidsCare, "100-cotton", "relaxed", "casual", 2790m, null, ProductBadge.New, null,
            CatalogReferenceData.BabyMonthsScale, BabyMonths, [Cream, Blush, Teal], StockProfile.Healthy, 4.8m, 39, 28, 310, 15),

        new("SD-K-0008", "Baby Muslin Bib Set", "baby-muslin-bib-set", "kids", "baby-accessories", "baby",
            "Three layers of cotton muslin, with a popper neck.",
            "<p>Triple-layer cotton muslin that gets softer and more absorbent with every wash. A popper at the neck rather than a tie.</p>",
            KidsCare, "cotton", "regular", "casual", 1290m, null, null, null,
            CatalogReferenceData.OneSizeScale, OneSize, [Cream, Mustard], StockProfile.Healthy, null, 0, 22, 240, 40),

        new("SD-K-0009", "Kids Canvas Sneakers", "kids-canvas-sneakers", "kids", "kids-footwear", "kids",
            "Canvas sneakers with a hook-and-loop strap, so they go on without help.",
            "<p>A canvas upper on a flexible rubber sole, closed with a wide hook-and-loop strap instead of laces. A reinforced toe bumper for the inevitable.</p>",
            "Wipe clean or hand wash. Air dry away from direct sun. Do not machine wash.",
            "cotton-blend", "regular", "casual", 3990m, null, null, null,
            CatalogReferenceData.FootwearScale, KidsShoes, [White, Navy], StockProfile.Mixed, 4.1m, 28, 16, 190, 125),

        new("SD-K-0010", "Girls Denim Pinafore", "girls-denim-pinafore", "kids", "girls-dresses", "kids",
            "A soft denim pinafore with adjustable straps and a front pocket.",
            "<p>Washed soft so it is comfortable from day one. Adjustable button straps, a bib pocket and an A-line skirt with room to run.</p>",
            DenimCare, "denim", "relaxed", "casual", 3490m, null, null, null,
            CatalogReferenceData.KidsAgeScale, KidsAges, [MidBlue], StockProfile.Healthy, 4.4m, 36, 21, 265, 100),
    ];
}
