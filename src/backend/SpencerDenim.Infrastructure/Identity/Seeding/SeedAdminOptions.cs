namespace SpencerDenim.Infrastructure.Identity.Seeding;

/// <summary>
/// Bootstraps exactly one initial Super Admin (doc 08 §3.1: "at least two Super Admin accounts recommended,
/// created at handover"; this seeds the first one so there is any way in at all before Users &amp; Roles
/// management exists). Bound from environment variables only — never a literal password in code or appsettings
/// (CLAUDE.md, doc 03 §4 "Secrets"):
/// <list type="bullet">
/// <item><description><c>Seed__Admin__Enabled</c> — must be explicitly <c>true</c>. Default <c>false</c>, so a
/// production deployment never seeds an admin unless someone deliberately turns this on for the first boot.</description></item>
/// <item><description><c>Seed__Admin__Email</c>, <c>Seed__Admin__Password</c> — required when enabled. The
/// password has to satisfy <see cref="SpencerDenim.Domain.Identity.PasswordPolicy"/> (at least 10 characters with an
/// upper-case letter, a lower-case letter, a number and a symbol), the same as every other account. A shorter one
/// creates no account: the host still starts, and an error naming the rule is logged (issue #200).</description></item>
/// <item><description><c>Seed__Admin__FirstName</c>, <c>Seed__Admin__LastName</c> — optional, default "Super"/"Admin".</description></item>
/// </list>
/// Doc 03 §4: on the server these live in the bootstrap <c>.env</c> file, never committed.
/// <see cref="Seeding.AdminSeedHostedService"/> is additionally idempotent (it only ever acts when no active
/// GrantsAll holder exists yet), so leaving the flag on after the first boot is harmless but should still be
/// turned off once the real Super Admin(s) are created.
/// </summary>
public sealed class SeedAdminOptions
{
    public const string SectionName = "Seed:Admin";

    public bool Enabled { get; set; }

    public string? Email { get; set; }

    public string? Password { get; set; }

    public string FirstName { get; set; } = "Super";

    public string LastName { get; set; } = "Admin";
}
