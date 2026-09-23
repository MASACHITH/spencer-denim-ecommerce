using SpencerDenim.Infrastructure.Persistence.Seeding;

namespace SpencerDenim.UnitTests.Infrastructure;

/// <summary>
/// The demo seeder (doc 08 §9) promises an identical catalogue on every machine, which the integration
/// tests rely on when they assert exact stock states. These tests pin the two places that promise was
/// broken: a generator seeded from <see cref="string.GetHashCode(string?)"/>, which .NET randomises per
/// process, and <c>Math.Abs</c> on a hash that can be <see cref="int.MinValue"/>.
/// </summary>
public sealed class DemoCatalogueSeederTests
{
    private static readonly string[] SampleProductCodes =
    [
        "SD-W-0001", "SD-W-0012", "SD-M-0003", "SD-M-0004", "SD-K-0002", "SD-K-0010",
    ];

    /// <summary>
    /// The values a fresh process must reproduce, written down rather than recomputed: a test that only
    /// compared the generator with itself would still pass if the seed became process-specific again.
    /// </summary>
    [Theory]
    [InlineData("SD-W-0001", 131_570_150u)]
    [InlineData("SD-M-0003", 2_834_243_067u)]
    [InlineData("SD-K-0002", 3_007_723_429u)]
    public void The_seed_for_a_product_code_is_the_same_in_every_process(string code, uint expected)
    {
        Assert.Equal(expected, DemoCatalogueSeeder.StableSeed(code));
    }

    [Fact]
    public void The_same_product_code_draws_the_same_stock_run_after_run()
    {
        foreach (var code in SampleProductCodes)
        {
            foreach (var profile in Enum.GetValues<StockProfile>())
            {
                Assert.Equal(DrawStock(code, profile), DrawStock(code, profile));
            }
        }
    }

    [Fact]
    public void Different_product_codes_draw_different_stock()
    {
        var draws = SampleProductCodes
            .Select(code => string.Join(',', DrawStock(code, StockProfile.Mixed)))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        Assert.Equal(SampleProductCodes.Length, draws.Count);
    }

    /// <summary>
    /// <c>ProductDetailAndSearchTests.A_partly_sold_out_product_marks_only_the_missing_sizes</c> reads
    /// SD-M-0003 "Poplin Formal Shirt" and needs it to have both a sold-out and an in-stock variant.
    /// With a per-process seed that held on about 97% of runs; it now holds on all of them.
    /// </summary>
    [Fact]
    public void The_partly_sold_out_demo_product_always_has_both_a_sold_out_and_a_stocked_variant()
    {
        var stock = DrawStock("SD-M-0003");

        Assert.Equal(10, stock.Length);
        Assert.Contains(0, stock);
        Assert.Contains(stock, quantity => quantity > 0);
    }

    [Fact]
    public void The_explicit_low_stock_threshold_falls_on_the_same_products_every_run()
    {
        var first = SampleProductCodes.Select(DemoCatalogueSeeder.ShouldUseExplicitThreshold).ToList();
        var second = SampleProductCodes.Select(DemoCatalogueSeeder.ShouldUseExplicitThreshold).ToList();

        Assert.Equal(first, second);

        // Roughly one product in seven gets one, and which seven is now fixed.
        Assert.True(DemoCatalogueSeeder.ShouldUseExplicitThreshold("SD-W-0003"));
        Assert.False(DemoCatalogueSeeder.ShouldUseExplicitThreshold("SD-K-0002"));
    }

    /// <summary>
    /// A digest whose first four bytes read as <see cref="int.MinValue"/> used to reach
    /// <c>Math.Abs(int.MinValue)</c> and throw. The conversion is unsigned now, so no product code can
    /// overflow, whatever its digest, and every draw stays inside its profile's band.
    /// </summary>
    [Fact]
    public void No_product_code_can_overflow_the_seed()
    {
        foreach (var code in Enumerable.Range(0, 5_000).Select(index => $"SD-X-{index:0000}"))
        {
            var random = new DemoCatalogueSeeder.DeterministicRandom(code);

            Assert.InRange(DemoCatalogueSeeder.OnHandFor(StockProfile.Mixed, random), 0, 35);
            Assert.InRange(DemoCatalogueSeeder.OnHandFor(StockProfile.Low, random), 1, 4);
            Assert.InRange(DemoCatalogueSeeder.OnHandFor(StockProfile.Healthy, random), 8, 47);
        }
    }

    /// <summary>
    /// One product's variant stock, drawn exactly the way <c>SeedProductAsync</c> draws it: one value
    /// per variant, and a product has one variant per colour and size. Drawing more than the real
    /// count would let a zero that only appears at draw 11 satisfy a test the seeder never reaches.
    /// </summary>
    private static int[] DrawStock(string code, StockProfile? profile = null)
    {
        var seed = DemoCatalogueData.Products.Single(product => product.Code == code);
        var variants = seed.Colours.Length * seed.SizeLabels.Length;
        var random = new DemoCatalogueSeeder.DeterministicRandom(code);

        return [.. Enumerable.Range(0, variants).Select(_ => DemoCatalogueSeeder.OnHandFor(profile ?? seed.Stock, random))];
    }
}
