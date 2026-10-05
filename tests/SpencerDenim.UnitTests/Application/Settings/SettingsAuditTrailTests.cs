using System.Text.Json;
using SpencerDenim.Application.Abstractions.Auditing;
using SpencerDenim.Application.Settings;
using SpencerDenim.Domain.Auditing;
using SpencerDenim.Domain.Settings;

namespace SpencerDenim.UnitTests.Application.Settings;

/// <summary>
/// The ADM-22 audit trail (<see cref="SettingsAuditTrail"/>), doc 08 §3.8.
/// <para>
/// The redaction tests matter more than they look: <c>ops.AuditLogs</c> is retained for three years and has
/// no delete path, so a secret that reaches it is a secret in every backup from then on. The guard is built
/// and tested now, while there is nothing to lose by getting it wrong.
/// </para>
/// </summary>
public sealed class SettingsAuditTrailTests
{
    private static readonly DateTime When = new(2026, 10, 2, 11, 30, 0, DateTimeKind.Utc);

    private static readonly AdminActor Actor = new(7, "staff@example.com", "203.0.113.9", "Firefox", "corr-1");

    [Fact]
    public void A_changed_value_produces_one_row_naming_the_key()
    {
        var row = Row(SettingKeys.Social.InstagramUrl, before: null);

        var audit = SettingsAuditTrail.ForValueChange(row, "https://www.instagram.com/shop", Actor, When);

        Assert.NotNull(audit);
        Assert.Equal(AuditActions.SettingChange, audit.Action);
        Assert.Equal("Setting", audit.EntityType);

        // The key, not the group: the viewer filters by EntityId, and a row named "Social" could not be
        // searched for and its AfterJson could not be read against a single key.
        Assert.Equal(SettingKeys.Social.InstagramUrl, audit.EntityId);

        Assert.Equal(When, audit.OccurredAtUtc);
        Assert.Equal(7, audit.UserId);
        Assert.Equal("staff@example.com", audit.UserEmail);
        Assert.Equal("203.0.113.9", audit.IpAddress);
        Assert.Equal("Firefox", audit.UserAgent);
        Assert.Equal("corr-1", audit.CorrelationId);
    }

    [Fact]
    public void Both_the_old_and_the_new_value_are_recorded_for_a_non_secret()
    {
        var row = Row(SettingKeys.Social.FacebookUrl, before: "https://www.facebook.com/old");

        var audit = SettingsAuditTrail.ForValueChange(row, "https://www.facebook.com/new", Actor, When)!;

        Assert.Equal("https://www.facebook.com/old", ValueIn(audit.BeforeJson));
        Assert.Equal("https://www.facebook.com/new", ValueIn(audit.AfterJson));
    }

    /// <summary>
    /// The logo case: the stored value is an asset id, and both ends of the change are in the row — which is
    /// what makes "who replaced the logo, and with what" answerable months later.
    /// </summary>
    [Fact]
    public void A_logo_replacement_records_both_asset_ids()
    {
        var row = Row(SettingKeys.Store.LogoAssetId, before: "41", type: SettingValueType.MediaAssetRef);

        var audit = SettingsAuditTrail.ForValueChange(row, "57", Actor, When)!;

        Assert.Equal("41", ValueIn(audit.BeforeJson));
        Assert.Equal("57", ValueIn(audit.AfterJson));
    }

    [Fact]
    public void Removing_the_logo_records_the_old_id_and_a_null()
    {
        var row = Row(SettingKeys.Store.LogoAssetId, before: "41", type: SettingValueType.MediaAssetRef);

        var audit = SettingsAuditTrail.ForValueChange(row, null, Actor, When)!;

        Assert.Equal("41", ValueIn(audit.BeforeJson));
        Assert.Null(ValueIn(audit.AfterJson));
    }

    /// <summary>
    /// No row for a save that changed nothing. A trail full of "changed nothing" entries is how an audit log
    /// stops being read — and the handlers use the null to decide there is nothing to save either, so this is
    /// also what stops an untouched form moving every row version on the screen.
    /// </summary>
    [Theory]
    [InlineData(null, null)]
    [InlineData("https://x.example/", "https://x.example/")]
    [InlineData("41", "41")]
    public void An_unchanged_value_produces_no_row(string? before, string? after)
    {
        Assert.Null(SettingsAuditTrail.ForValueChange(Row("Social.FacebookUrl", before), after, Actor, When));
    }

    /// <summary>
    /// Ordinal comparison, not the database's case-insensitive collation: a client correcting the case of
    /// their own Instagram handle has changed something, and the trail must say so.
    /// </summary>
    [Fact]
    public void A_change_of_case_only_is_still_a_change()
    {
        var row = Row(SettingKeys.Social.InstagramUrl, before: "https://instagram.com/ColomboWardrobe");

        Assert.NotNull(SettingsAuditTrail.ForValueChange(
            row, "https://instagram.com/colombowardrobe", Actor, When));
    }

    // ---- the redaction ---------------------------------------------------------------------------

    /// <summary>
    /// Doc 08 §3.8's redaction list: "Logged as <c>[secret changed]</c> — <c>Settings.Value</c> where
    /// IsSecret". Neither end of the change may carry the value.
    /// </summary>
    [Fact]
    public void A_secret_value_never_reaches_the_audit_row()
    {
        var row = Row("Payments.PayPal.ClientSecret", before: "old-ciphertext", type: SettingValueType.Secret);

        var audit = SettingsAuditTrail.ForValueChange(row, "new-ciphertext", Actor, When)!;

        Assert.Equal(SettingsAuditTrail.RedactedSecret, ValueIn(audit.BeforeJson));
        Assert.Equal(SettingsAuditTrail.RedactedSecret, ValueIn(audit.AfterJson));

        // Belt as well as braces: neither string appears anywhere in the serialised row, including in a
        // field nobody thought to check.
        Assert.DoesNotContain("old-ciphertext", audit.BeforeJson!, StringComparison.Ordinal);
        Assert.DoesNotContain("new-ciphertext", audit.AfterJson!, StringComparison.Ordinal);
    }

    /// <summary>
    /// A secret being <i>set</i> for the first time, and being <i>cleared</i>. Null stays null rather than
    /// becoming "[secret changed]", so the trail still distinguishes "a password was configured" from "a
    /// password was removed" — which is a security-relevant difference and leaks nothing.
    /// </summary>
    [Fact]
    public void Setting_and_clearing_a_secret_are_distinguishable_without_the_value()
    {
        var unset = Row("Email.Smtp.Password", before: null, type: SettingValueType.Secret);
        var set = Row("Email.Smtp.Password", before: "ciphertext", type: SettingValueType.Secret);

        var configured = SettingsAuditTrail.ForValueChange(unset, "ciphertext", Actor, When)!;
        var cleared = SettingsAuditTrail.ForValueChange(set, null, Actor, When)!;

        Assert.Null(ValueIn(configured.BeforeJson));
        Assert.Equal(SettingsAuditTrail.RedactedSecret, ValueIn(configured.AfterJson));

        Assert.Equal(SettingsAuditTrail.RedactedSecret, ValueIn(cleared.BeforeJson));
        Assert.Null(ValueIn(cleared.AfterJson));
    }

    /// <summary>
    /// The redaction is driven by the row's own flag, which is the property that makes it unforgettable: a
    /// handler supplies no flag and so cannot opt out of it.
    /// </summary>
    [Fact]
    public void The_redaction_follows_the_rows_own_flag_not_the_callers_intent()
    {
        var secret = Row("Anything.AtAll", before: "plain", type: SettingValueType.Secret);

        Assert.Equal(SettingsAuditTrail.RedactedSecret, ValueIn(
            SettingsAuditTrail.ForValueChange(secret, "other", Actor, When)!.AfterJson));
    }

    /// <summary>
    /// <c>CK_AuditLogs_BeforeJson</c> / <c>AfterJson</c> require <c>ISJSON = 1</c>, so a bare string would
    /// make the insert fail — and take the settings change with it, since the two share a transaction.
    /// </summary>
    [Fact]
    public void Both_json_columns_are_json_objects()
    {
        var audit = SettingsAuditTrail.ForValueChange(
            Row("Social.TikTokUrl", before: "a"), "b", Actor, When)!;

        Assert.Equal(JsonValueKind.Object, JsonDocument.Parse(audit.BeforeJson!).RootElement.ValueKind);
        Assert.Equal(JsonValueKind.Object, JsonDocument.Parse(audit.AfterJson!).RootElement.ValueKind);
    }

    [Fact]
    public void An_actor_nothing_is_known_about_is_recorded_as_such_rather_than_refused()
    {
        // A CLI verb or a seeder has no user. Doc 08 §1.5 allows the audit columns to be NULL.
        var audit = SettingsAuditTrail.ForValueChange(
            Row("Social.TikTokUrl", before: null), "https://x.example/", AdminActor.Unknown, When)!;

        Assert.Null(audit.UserId);
        Assert.Null(audit.UserEmail);
        Assert.Null(audit.IpAddress);
    }

    private static Setting Row(string key, string? before, SettingValueType type = SettingValueType.String) =>
        new()
        {
            Key = key,
            GroupKey = key.Split('.')[0],
            ValueType = type,
            Value = before,
            IsSecret = type == SettingValueType.Secret,
            RequiredPermission = "Settings.Edit",
        };

    private static string? ValueIn(string? json)
    {
        var value = JsonDocument.Parse(json!).RootElement.GetProperty("value");

        return value.ValueKind == JsonValueKind.Null ? null : value.GetString();
    }
}
