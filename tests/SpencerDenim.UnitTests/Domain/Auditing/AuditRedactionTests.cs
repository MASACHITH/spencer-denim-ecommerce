using System.Text.Json;
using SpencerDenim.Domain.Auditing;

namespace SpencerDenim.UnitTests.Domain.Auditing;

/// <summary>
/// Doc 08 DB24's redaction list, applied to every audit row at the writer (#50): whatever a use case puts in
/// <c>BeforeJson</c> / <c>AfterJson</c>, no credential reaches <c>ops.AuditLogs</c>.
/// </summary>
public sealed class AuditRedactionTests
{
    /// <summary>The list asserted, as DB24 asks of its model test: a change to it is a change somebody has to look at.</summary>
    [Fact]
    public void The_list_is_doc_08_DB24s_plus_the_credentials_a_hand_written_row_could_copy()
    {
        Assert.Equal(
            [
                "AccessFailedCount", "AuthenticatorKey", "ClientSecret", "ConcurrencyStamp", "CurrentPassword",
                "InitialPassword", "NewPassword", "Password", "PasswordHash", "RecoveryCode", "RecoveryCodes",
                "SecurityStamp", "TemporaryPassword", "TransportOverrideProtected",
            ],
            AuditRedaction.NeverLogged.Order(StringComparer.Ordinal));
        Assert.Equal(["ProofAssetId"], AuditRedaction.LoggedAsChanged);
    }

    [Theory]
    [InlineData("""{"securityStamp":"ABC","isActive":true}""")]
    [InlineData("""{"SecurityStamp":"ABC","isActive":true}""")]
    [InlineData("""{"SECURITYSTAMP":"ABC","isActive":true}""")]
    public void A_never_logged_property_is_removed_whatever_its_casing(string json)
    {
        var redacted = AuditRedaction.Redact(json)!;

        Assert.DoesNotContain("ABC", redacted, StringComparison.Ordinal);
        Assert.True(JsonDocument.Parse(redacted).RootElement.GetProperty("isActive").GetBoolean());
    }

    [Fact]
    public void Nested_objects_and_arrays_are_redacted_too()
    {
        var json = """{"user":{"passwordHash":"H","name":"N"},"rows":[{"accessFailedCount":3,"id":1},{"recoveryCodes":["a","b"]}]}""";

        var redacted = AuditRedaction.Redact(json)!;

        Assert.DoesNotContain("passwordHash", redacted, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("accessFailedCount", redacted, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("recoveryCodes", redacted, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"name\":\"N\"", redacted, StringComparison.Ordinal);
        Assert.Contains("\"id\":1", redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void A_proof_asset_id_is_written_as_changed()
    {
        var redacted = AuditRedaction.Redact("""{"proofAssetId":"7c1e7c9e-1111-2222-3333-444444444444"}""")!;

        Assert.Equal("[changed]", JsonDocument.Parse(redacted).RootElement.GetProperty("proofAssetId").GetString());
    }

    [Fact]
    public void A_name_that_only_contains_a_listed_word_is_left_alone()
    {
        // MustChangePassword is a flag, not a password; matching on substrings would hide what the audit row is for.
        const string json = """{"mustChangePassword":true,"passwordChanged":true}""";

        Assert.Same(json, AuditRedaction.Redact(json));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json at all")]
    public void Nothing_or_not_JSON_is_left_as_written(string? json)
    {
        Assert.Same(json, AuditRedaction.Redact(json));
    }

    [Fact]
    public void A_clean_row_is_the_same_instance()
    {
        var row = new AuditLog { Action = "Update", EntityType = "User", AfterJson = """{"isActive":false}""" };

        Assert.Same(row, AuditRedaction.Apply(row));
    }

    [Fact]
    public void A_dirty_row_is_copied_field_for_field_with_its_JSON_redacted()
    {
        var row = new AuditLog
        {
            OccurredAtUtc = new DateTime(2026, 11, 6, 9, 0, 0, DateTimeKind.Utc),
            UserId = 7,
            UserEmail = "admin@example.com",
            Action = "Update",
            EntityType = "User",
            EntityId = "42",
            BeforeJson = """{"securityStamp":"OLD"}""",
            AfterJson = """{"securityStamp":"NEW","firstName":"Nimal"}""",
            IpAddress = "203.0.113.1",
            UserAgent = "Browser",
            CorrelationId = "corr",
        };

        var written = AuditRedaction.Apply(row);

        Assert.NotSame(row, written);
        Assert.Equal(
            (row.OccurredAtUtc, row.UserId, row.UserEmail, row.Action, row.EntityType, row.EntityId, row.IpAddress, row.UserAgent, row.CorrelationId),
            (written.OccurredAtUtc, written.UserId, written.UserEmail, written.Action, written.EntityType, written.EntityId, written.IpAddress, written.UserAgent, written.CorrelationId));
        Assert.Equal("{}", written.BeforeJson);
        Assert.Equal("""{"firstName":"Nimal"}""", written.AfterJson);
    }

    /// <summary>
    /// The copy must carry every column the row has, or a column added to <see cref="AuditLog"/> later would be silently
    /// dropped from exactly the rows that needed redacting. Every settable column is filled by reflection, so a new one
    /// is covered without editing this test.
    /// </summary>
    [Fact]
    public void Apply_copies_every_settable_column_of_the_row()
    {
        var row = new AuditLog { Action = "A", EntityType = "E" };
        foreach (var property in typeof(AuditLog).GetProperties().Where(p => p.CanWrite && p.Name != nameof(AuditLog.Id)))
        {
            object value = property.PropertyType == typeof(string) ? (object)(property.Name + "-v")
                : property.PropertyType == typeof(DateTime) ? (object)new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc)
                : property.PropertyType == typeof(int?) || property.PropertyType == typeof(int) ? (object)5
                : property.PropertyType == typeof(long?) || property.PropertyType == typeof(long) ? (object)5L
                : throw new InvalidOperationException(property.PropertyType.Name);
            property.SetValue(row, value);
        }

        typeof(AuditLog).GetProperty(nameof(AuditLog.AfterJson))!.SetValue(row, """{"password":"x","k":1}""");
        typeof(AuditLog).GetProperty(nameof(AuditLog.BeforeJson))!.SetValue(row, """{"k":0}""");

        var written = AuditRedaction.Apply(row);
        Assert.NotSame(row, written);
        Assert.Equal("""{"k":1}""", written.AfterJson);
        foreach (var property in typeof(AuditLog).GetProperties().Where(p => p.CanWrite && p.Name is not nameof(AuditLog.Id) and not nameof(AuditLog.AfterJson)))
        {
            Assert.Equal(property.GetValue(row), property.GetValue(written));
        }
    }
}
