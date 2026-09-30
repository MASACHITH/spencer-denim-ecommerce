namespace SpencerDenim.Application.Admin.Users.Abstractions;

/// <summary>
/// The acting user's authorization snapshot at the time a Users &amp; Roles command handler made its
/// allow/deny decision (doc 08 §3.1 DB4 rules 1/2), re-supplied to <see cref="IStaffUserRoleManagementStore"/>
/// so the store can re-run that same decision against the TARGET's fresh state inside the same transaction as
/// the write (tester-round-1 fix, W1-08, #8, defect 2).
/// <para>
/// Before this fix, the handler authorized against a snapshot read outside any transaction, then the store
/// re-read the target's role state independently and wrote with no check tying the write back to what was
/// authorized — a concurrent change to the same target between the two reads could be silently clobbered
/// (lost update), and the write could proceed even though the true state at write time no longer satisfied
/// the subset rule. The store now re-validates using this context immediately before writing; see
/// <c>StaffUserRoleManagementStore</c>'s per-target <c>sp_getapplock</c> for how it closes the race, not just
/// detects it after the fact.
/// </para>
/// </summary>
public sealed record RbacActorContext(bool HasGrantsAll, bool HasUsersManage, IReadOnlySet<string> EffectivePermissions);
