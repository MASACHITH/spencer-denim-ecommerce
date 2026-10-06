using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SpencerDenim.Domain.Catalog;
using SpencerDenim.Infrastructure;
using SpencerDenim.Infrastructure.Persistence;

namespace SpencerDenim.UnitTests.Architecture;

/// <summary>
/// Everything that points at a <see cref="ProductVariant"/> is named in
/// <see cref="VariantReferences.Known"/>, with a decision about what happens when the variant is deleted.
/// <para>
/// <b>Why this test exists.</b> Doc 08 §1.6 makes <c>ON DELETE NO ACTION</c> the default and lists the
/// cascades; nothing pointing at <c>ProductVariants</c> is among them. So ADM-10's "remove a size" has to
/// deal with every reference by hand, and on this task <b>three</b> were missed one after another — the 1:1
/// <c>StockLevels</c> row, the colourway's required composite key, and then <c>sales.CartItems</c>, each
/// surfacing as a 500 the admin could not act on, and each found only because some test happened to touch a
/// real database. The third was found by the tester, not by me.
/// </para>
/// <para>
/// <b>What it buys.</b> <c>inventory.InventoryMovements</c> (doc 08 §3.4, issue <b>#108</b>) does not exist
/// yet. It is append-only and kept "for the life of the system", so when ADM-12 adds it the delete must
/// <i>block</i> rather than cascade — and this test goes red the moment the entity appears, instead of
/// `RemoveVariantsAsync` either answering 500 again or quietly destroying ledger rows. No database and no
/// migration needed: it reads the EF model.
/// </para>
/// </summary>
public sealed class VariantReferenceConventionTests
{
    private static readonly IModel Model = BuildModel();

    /// <summary>
    /// Every foreign key in the model whose principal is <see cref="ProductVariant"/>, by the CLR name of
    /// the entity that holds it.
    /// </summary>
    private static List<string> Referencing() => Model
        .GetEntityTypes()
        .SelectMany(entity => entity.GetForeignKeys())
        .Where(foreignKey => foreignKey.PrincipalEntityType.ClrType == typeof(ProductVariant))
        .Select(foreignKey => foreignKey.DeclaringEntityType.ClrType.Name)
        .Distinct(StringComparer.Ordinal)
        .Order(StringComparer.Ordinal)
        .ToList();

    [Fact]
    public void Every_reference_to_a_variant_has_a_recorded_disposition()
    {
        var referencing = Referencing();

        // A guard on the sweep itself: if the relationships were renamed or the model stopped loading,
        // this rule would pass by finding nothing — the failure mode of every reflection test.
        Assert.True(
            referencing.Count >= 3,
            $"only {referencing.Count} entities were found pointing at ProductVariant, which is fewer than "
            + "this rule has ever seen. Either the model moved or this sweep has stopped sweeping. Point it "
            + "at the new shape rather than lowering the number.");

        var undeclared = referencing
            .Where(name => !VariantReferences.Known.ContainsKey(name))
            .ToList();

        Assert.True(
            undeclared.Count == 0,
            $"{string.Join(", ", undeclared)} point(s) at ProductVariant and VariantReferences.Known does "
            + "not say what happens to them when a variant is deleted.\n\n"
            + "Decide, write it there, and make ProductWriteStore do it:\n"
            + "  • DeletedWithTheVariant — the row is a *current* fact about the variant (its stock count, a "
            + "line in somebody's bag), so it is deleted in the same unit of work, before the variant.\n"
            + "  • BlocksTheDelete — the row is *history* that doc 08 §1.6 keeps for the life of the system "
            + "(an order line, an inventory movement), so VariantDeleteGuardsAsync refuses the delete and "
            + "the admin deactivates the variant instead.\n\n"
            + "Leaving it out is not a neutral choice: ON DELETE NO ACTION means the save answers 500 with a "
            + "constraint name and no explanation, which is exactly how CartItems shipped.");
    }

    /// <summary>
    /// The declaration has no entries for things that no longer point at a variant. An allow-list with a
    /// stale entry is a rule that has quietly stopped covering something.
    /// </summary>
    [Fact]
    public void Every_recorded_disposition_is_a_real_reference()
    {
        var referencing = Referencing().ToHashSet(StringComparer.Ordinal);

        var stale = VariantReferences.Known.Keys
            .Where(name => !referencing.Contains(name))
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.True(
            stale.Count == 0,
            $"VariantReferences.Known names {string.Join(", ", stale)}, which no longer hold a foreign key "
            + "to ProductVariant. Remove the entry, and the handling in ProductWriteStore with it.");
    }

    /// <summary>
    /// Doc 08 §1.6 again: none of these is a cascade, which is the whole reason the dispositions have to be
    /// carried out by hand. If one ever becomes a cascade, the code deleting it by hand becomes dead and
    /// should go — so the assumption is pinned rather than assumed.
    /// </summary>
    [Fact]
    public void No_reference_to_a_variant_is_a_cascade()
    {
        var cascading = Model
            .GetEntityTypes()
            .SelectMany(entity => entity.GetForeignKeys())
            .Where(foreignKey => foreignKey.PrincipalEntityType.ClrType == typeof(ProductVariant))
            .Where(foreignKey => foreignKey.DeleteBehavior is DeleteBehavior.Cascade or DeleteBehavior.ClientCascade)
            .Select(foreignKey => foreignKey.DeclaringEntityType.ClrType.Name)
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.True(
            cascading.Count == 0,
            $"{string.Join(", ", cascading)} now cascade(s) from ProductVariant. Doc 08 §1.6 lists the "
            + "cascades and none of these is on it; if that changed deliberately, the hand-written delete in "
            + "ProductWriteStore.RemoveVariantsAsync is now dead code and should be removed with this rule.");
    }

    private static IModel BuildModel()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Database"] = "Server=example;Database=SpencerDenim;",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddLogging();
        services.AddInfrastructure();

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        return scope.ServiceProvider.GetRequiredService<AppDbContext>().Model;
    }
}
