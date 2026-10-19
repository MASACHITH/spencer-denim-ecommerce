using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SpencerDenim.Domain.Auditing;
using SpencerDenim.Domain.Identity;
using SpencerDenim.Domain.Inventory;
using SpencerDenim.Infrastructure.Persistence;
using SpencerDenim.IntegrationTests.Infrastructure;

namespace SpencerDenim.IntegrationTests.Api.Admin;

/// <summary>
/// The inventory screen's API (ADM-12, #190) against SQL Server: who may do what, the reserved floor, the
/// two shapes of change, bulk semantics, the threshold, the ledger and the audit trail.
/// </summary>
public sealed class AdminInventoryEndpointTests(SqlServerDatabaseFixture sqlServer) : InventoryTestBase(sqlServer)
{
    // ---- permissions ----------------------------------------------------------------------------------

    public static TheoryData<string, bool, bool> Roles => new()
    {
        // Role, may view, may edit. Doc 02's role table and doc 08 §9's seeded claims.
        { DefaultRoleDefinitions.StoreManagerName, true, true },
        { DefaultRoleDefinitions.OrderStaffName, true, false },
        { DefaultRoleDefinitions.ContentEditorName, false, false },
    };

    /// <summary>
    /// Each default role against each route: Order Staff see stock and cannot change it; Content Editors see
    /// neither; Store Managers do both. The seeded permission lists are used as they are, so a role that
    /// changes in <see cref="DefaultRoleDefinitions"/> changes this test's answer.
    /// </summary>
    [Theory]
    [MemberData(nameof(Roles))]
    public async Task Each_default_role_gets_exactly_what_the_role_matrix_gives_it(string role, bool view, bool edit)
    {
        var variantId = await CheckoutVariantIdAsync();
        await ArrangeAsync(variantId, onHand: 20);

        var permissions = DefaultRoleDefinitions.All.Single(definition => definition.Name == role).Permissions;
        using var client = await StaffAsync(permissions);

        var viewStatus = view ? HttpStatusCode.OK : HttpStatusCode.Forbidden;
        var editStatus = edit ? HttpStatusCode.OK : HttpStatusCode.Forbidden;

        Assert.Equal(viewStatus, (await GetAsync(client, ListUrl)).Status);
        Assert.Equal(viewStatus, (await GetAsync(client, $"{ListUrl}/filters")).Status);
        Assert.Equal(viewStatus, (await GetAsync(client, $"{ListUrl}/{variantId}/movements")).Status);

        var mark = await MovementMarkAsync();
        Assert.Equal(editStatus, (await PostAsync(client, AdjustUrl, new { reason = "Role test", rows = new[] { Row(variantId, "Adjust", 1) } })).Status);
        Assert.Equal(editStatus, (await PutAsync(client, $"{ListUrl}/{variantId}/threshold", new { lowStockThreshold = 3 })).Status);

        // A refused write changed nothing and wrote nothing.
        Assert.Equal(edit ? 1 : 0, (await MovementsAfterAsync(mark, variantId)).Count);
        Assert.Equal(edit ? 21 : 20, (await StockAsync(variantId)).OnHand);
    }

    /// <summary>
    /// Inventory.View is what opens the screen, and nothing else does: Reports.View (the inventory report's
    /// permission) is not enough, and Inventory.View does not open the report — doc 11 P16 keeps Order
    /// Staff out of Reports, and the decision not to widen it is recorded in the journal.
    /// </summary>
    [Fact]
    public async Task The_screen_needs_Inventory_View_and_the_report_still_needs_Reports_View()
    {
        using var reportsOnly = await StaffAsync(Permissions.Reports.View);
        using var inventoryOnly = await StaffAsync(Permissions.Inventory.View);
        using var editOnly = await StaffAsync(Permissions.Inventory.Edit);

        Assert.Equal(HttpStatusCode.Forbidden, (await GetAsync(reportsOnly, ListUrl)).Status);
        Assert.Equal(HttpStatusCode.OK, (await GetAsync(reportsOnly, "/api/admin/reports/inventory")).Status);
        Assert.Equal(HttpStatusCode.OK, (await GetAsync(inventoryOnly, ListUrl)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await GetAsync(inventoryOnly, "/api/admin/reports/inventory")).Status);

        // Edit alone does not read: each route declares the one permission it needs (doc 03 §3a).
        Assert.Equal(HttpStatusCode.Forbidden, (await GetAsync(editOnly, ListUrl)).Status);
    }

    [Fact]
    public async Task An_anonymous_caller_is_refused_every_route()
    {
        using var anonymous = Factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
        });

        Assert.Equal(HttpStatusCode.Unauthorized, (await GetAsync(anonymous, ListUrl)).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await GetAsync(anonymous, $"{ListUrl}/1/movements")).Status);
        Assert.NotEqual(HttpStatusCode.OK, (await PostAsync(anonymous, AdjustUrl, new { reason = "x", rows = new[] { Row(1, "Adjust", 1) } })).Status);
    }

    // ---- the reserved floor ---------------------------------------------------------------------------

    /// <summary>
    /// Doc 11 P2 step 3: on hand may never fall below reserved. With 10 on hand and 4 held by unpaid orders,
    /// taking 7 is refused with the reserved figure in the message and nothing changes; taking 6 leaves 4
    /// on hand and 0 available; a count below 4 is refused; a count of exactly 4 is applied.
    /// </summary>
    [Fact]
    public async Task On_hand_can_never_be_taken_below_what_unpaid_orders_hold()
    {
        var variantId = await CheckoutVariantIdAsync();
        await ArrangeAsync(variantId, onHand: 10, reserved: 4);
        using var manager = await StaffAsync(Permissions.Inventory.View, Permissions.Inventory.Edit);
        var mark = await MovementMarkAsync();

        var refused = (await AdjustAsync(manager, "Damaged", Row(variantId, "Adjust", -7))).GetProperty("rows")[0];
        Assert.Equal("Refused", refused.GetProperty("outcome").GetString());
        Assert.Contains("4 units are reserved for unpaid orders", refused.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Equal(10, refused.GetProperty("onHand").GetInt32());
        Assert.Equal(0, refused.GetProperty("onHandDelta").GetInt32());
        Assert.Equal((10, 4, 6), await StockAsync(variantId));

        var applied = (await AdjustAsync(manager, "Damaged", Row(variantId, "Adjust", -6))).GetProperty("rows")[0];
        Assert.Equal("Applied", applied.GetProperty("outcome").GetString());
        Assert.Equal(0, applied.GetProperty("available").GetInt32());
        Assert.Equal((4, 4, 0), await StockAsync(variantId));

        var belowCount = (await AdjustAsync(manager, "Stock-take", Row(variantId, "SetCount", 3, expectedOnHand: 4))).GetProperty("rows")[0];
        Assert.Equal("Refused", belowCount.GetProperty("outcome").GetString());
        Assert.Equal((4, 4, 0), await StockAsync(variantId));

        var atCount = (await AdjustAsync(manager, "Stock-take", Row(variantId, "SetCount", 4, expectedOnHand: 4))).GetProperty("rows")[0];
        Assert.Equal("Applied", atCount.GetProperty("outcome").GetString());

        // Two applied changes, two ledger rows; the refusals wrote none.
        var movements = await MovementsAfterAsync(mark, variantId);
        Assert.Equal(2, movements.Count);
        Assert.Equal(InventoryMovementType.AdjustOut, movements[0].MovementType);
        Assert.Equal(-6, movements[0].OnHandDelta);
        Assert.Equal(4, movements[0].OnHandAfter);
        Assert.Equal(4, movements[0].ReservedAfter);
        Assert.Equal(InventoryMovementType.CountCorrection, movements[1].MovementType);
        Assert.Equal(0, movements[1].OnHandDelta);
    }

    /// <summary>A single reserved unit is named in the singular, and the floor holds at exactly 1.</summary>
    [Fact]
    public async Task One_reserved_unit_is_said_in_the_singular()
    {
        var variantId = await CheckoutVariantIdAsync();
        await ArrangeAsync(variantId, onHand: 1, reserved: 1);
        using var manager = await StaffAsync(Permissions.Inventory.Edit);

        var row = (await AdjustAsync(manager, "Lost", Row(variantId, "Adjust", -1))).GetProperty("rows")[0];

        Assert.Equal("Refused", row.GetProperty("outcome").GetString());
        Assert.StartsWith("1 unit is reserved for an unpaid order", row.GetProperty("message").GetString(), StringComparison.Ordinal);
    }

    // ---- the two shapes -------------------------------------------------------------------------------

    /// <summary>
    /// A set count applies only while on hand is the figure the screen showed (doc 08 §5.8). When it moved,
    /// the answer is Changed with the figure it is now, and nothing is written.
    /// </summary>
    [Fact]
    public async Task A_set_count_against_a_figure_that_moved_is_Changed_and_writes_nothing()
    {
        var variantId = await CheckoutVariantIdAsync();
        await ArrangeAsync(variantId, onHand: 12);
        using var manager = await StaffAsync(Permissions.Inventory.Edit);
        var mark = await MovementMarkAsync();
        var audit = await AuditMarkAsync();

        var row = (await AdjustAsync(manager, "Stock-take", Row(variantId, "SetCount", 30, expectedOnHand: 11))).GetProperty("rows")[0];

        Assert.Equal("Changed", row.GetProperty("outcome").GetString());
        Assert.Contains("On hand is now 12, not the 11", row.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Equal(12, (await StockAsync(variantId)).OnHand);
        Assert.Empty(await MovementsAfterAsync(mark, variantId));
        Assert.Empty(await StockAuditAfterAsync(audit));
    }

    /// <summary>
    /// Receive, adjust in, adjust out and a set count each write the movement type doc 08 §3.4 names, with
    /// the delta, the figures after, the reason, the staff member and no batch; and an audit row each.
    /// </summary>
    [Fact]
    public async Task Every_applied_change_writes_one_movement_and_one_audit_row()
    {
        var variantId = await CheckoutVariantIdAsync();
        await ArrangeAsync(variantId, onHand: 10, reserved: 2);
        using var manager = await StaffAsync(Permissions.Inventory.View, Permissions.Inventory.Edit);
        var mark = await MovementMarkAsync();
        var audit = await AuditMarkAsync();

        await AdjustAsync(manager, "Delivery from the factory", Row(variantId, "Receive", 5));
        await AdjustAsync(manager, "Found in count", Row(variantId, "Adjust", 2));
        await AdjustAsync(manager, "Damaged in store", Row(variantId, "Adjust", -3));
        var counted = await AdjustAsync(manager, "Stock-take", Row(variantId, "SetCount", 20, expectedOnHand: 14));

        Assert.True(counted.GetProperty("batchId").ValueKind == JsonValueKind.Null, "A single change has no batch.");

        var movements = await MovementsAfterAsync(mark, variantId);
        Assert.Equal(
            [InventoryMovementType.Receive, InventoryMovementType.AdjustIn, InventoryMovementType.AdjustOut, InventoryMovementType.CountCorrection],
            movements.Select(movement => movement.MovementType));
        Assert.Equal([5, 2, -3, 6], movements.Select(movement => movement.OnHandDelta));
        Assert.Equal([15, 17, 14, 20], movements.Select(movement => movement.OnHandAfter));
        Assert.All(movements, movement =>
        {
            Assert.Equal(2, movement.ReservedAfter);
            Assert.Equal(0, movement.ReservedDelta);
            Assert.Null(movement.BatchId);
            Assert.Null(movement.OrderId);
            Assert.NotNull(movement.CreatedByUserId);
        });
        Assert.Equal(["Delivery from the factory", "Found in count", "Damaged in store", "Stock-take"], movements.Select(movement => movement.Reason));

        var rows = await StockAuditAfterAsync(audit);
        Assert.Equal(4, rows.Count);
        Assert.All(rows, row =>
        {
            Assert.Equal(AuditActions.Update, row.Action);
            Assert.Equal(variantId.ToString(System.Globalization.CultureInfo.InvariantCulture), row.EntityId);
            Assert.Equal(movements[0].CreatedByUserId, row.UserId);
        });
        var last = JsonDocument.Parse(rows[3].AfterJson!).RootElement;
        Assert.Equal(20, last.GetProperty("onHand").GetInt32());
        Assert.Equal("CountCorrection", last.GetProperty("movementType").GetString());
        Assert.Equal(14, JsonDocument.Parse(rows[3].BeforeJson!).RootElement.GetProperty("onHand").GetInt32());

        // The history reads them back newest first, with who did it. Scoped to this test's own movements:
        // the variant is shared with other suites and its older history is theirs.
        var (_, history) = await GetAsync(manager, $"{ListUrl}/{variantId}/movements?pageSize=100");
        var mine = history.GetProperty("rows").EnumerateArray().Where(row => row.GetProperty("movementId").GetInt64() > mark).ToList();
        Assert.Equal(["CountCorrection", "AdjustOut", "AdjustIn", "Receive"], mine.Select(row => row.GetProperty("movementType").GetString()));
        Assert.EndsWith("@example.com", mine[0].GetProperty("createdBy").GetString(), StringComparison.Ordinal);
        Assert.Equal(20, history.GetProperty("variant").GetProperty("onHand").GetInt32());
    }

    /// <summary>On hand cannot be pushed past the ceiling by a delta either, and the refusal says why.</summary>
    [Fact]
    public async Task A_delta_that_would_pass_the_ceiling_is_refused()
    {
        var variantId = await CheckoutVariantIdAsync();
        await ArrangeAsync(variantId, onHand: StockChangeRules.MaxOnHand - 10);
        using var manager = await StaffAsync(Permissions.Inventory.Edit);

        var row = (await AdjustAsync(manager, "Delivery", Row(variantId, "Receive", 11))).GetProperty("rows")[0];

        Assert.Equal("Refused", row.GetProperty("outcome").GetString());
        Assert.Contains("1,000,000", row.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Equal(StockChangeRules.MaxOnHand - 10, (await StockAsync(variantId)).OnHand);
    }

    // ---- bulk ------------------------------------------------------------------------------------------

    /// <summary>
    /// Doc 08 §5.8 / doc 11 P2 step 7: per row. One request carrying an applicable receive, an adjustment
    /// that would breach the floor, a set count against a moved figure and an unknown variant applies the
    /// first, reports the other three, answers in the order sent, and stamps one batch id on the one
    /// movement it wrote.
    /// </summary>
    [Fact]
    public async Task A_bulk_update_applies_row_by_row_and_reports_each_row_in_the_order_sent()
    {
        var ids = await CheckoutVariantIdsAsync(3);
        var (receive, breach, moved) = (ids[0], ids[1], ids[2]);
        await ArrangeAsync(receive, onHand: 5);
        await ArrangeAsync(breach, onHand: 5, reserved: 3);
        await ArrangeAsync(moved, onHand: 9);
        using var manager = await StaffAsync(Permissions.Inventory.Edit);
        var mark = await MovementMarkAsync();
        var audit = await AuditMarkAsync();

        var result = await AdjustAsync(
            manager,
            "Weekly delivery",
            Row(moved, "SetCount", 1, expectedOnHand: 8),
            Row(int.MaxValue, "Adjust", 1),
            Row(breach, "Adjust", -4),
            Row(receive, "Receive", 7));

        Assert.Equal(1, result.GetProperty("appliedCount").GetInt32());
        Assert.Equal(3, result.GetProperty("notAppliedCount").GetInt32());

        var rows = result.GetProperty("rows").EnumerateArray().ToList();
        Assert.Equal([moved, int.MaxValue, breach, receive], rows.Select(row => row.GetProperty("variantId").GetInt32()));
        Assert.Equal(["Changed", "NotFound", "Refused", "Applied"], rows.Select(row => row.GetProperty("outcome").GetString()));
        Assert.Equal(JsonValueKind.Null, rows[1].GetProperty("onHand").ValueKind);

        Assert.Equal((12, 0, 12), await StockAsync(receive));
        Assert.Equal((5, 3, 2), await StockAsync(breach));
        Assert.Equal((9, 0, 9), await StockAsync(moved));

        var movement = Assert.Single(await MovementsAfterAsync(mark));
        Assert.Equal(receive, movement.VariantId);
        Assert.Equal(Guid.Parse(result.GetProperty("batchId").GetString()!), movement.BatchId);
        Assert.Single(await StockAuditAfterAsync(audit));
    }

    /// <summary>
    /// Validation is all or nothing: one bad row in a request of three refuses the request with the bad
    /// row's field named, and the two good rows are not applied.
    /// </summary>
    [Fact]
    public async Task A_malformed_bulk_update_is_refused_whole_and_applies_nothing()
    {
        var ids = await CheckoutVariantIdsAsync(2);
        await ArrangeAsync(ids[0], onHand: 5);
        await ArrangeAsync(ids[1], onHand: 5);
        using var manager = await StaffAsync(Permissions.Inventory.Edit);
        var mark = await MovementMarkAsync();

        var (status, body) = await PostAsync(manager, AdjustUrl, new
        {
            reason = "Delivery",
            rows = new[] { Row(ids[0], "Receive", 2), Row(ids[1], "Receive", -2), Row(ids[0], "Adjust", 1) },
        });

        Assert.Equal(HttpStatusCode.BadRequest, status);
        var errors = body.GetProperty("errors");
        Assert.True(errors.TryGetProperty("rows[1].quantity", out _), body.ToString());
        Assert.True(errors.TryGetProperty("rows[2].variantId", out _), "A variant named twice is refused.");
        Assert.Equal(5, (await StockAsync(ids[0])).OnHand);
        Assert.Empty(await MovementsAfterAsync(mark));
    }

    [Fact]
    public async Task More_than_five_hundred_rows_are_refused()
    {
        using var manager = await StaffAsync(Permissions.Inventory.Edit);

        var rows = Enumerable.Range(1, StockChangeRules.MaxBulkRows + 1).Select(id => Row(id, "Adjust", 1)).ToArray();
        var (status, body) = await PostAsync(manager, AdjustUrl, new { reason = "Too many", rows });

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.True(body.GetProperty("errors").TryGetProperty("rows", out _));
    }

    // ---- validation -------------------------------------------------------------------------------------

    public static TheoryData<string, string, string> BadRequests => new()
    {
        // Name, body (with {v} for the variant), the field the refusal must name.
        { "no reason", """{"reason":null,"rows":[{"variantId":{v},"kind":"Adjust","quantity":1,"expectedOnHand":null}]}""", "reason" },
        { "blank reason", """{"reason":"   ","rows":[{"variantId":{v},"kind":"Adjust","quantity":1,"expectedOnHand":null}]}""", "reason" },
        { "reason too long", """{"reason":"{long}","rows":[{"variantId":{v},"kind":"Adjust","quantity":1,"expectedOnHand":null}]}""", "reason" },
        { "reason key missing", """{"rows":[{"variantId":{v},"kind":"Adjust","quantity":1,"expectedOnHand":null}]}""", "reason" },
        { "no rows", """{"reason":"x","rows":[]}""", "rows" },
        { "zero adjustment", """{"reason":"x","rows":[{"variantId":{v},"kind":"Adjust","quantity":0,"expectedOnHand":null}]}""", "rows[0].quantity" },
        { "huge adjustment", """{"reason":"x","rows":[{"variantId":{v},"kind":"Adjust","quantity":100001,"expectedOnHand":null}]}""", "rows[0].quantity" },
        { "huge negative adjustment", """{"reason":"x","rows":[{"variantId":{v},"kind":"Adjust","quantity":-100001,"expectedOnHand":null}]}""", "rows[0].quantity" },
        { "beyond int", """{"reason":"x","rows":[{"variantId":{v},"kind":"Adjust","quantity":99999999999,"expectedOnHand":null}]}""", "rows[0].quantity" },
        { "fraction", """{"reason":"x","rows":[{"variantId":{v},"kind":"Adjust","quantity":1.5,"expectedOnHand":null}]}""", "rows[0].quantity" },
        { "negative receipt", """{"reason":"x","rows":[{"variantId":{v},"kind":"Receive","quantity":-1,"expectedOnHand":null}]}""", "rows[0].quantity" },
        { "negative count", """{"reason":"x","rows":[{"variantId":{v},"kind":"SetCount","quantity":-1,"expectedOnHand":5}]}""", "rows[0].quantity" },
        { "count without the figure", """{"reason":"x","rows":[{"variantId":{v},"kind":"SetCount","quantity":5,"expectedOnHand":null}]}""", "rows[0].expectedOnHand" },
        { "figure on an adjustment", """{"reason":"x","rows":[{"variantId":{v},"kind":"Adjust","quantity":5,"expectedOnHand":3}]}""", "rows[0].expectedOnHand" },
        { "kind as a number", """{"reason":"x","rows":[{"variantId":{v},"kind":"2","quantity":1,"expectedOnHand":null}]}""", "rows[0].kind" },
        { "unknown kind", """{"reason":"x","rows":[{"variantId":{v},"kind":"Steal","quantity":1,"expectedOnHand":null}]}""", "rows[0].kind" },
        { "key missing in a row", """{"reason":"x","rows":[{"variantId":{v},"kind":"Adjust","quantity":1}]}""", "rows[0].expectedOnHand" },
        { "no variant", """{"reason":"x","rows":[{"variantId":null,"kind":"Adjust","quantity":1,"expectedOnHand":null}]}""", "rows[0].variantId" },
    };

    [Theory]
    [MemberData(nameof(BadRequests))]
    public async Task A_malformed_change_is_refused_naming_its_field_and_changes_nothing(string name, string template, string field)
    {
        var variantId = await CheckoutVariantIdAsync();
        await ArrangeAsync(variantId, onHand: 10);
        using var manager = await StaffAsync(Permissions.Inventory.Edit);
        var mark = await MovementMarkAsync();

        var body = template
            .Replace("{v}", variantId.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{long}", new string('r', InventoryMovement.ReasonMaxLength + 1), StringComparison.Ordinal);

        using var content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
        using var response = await manager.PostAsync(new Uri(AdjustUrl, UriKind.Relative), content, Token);
        var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Token)).RootElement;

        Assert.True(response.StatusCode == HttpStatusCode.BadRequest, $"{name}: {(int)response.StatusCode}");
        Assert.True(problem.GetProperty("errors").TryGetProperty(field, out _), $"{name}: {problem}");
        Assert.Equal(10, (await StockAsync(variantId)).OnHand);
        Assert.Empty(await MovementsAfterAsync(mark, variantId));
    }

    /// <summary>The longest reason the column holds is accepted, and kept exactly (trimmed).</summary>
    [Fact]
    public async Task A_reason_of_exactly_250_characters_is_accepted_and_trimmed()
    {
        var variantId = await CheckoutVariantIdAsync();
        await ArrangeAsync(variantId, onHand: 10);
        using var manager = await StaffAsync(Permissions.Inventory.Edit);
        var mark = await MovementMarkAsync();
        var reason = new string('r', InventoryMovement.ReasonMaxLength);

        await AdjustAsync(manager, "  " + reason + "  ", Row(variantId, "Adjust", 1));

        Assert.Equal(reason, Assert.Single(await MovementsAfterAsync(mark, variantId)).Reason);
    }

    // ---- threshold --------------------------------------------------------------------------------------

    /// <summary>
    /// A value is the variant's own; null is the shop default. The answer and the list say which applies,
    /// the status follows it, each real change writes one audit row, and saving the same value writes none.
    /// </summary>
    [Fact]
    public async Task A_threshold_is_the_variant_s_own_or_null_for_the_shop_default()
    {
        var variantId = await CheckoutVariantIdAsync();
        await ArrangeAsync(variantId, onHand: 8);
        using var manager = await StaffAsync(Permissions.Inventory.View, Permissions.Inventory.Edit);
        var audit = await AuditMarkAsync();
        var sku = await SkuAsync(variantId);
        var url = $"{ListUrl}/{variantId}/threshold";

        var (status, own) = await PutAsync(manager, url, new { lowStockThreshold = 10 });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(10, own.GetProperty("threshold").GetInt32());
        Assert.Equal(10, own.GetProperty("ownThreshold").GetInt32());
        Assert.Equal("LowStock", own.GetProperty("stockStatus").GetString());
        Assert.Equal(10, await OwnThresholdAsync(variantId));

        var (_, cleared) = await PutAsync(manager, url, new { lowStockThreshold = (int?)null });
        var defaultThreshold = cleared.GetProperty("threshold").GetInt32();
        Assert.Equal(JsonValueKind.Null, cleared.GetProperty("ownThreshold").ValueKind);
        Assert.Null(await OwnThresholdAsync(variantId));
        Assert.Equal(8 <= defaultThreshold ? "LowStock" : "InStock", cleared.GetProperty("stockStatus").GetString());

        var (_, list) = await GetAsync(manager, $"{ListUrl}?search={Uri.EscapeDataString(sku)}");
        var row = list.GetProperty("rows").EnumerateArray().Single(candidate => candidate.GetProperty("variantId").GetInt32() == variantId);
        Assert.Equal(defaultThreshold, row.GetProperty("threshold").GetInt32());
        Assert.Equal(list.GetProperty("defaultThreshold").GetInt32(), defaultThreshold);

        // Unchanged: no audit row.
        Assert.Equal(HttpStatusCode.OK, (await PutAsync(manager, url, new { lowStockThreshold = (int?)null })).Status);

        var rows = await StockAuditAfterAsync(audit);
        Assert.Equal(2, rows.Count);
        Assert.Equal("10", JsonDocument.Parse(rows[0].AfterJson!).RootElement.GetProperty("lowStockThreshold").GetRawText());
        Assert.Equal("null", JsonDocument.Parse(rows[1].AfterJson!).RootElement.GetProperty("lowStockThreshold").GetRawText());
    }

    [Fact]
    public async Task A_threshold_must_be_0_to_10000_or_null_and_the_key_must_be_sent()
    {
        var variantId = await CheckoutVariantIdAsync();
        await ArrangeAsync(variantId, onHand: 8, threshold: 4);
        using var manager = await StaffAsync(Permissions.Inventory.Edit);
        var url = $"{ListUrl}/{variantId}/threshold";

        Assert.Equal(HttpStatusCode.BadRequest, (await PutAsync(manager, url, new { lowStockThreshold = -1 })).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await PutAsync(manager, url, new { lowStockThreshold = StockChangeRules.MaxThreshold + 1 })).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await PutAsync(manager, url, new { something = 1 })).Status);
        Assert.Equal(4, await OwnThresholdAsync(variantId));

        Assert.Equal(HttpStatusCode.OK, (await PutAsync(manager, url, new { lowStockThreshold = 0 })).Status);
        Assert.Equal(0, await OwnThresholdAsync(variantId));

        Assert.Equal(HttpStatusCode.NotFound, (await PutAsync(manager, $"{ListUrl}/{int.MaxValue}/threshold", new { lowStockThreshold = 1 })).Status);
    }

    // ---- the list ---------------------------------------------------------------------------------------

    /// <summary>
    /// The status rule: out of stock is never low, at the threshold is low, one above is in stock, and the
    /// variant's own threshold wins over the default in both directions. The filters agree with the
    /// statuses, and the counts ignore the status filter.
    /// </summary>
    [Fact]
    public async Task The_list_classifies_out_low_and_in_stock_by_the_variant_s_threshold()
    {
        var ids = await CheckoutVariantIdsAsync(5);
        using var viewer = await StaffAsync(Permissions.Inventory.View);
        var (_, baseline) = await GetAsync(viewer, $"{ListUrl}?pageSize=1");
        var threshold = baseline.GetProperty("defaultThreshold").GetInt32();

        await ArrangeAsync(ids[0], onHand: 3, reserved: 3);                          // out: nothing available
        await ArrangeAsync(ids[1], onHand: threshold);                               // low: at the default
        await ArrangeAsync(ids[2], onHand: threshold + 1);                           // in stock
        await ArrangeAsync(ids[3], onHand: threshold + 5, threshold: threshold + 5); // low by its own
        await ArrangeAsync(ids[4], onHand: threshold, threshold: threshold - 1);     // in stock by its own

        var expected = new[] { "OutOfStock", "LowStock", "InStock", "LowStock", "InStock" };

        for (var index = 0; index < ids.Count; index++)
        {
            var sku = await SkuAsync(ids[index]);
            var (_, page) = await GetAsync(viewer, $"{ListUrl}?search={Uri.EscapeDataString(sku)}");
            var row = page.GetProperty("rows").EnumerateArray().Single(candidate => candidate.GetProperty("variantId").GetInt32() == ids[index]);
            Assert.Equal(expected[index], row.GetProperty("stockStatus").GetString());

            foreach (var status in new[] { "OutOfStock", "LowStock", "InStock" })
            {
                var (_, filtered) = await GetAsync(viewer, $"{ListUrl}?search={Uri.EscapeDataString(sku)}&status={status}");
                var listed = filtered.GetProperty("rows").EnumerateArray().Any(candidate => candidate.GetProperty("variantId").GetInt32() == ids[index]);
                Assert.True(listed == (status == expected[index]), $"{sku} under {status}");
            }
        }

        var (_, all) = await GetAsync(viewer, $"{ListUrl}?pageSize=1");
        var (_, lowOnly) = await GetAsync(viewer, $"{ListUrl}?pageSize=1&status=LowStock");
        Assert.Equal(all.GetProperty("counts").GetRawText(), lowOnly.GetProperty("counts").GetRawText());
        Assert.Equal(lowOnly.GetProperty("counts").GetProperty("lowStock").GetInt32(), lowOnly.GetProperty("totalCount").GetInt32());
        var counts = all.GetProperty("counts");
        Assert.Equal(
            all.GetProperty("totalCount").GetInt32(),
            counts.GetProperty("inStock").GetInt32() + counts.GetProperty("lowStock").GetInt32() + counts.GetProperty("outOfStock").GetInt32());
    }

    [Fact]
    public async Task The_list_filters_by_department_and_category_including_categories_below_it()
    {
        using var viewer = await StaffAsync(Permissions.Inventory.View);
        var (_, filters) = await GetAsync(viewer, $"{ListUrl}/filters");
        Assert.True(filters.GetProperty("departments").GetArrayLength() >= 3);

        using var scope = CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // A top-level category with at least one active variant somewhere below it.
        var parent = await db.Categories
            .AsNoTracking()
            .Where(category => category.Depth == 1)
            .Select(category => new { category.Id, category.DepartmentId, category.Path })
            .FirstAsync(category => db.ProductVariants.Any(variant =>
                variant.IsActive && !variant.Product!.IsDeleted
                && db.Categories.Any(below => below.Id == variant.Product.PrimaryCategoryId && below.Path.StartsWith(category.Path))), Token);

        var expectedInCategory = await db.ProductVariants
            .AsNoTracking()
            .CountAsync(variant => variant.IsActive && !variant.Product!.IsDeleted
                && db.Categories.Any(below => below.Id == variant.Product.PrimaryCategoryId && below.Path.StartsWith(parent.Path)), Token);
        var expectedInDepartment = await db.ProductVariants
            .AsNoTracking()
            .CountAsync(variant => variant.IsActive && !variant.Product!.IsDeleted && variant.Product.DepartmentId == parent.DepartmentId, Token);

        var (_, byCategory) = await GetAsync(viewer, $"{ListUrl}?categoryId={parent.Id}&pageSize=100");
        var (_, byDepartment) = await GetAsync(viewer, $"{ListUrl}?departmentId={parent.DepartmentId}&pageSize=1");
        var (_, unknown) = await GetAsync(viewer, $"{ListUrl}?categoryId={int.MaxValue}");

        Assert.Equal(expectedInCategory, byCategory.GetProperty("totalCount").GetInt32());
        Assert.Equal(expectedInDepartment, byDepartment.GetProperty("totalCount").GetInt32());
        Assert.Equal(0, unknown.GetProperty("totalCount").GetInt32());
    }

    [Fact]
    public async Task The_list_sorts_pages_and_refuses_a_sort_or_status_given_as_a_number()
    {
        using var viewer = await StaffAsync(Permissions.Inventory.View);

        var (_, descending) = await GetAsync(viewer, $"{ListUrl}?sort=Available&direction=Desc&pageSize=20");
        var available = descending.GetProperty("rows").EnumerateArray().Select(row => row.GetProperty("available").GetInt32()).ToList();
        Assert.Equal(available.OrderDescending(), available);

        var (_, clamped) = await GetAsync(viewer, $"{ListUrl}?page=0&pageSize=100000");
        Assert.Equal(1, clamped.GetProperty("page").GetInt32());
        Assert.Equal(100, clamped.GetProperty("pageSize").GetInt32());

        var (_, six) = await GetAsync(viewer, $"{ListUrl}?pageSize=6");
        var (_, second) = await GetAsync(viewer, $"{ListUrl}?page=2&pageSize=3");
        Assert.Equal(six.GetProperty("rows")[3].GetProperty("variantId").GetInt32(), second.GetProperty("rows")[0].GetProperty("variantId").GetInt32());

        Assert.Equal(HttpStatusCode.BadRequest, (await GetAsync(viewer, $"{ListUrl}?sort=2")).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await GetAsync(viewer, $"{ListUrl}?status=1")).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await GetAsync(viewer, $"{ListUrl}?direction=Sideways")).Status);
    }

    // ---- tester #190 findings D1–D3 -----------------------------------------------------------------------

    /// <summary>
    /// D1: the ledger itself refuses an Adjust or CountCorrection with no reason, NULL included. Before the
    /// fix the CHECK evaluated to UNKNOWN for NULL and let it through.
    /// </summary>
    [Theory]
    [InlineData(3, null)]
    [InlineData(4, null)]
    [InlineData(9, null)]
    [InlineData(4, "")]
    [InlineData(4, "   ")]
    public async Task The_ledger_refuses_a_manual_movement_with_no_reason(int movementType, string? reason)
    {
        var variantId = await CheckoutVariantIdAsync();
        using var scope = CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var error = await Assert.ThrowsAsync<Microsoft.Data.SqlClient.SqlException>(() => db.Database.ExecuteSqlRawAsync(
            "INSERT INTO inventory.InventoryMovements (VariantId, MovementType, OnHandDelta, ReservedDelta, OnHandAfter, ReservedAfter, Reason) "
            + "VALUES (@variant, @type, -1, 0, 1, 0, @reason);",
            [
                new Microsoft.Data.SqlClient.SqlParameter("@variant", variantId),
                new Microsoft.Data.SqlClient.SqlParameter("@type", (byte)movementType),
                new Microsoft.Data.SqlClient.SqlParameter("@reason", System.Data.SqlDbType.NVarChar, 250) { Value = (object?)reason ?? DBNull.Value },
            ],
            Token));

        Assert.Equal(547, error.Number);
        Assert.Contains("CK_InventoryMovements_Reason", error.Message, StringComparison.Ordinal);
    }

    /// <summary>D1's control: an automatic movement (Reserve) needs no reason, so the CHECK is not simply "always".</summary>
    [Fact]
    public async Task The_ledger_accepts_an_automatic_movement_with_no_reason()
    {
        var variantId = await CheckoutVariantIdAsync();
        using var scope = CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        await using var transaction = await db.Database.BeginTransactionAsync(Token);
        var written = await db.Database.ExecuteSqlRawAsync(
            "INSERT INTO inventory.InventoryMovements (VariantId, MovementType, OnHandDelta, ReservedDelta, OnHandAfter, ReservedAfter, Reason) "
            + "VALUES ({0}, 5, 0, 1, 1, 1, NULL);",
            [variantId],
            Token);
        await transaction.RollbackAsync(Token);

        Assert.Equal(1, written);
    }

    /// <summary>D2: the list's names are names — no numbers with a sign, no comma lists.</summary>
    [Theory]
    [InlineData("status=+1")]
    [InlineData("status=LowStock,OutOfStock")]
    [InlineData("sort=Sku,OnHand")]
    [InlineData("sort=+2")]
    [InlineData("direction=Asc,Desc")]
    [InlineData("direction=%201")]
    public async Task The_list_refuses_a_name_that_is_a_number_or_a_list(string query)
    {
        using var viewer = await StaffAsync(Permissions.Inventory.View);

        Assert.Equal(HttpStatusCode.BadRequest, (await GetAsync(viewer, $"{ListUrl}?{query}")).Status);
    }

    [Fact]
    public async Task The_list_accepts_its_names_in_any_case()
    {
        using var viewer = await StaffAsync(Permissions.Inventory.View);

        Assert.Equal(HttpStatusCode.OK, (await GetAsync(viewer, $"{ListUrl}?status=lowstock&sort=SKU&direction=desc")).Status);
    }

    /// <summary>
    /// D3: a variant of a removed product cannot be changed. The stock change answers NotFound and writes no
    /// stock figure, no movement and no audit row — the refusal and the write are one statement.
    /// </summary>
    [Fact]
    public async Task A_stock_change_to_a_removed_product_s_variant_is_NotFound_and_writes_nothing()
    {
        var variantId = await CheckoutVariantIdAsync();
        await ArrangeAsync(variantId, onHand: 10);
        using var manager = await StaffAsync(Permissions.Inventory.View, Permissions.Inventory.Edit);
        await SetProductDeletedAsync(variantId, true);
        var mark = await MovementMarkAsync();
        var audit = await AuditMarkAsync();

        try
        {
            var result = await AdjustAsync(manager, "Delivery", Row(variantId, "Receive", 5));
            var count = await AdjustAsync(manager, "Stock-take", Row(variantId, "SetCount", 3, expectedOnHand: 10));

            Assert.Equal("NotFound", result.GetProperty("rows")[0].GetProperty("outcome").GetString());
            Assert.Equal("NotFound", count.GetProperty("rows")[0].GetProperty("outcome").GetString());
            Assert.Equal(10, (await StockAsync(variantId)).OnHand);
            Assert.Empty(await MovementsAfterAsync(mark, variantId));
            Assert.Empty(await StockAuditAfterAsync(audit));

            Assert.Equal(HttpStatusCode.NotFound, (await PutAsync(manager, $"{ListUrl}/{variantId}/threshold", new { lowStockThreshold = 7 })).Status);
            Assert.Null(await OwnThresholdAsync(variantId));
            Assert.Empty(await StockAuditAfterAsync(audit));
            Assert.Equal(HttpStatusCode.NotFound, (await GetAsync(manager, $"{ListUrl}/{variantId}/movements")).Status);
        }
        finally
        {
            await SetProductDeletedAsync(variantId, false);
        }
    }

    private async Task SetProductDeletedAsync(int variantId, bool deleted)
    {
        using var scope = CreateScope();
        await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.ExecuteSqlRawAsync(
            "UPDATE catalog.Products SET IsDeleted = {0} WHERE Id = (SELECT ProductId FROM catalog.ProductVariants WHERE Id = {1});",
            [deleted, variantId],
            CancellationToken.None);
    }

    [Fact]
    public async Task The_history_of_an_unknown_variant_is_404()
    {
        using var viewer = await StaffAsync(Permissions.Inventory.View);

        Assert.Equal(HttpStatusCode.NotFound, (await GetAsync(viewer, $"{ListUrl}/{int.MaxValue}/movements")).Status);
    }
}
