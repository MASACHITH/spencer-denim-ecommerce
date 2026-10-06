using SpencerDenim.Domain.Sales;

namespace SpencerDenim.Domain.Reference;

/// <summary>
/// <c>ref.Districts</c> (doc 08 §3.1, GLB-06 / CHK-01 / ADM-08). The twenty-five districts a Sri Lankan
/// delivery address names, and the bridge between an address and a delivery rate.
/// <para>
/// <b>Why this table is in a checkout task at all.</b> Doc 08 §3.1 heads it "GLB-06, CHK-01, ADM-08", and
/// CHK-01 is "delivery address (district)". <c>sales.Orders.DeliveryDistrictId</c> is <c>NOT NULL</c> and
/// <c>Orders.DeliveryZone</c> is derived from <see cref="DeliveryZone"/> here, so an order cannot be
/// written at all until these rows exist. W3-03 left the two columns constrained by nothing on purpose
/// (its journal, decision 1) precisely so that the task which created the table would add the
/// constraints; this is that task.
/// </para>
/// <para>
/// <b>No audit columns, and that is doc 08's choice rather than an omission.</b> §3.1's heading for this
/// table carries no <c>[A]</c> marker and its column list has no audit group, so the table is not
/// audited; §1.5 applies those four columns to <c>[A]</c> tables only. The one field an admin may change
/// here (<see cref="DeliveryZone"/>) is a <c>Settings.Edit</c> action, and doc 08 §3.8 records settings
/// changes in <c>AuditLogs</c> with their values — which is a stronger record than two stamp columns,
/// and the same reading <c>identity.Roles</c> already gets.
/// </para>
/// <para>
/// <b>It is reference data with one admin-editable field.</b> <see cref="DeliveryZone"/> is reassignable
/// under <c>Settings.Edit</c> (doc 08 §3.1) because the client has not confirmed the zone map (D8), and
/// <see cref="SortOrder"/> and <see cref="IsActive"/> are presentation. The code and the name are not
/// editable: they are the country's, not the shop's. Nothing in this task builds a screen for any of it —
/// see the task report for what ADM-22 is owed.
/// </para>
/// <para>
/// <b>There is deliberately no foreign key from here to
/// <c>sales.DeliveryMethodZoneRates</c></b>, and doc 08 §2.5 draws that edge dashed for the same reason:
/// the two tables share the <see cref="Sales.DeliveryZone"/> value, not a key. A rate row is keyed
/// (method, zone), so a district joins to it on the zone it currently carries, and a missing row means
/// "that method is unavailable here" rather than a broken reference.
/// </para>
/// </summary>
public sealed class District
{
    /// <summary>Doc 08 §3.1: <c>varchar(6)</c>, e.g. <c>LK-11</c>.</summary>
    public const int MaxCodeLength = 6;

    public const int MaxNameLength = 60;

    /// <summary>PK, seeded 1–25 with no identity (doc 08 §3.1).</summary>
    public int Id { get; set; }

    public int ProvinceId { get; set; }

    public Province? Province { get; set; }

    /// <summary>Unique. ISO 3166-2, e.g. <c>LK-11</c> for Colombo.</summary>
    public string Code { get; set; } = string.Empty;

    /// <summary>Unique (doc 08 §3.1 marks both <c>Code</c> and <c>Name</c> UX).</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Which rate column of <c>sales.DeliveryMethodZoneRates</c> an address here is charged from (N4).
    /// Admin-editable, and <b>snapshotted onto the order at placement</b> so that reassigning a district
    /// never rewrites what a past order was charged.
    /// </summary>
    public DeliveryZone DeliveryZone { get; set; }

    public short SortOrder { get; set; }

    /// <summary>
    /// Doc 08 §3.1, default 1. A deactivated district is not offered at checkout. It is not deleted,
    /// because orders snapshot the id and the name and must keep reading correctly for ever.
    /// </summary>
    public bool IsActive { get; set; } = true;
}
