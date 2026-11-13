using System.Text.Json;
using SpencerDenim.Domain.Auditing;

namespace SpencerDenim.UnitTests.Domain.Auditing;

/// <summary>Edge cases in <see cref="AuditRedaction.Redact"/> found by the independent tester (#50).</summary>
public sealed class AuditRedactionEdgeCaseTests
{
    private static bool IsValidJson(string? s)
    {
        if (s is null) return true;
        try { using var _ = JsonDocument.Parse(s, new JsonDocumentOptions { MaxDepth = 512 }); return true; }
        catch (JsonException) { return false; }
    }

    [Fact]
    public void proof_null_stays_null()
    {
        const string json = """{"proofAssetId":null,"x":1}""";
        Assert.Same(json, AuditRedaction.Redact(json));
    }

    [Fact]
    public void proof_number_becomes_changed()
    {
        var r = AuditRedaction.Redact("""{"ProofAssetId":12}""")!;
        Assert.Equal("[changed]", JsonDocument.Parse(r).RootElement.GetProperty("ProofAssetId").GetString());
    }

    [Fact]
    public void proof_already_changed_is_same_instance()
    {
        const string json = """{"proofAssetId":"[changed]"}""";
        Assert.Same(json, AuditRedaction.Redact(json));
    }

    [Fact]
    public void root_array_of_objects()
    {
        var r = AuditRedaction.Redact("""[{"password":"p1","a":1},[{"Password":"p2"}]]""")!;
        Assert.DoesNotContain("p1", r);
        Assert.DoesNotContain("p2", r);
        Assert.True(IsValidJson(r));
    }

    [Theory]
    [InlineData("\"password\"")]
    [InlineData("42")]
    [InlineData("null")]
    [InlineData("true")]
    public void non_object_roots_untouched(string json)
    {
        Assert.Same(json, AuditRedaction.Redact(json));
    }

    [Fact]
    public void bom_prefixed()
    {
        var r = AuditRedaction.Redact("\uFEFF{\"password\":\"SECRET\"}")!;
        Assert.DoesNotContain("SECRET", r);
        Assert.True(IsValidJson(r));
    }

    [Fact]
    public void duplicate_listed_keys_fail_closed()
    {
        var r = AuditRedaction.Redact("""{"password":"S1","password":"S2"}""")!;
        Assert.DoesNotContain("S1", r);
        Assert.DoesNotContain("S2", r);
        Assert.True(IsValidJson(r));
    }

    [Fact]
    public void duplicate_unlisted_keys_with_nested_secret()
    {
        var r = AuditRedaction.Redact("""{"a":1,"a":2,"u":{"passwordHash":"SECRET"}}""")!;
        Assert.DoesNotContain("SECRET", r);
    }

    [Fact]
    public void escaped_listed_name_parsed_is_removed()
    {
        var r = AuditRedaction.Redact("""{"pass\u0077ord":"SECRET","x":1}""")!;
        Assert.DoesNotContain("SECRET", r);
    }

    /// <summary>Unreadable text (duplicate key) whose listed name is spelled with a JSON escape: the substring check misses it.</summary>
    [Fact]
    public void escaped_listed_name_in_unreadable_json_fails_closed()
    {
        var r = AuditRedaction.Redact("""{"pass\u0077ord":"SECRET","pass\u0077ord":"SECRET2"}""")!;
        Assert.DoesNotContain("SECRET", r);
    }

    [Theory]
    [InlineData(70)]
    [InlineData(100)]
    [InlineData(127)]
    [InlineData(200)]
    [InlineData(300)]
    public void deep_nesting_with_secret_at_bottom(int depth)
    {
        var json = string.Concat(Enumerable.Repeat("{\"a\":", depth)) + "{\"password\":\"SECRET\"}" + new string('}', depth);
        var r = AuditRedaction.Redact(json)!;
        Assert.DoesNotContain("SECRET", r);
        Assert.True(IsValidJson(r));
    }

    [Theory]
    [InlineData(70)]
    [InlineData(300)]
    public void deep_nesting_clean_untouched(int depth)
    {
        var json = string.Concat(Enumerable.Repeat("{\"a\":", depth)) + "1" + new string('}', depth);
        Assert.Same(json, AuditRedaction.Redact(json));
    }

    [Fact]
    public void deep_arrays_with_secret()
    {
        const int depth = 100;
        var json = new string('[', depth) + "{\"password\":\"SECRET\"}" + new string(']', depth);
        var r = AuditRedaction.Redact(json)!;
        Assert.DoesNotContain("SECRET", r);
        Assert.True(IsValidJson(r));
    }

    [Theory]
    [InlineData("""{"passwords":"keep"}""")]
    [InlineData("""{"xPassword":"keep"}""")]
    [InlineData("""{"password_":"keep"}""")]
    [InlineData("""{" password":"keep"}""")]
    [InlineData("""{"recoveryCodesLeft":3}""")]
    [InlineData("""{"mustChangePassword":true}""")]
    public void prefix_suffix_names_survive(string json)
    {
        Assert.Same(json, AuditRedaction.Redact(json));
    }

    [Fact]
    public void listed_name_as_value_survives()
    {
        const string json = """{"field":"password","reason":"WrongPassword"}""";
        Assert.Same(json, AuditRedaction.Redact(json));
    }

    [Fact]
    public void comments_and_trailing_commas_do_not_throw()
    {
        var r = AuditRedaction.Redact("{\"password\":\"S\", /* c */ }");
        Assert.NotNull(r);
        Assert.DoesNotContain("\"S\"", r!);
    }

    [Fact]
    public void lone_surrogate_does_not_throw()
    {
        var r = AuditRedaction.Redact("{\"password\":\"S\",\"n\":\"\\uD800\"}");
        Assert.NotNull(r);
        Assert.DoesNotContain("\"S\"", r!);
    }

    [Fact]
    public void unicode_other_values_preserved_after_redaction()
    {
        var r = AuditRedaction.Redact("""{"password":"S","name":"ශ්‍රී <b>&'"}""")!;
        Assert.Equal("ශ්‍රී <b>&'", JsonDocument.Parse(r).RootElement.GetProperty("name").GetString());
    }

    [Fact]
    public void huge_number_precision_preserved()
    {
        var r = AuditRedaction.Redact("""{"password":"S","n":123456789012345678901234567890.123456789}""")!;
        Assert.Contains("123456789012345678901234567890.123456789", r);
    }

    [Fact]
    public void whitespace_only_unchanged()
    {
        Assert.Same("   ", AuditRedaction.Redact("   "));
    }
}
