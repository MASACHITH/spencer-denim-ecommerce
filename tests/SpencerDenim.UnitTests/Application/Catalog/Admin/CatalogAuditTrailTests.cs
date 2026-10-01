using System.Text.Json;
using SpencerDenim.Application.Abstractions.Auditing;
using SpencerDenim.Application.Catalog.Admin;
using SpencerDenim.Domain.Auditing;

namespace SpencerDenim.UnitTests.Application.Catalog.Admin;

/// <summary>
/// The audit rows W1-12 writes (issue #59, doc 08 §3.8 "changed properties only").
/// </summary>
public sealed class CatalogAuditTrailTests
{
    private static readonly DateTime When = new(2026, 10, 1, 12, 30, 0, DateTimeKind.Utc);

    private static readonly AdminActor Actor = new(7, "staff@example.com", "203.0.113.9", "agent", "corr-1");

    [Fact]
    public void A_diff_carries_only_the_fields_that_moved()
    {
        var row = CatalogAuditTrail.ForChange(
            AuditActions.Update,
            CatalogAuditTrail.CategoryEntityType,
            "17",
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["name"] = "Jeans", ["slug"] = "jeans" },
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["name"] = "Denim", ["slug"] = "jeans" },
            Actor,
            When);

        Assert.NotNull(row);
        Assert.Equal(AuditActions.Update, row.Action);
        Assert.Equal("Category", row.EntityType);
        Assert.Equal("17", row.EntityId);
        Assert.Equal(7, row.UserId);
        Assert.Equal("staff@example.com", row.UserEmail);
        Assert.Equal("corr-1", row.CorrelationId);
        Assert.Equal(When, row.OccurredAtUtc);

        var before = Fields(row.BeforeJson);
        var after = Fields(row.AfterJson);

        Assert.Equal(["name"], before.Keys);
        Assert.Equal(["name"], after.Keys);
        Assert.Equal("Jeans", before["name"].GetString());
        Assert.Equal("Denim", after["name"].GetString());
    }

    /// <summary>
    /// A save of a form nobody edited is not an event. Without this, a trail full of "changed nothing" rows
    /// is how an audit log stops being read — and W1-09's viewer has just shipped.
    /// </summary>
    [Fact]
    public void Nothing_changed_means_no_row_at_all()
    {
        var same = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["name"] = "Jeans",
            ["description"] = null,
            ["sortOrder"] = 3,
        };

        Assert.Null(CatalogAuditTrail.ForChange(
            AuditActions.Update, CatalogAuditTrail.CategoryEntityType, "17", same, new Dictionary<string, object?>(same, StringComparer.Ordinal), Actor, When));
    }

    /// <summary>
    /// The database collation is case-insensitive; the trail must not be. An admin correcting "Jeans" to
    /// "jeans" has changed something a shopper will see.
    /// </summary>
    [Fact]
    public void A_change_of_case_only_is_still_a_change()
    {
        var row = CatalogAuditTrail.ForChange(
            AuditActions.Update,
            CatalogAuditTrail.CategoryEntityType,
            "17",
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["name"] = "Jeans" },
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["name"] = "jeans" },
            Actor,
            When);

        Assert.NotNull(row);
    }

    [Fact]
    public void Clearing_a_field_is_a_change_and_setting_it_from_nothing_is_too()
    {
        var cleared = CatalogAuditTrail.ForChange(
            AuditActions.Update,
            CatalogAuditTrail.CategoryEntityType,
            "17",
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["description"] = "Soft denim" },
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["description"] = null },
            Actor,
            When);

        Assert.NotNull(cleared);
        Assert.Equal(JsonValueKind.Null, Fields(cleared.AfterJson)["description"].ValueKind);

        var filled = CatalogAuditTrail.ForChange(
            AuditActions.Update,
            CatalogAuditTrail.CategoryEntityType,
            "17",
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["imageAssetId"] = null },
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["imageAssetId"] = 41 },
            Actor,
            When);

        Assert.NotNull(filled);
        Assert.Equal(41, Fields(filled.AfterJson)["imageAssetId"].GetInt32());
    }

    /// <summary>
    /// <c>short</c> and <c>int</c> both reach this as boxed objects. <c>Department.SortOrder</c> is a
    /// <c>short</c> and <c>Category.SortOrder</c> an <c>int</c>, so a naive <c>Equals</c> between the boxes
    /// would report every department sort order as changed on every save.
    /// </summary>
    [Fact]
    public void Two_numbers_of_different_types_but_the_same_value_are_not_a_change()
    {
        Assert.Null(CatalogAuditTrail.ForChange(
            AuditActions.Update,
            CatalogAuditTrail.DepartmentEntityType,
            "1",
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["sortOrder"] = (short)2 },
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["sortOrder"] = 2 },
            Actor,
            When));
    }

    [Fact]
    public void A_creation_has_an_after_state_and_no_before_state()
    {
        var row = CatalogAuditTrail.Row(
            AuditActions.Create,
            CatalogAuditTrail.CategoryEntityType,
            "42",
            before: null,
            after: new Dictionary<string, object?>(StringComparer.Ordinal) { ["name"] = "Midi skirts" },
            Actor,
            When);

        Assert.Null(row.BeforeJson);
        Assert.Equal("Midi skirts", Fields(row.AfterJson)["name"].GetString());
    }

    /// <summary>
    /// A sibling list has to be identifiable: the root of a department has no parent id, so the department
    /// is part of the key or all three departments' root lists would share one <c>EntityId</c> and the
    /// viewer's entity filter would return all of them together.
    /// </summary>
    [Fact]
    public void An_order_scope_names_the_department_as_well_as_the_parent()
    {
        Assert.Equal("1/root", CatalogAuditTrail.OrderScopeId(1, null));
        Assert.Equal("2/root", CatalogAuditTrail.OrderScopeId(2, null));
        Assert.Equal("1/17", CatalogAuditTrail.OrderScopeId(1, 17));
    }

    /// <summary><c>ops.AuditLogs.EntityId</c> is <c>varchar(100)</c>; a longer value would fail the insert.</summary>
    [Fact]
    public void An_over_long_entity_id_is_trimmed_to_the_column()
    {
        var row = CatalogAuditTrail.Row(
            AuditActions.Update,
            CatalogAuditTrail.CategoryOrderEntityType,
            new string('9', 250),
            null,
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["order"] = "1,2" },
            Actor,
            When);

        Assert.Equal(100, row.EntityId!.Length);
    }

    /// <summary>
    /// Doc 08 §3.8 puts an <c>ISJSON</c> check on both columns, so anything written has to parse — and an
    /// empty diff has to be NULL rather than <c>{}</c> dressed up as content.
    /// </summary>
    [Fact]
    public void Both_json_columns_are_valid_json_or_null()
    {
        var row = CatalogAuditTrail.Row(
            AuditActions.SoftDelete,
            CatalogAuditTrail.CategoryEntityType,
            "17",
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["isDeleted"] = false },
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["isDeleted"] = true },
            Actor,
            When);

        using var before = JsonDocument.Parse(row.BeforeJson!);
        using var after = JsonDocument.Parse(row.AfterJson!);

        Assert.False(before.RootElement.GetProperty("isDeleted").GetBoolean());
        Assert.True(after.RootElement.GetProperty("isDeleted").GetBoolean());

        var empty = CatalogAuditTrail.Row(
            AuditActions.Update, CatalogAuditTrail.CategoryEntityType, "17", null, null, Actor, When);
        Assert.Null(empty.BeforeJson);
        Assert.Null(empty.AfterJson);
    }

    private static Dictionary<string, JsonElement> Fields(string? json)
    {
        Assert.NotNull(json);
        using var document = JsonDocument.Parse(json);

        return document.RootElement
            .EnumerateObject()
            .ToDictionary(property => property.Name, property => property.Value.Clone(), StringComparer.Ordinal);
    }
}
